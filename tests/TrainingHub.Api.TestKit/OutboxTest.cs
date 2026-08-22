using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using TrainingHub.Shared;
using TrainingHub.Shared.Application.IntegrationEvents;
using TrainingHub.Shared.Common;
using TrainingHub.Shared.Common.Results;
using TrainingHub.Shared.Domain;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate.ValueObjects;
using TrainingHub.Shared.Infrastructure.Outbox;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace TrainingHub.Api.TestKit;

/// <summary>
/// Proves the transactional half of the outbox's name: a committed change carries its integration
/// events with it, and a failed save takes them down with it.
/// </summary>
/// <remarks>
/// <para>
/// The second half is the one worth a hard look. It cannot be shown over HTTP: a stale
/// <c>If-Match</c> is refused by the version pre-check before the aggregate is ever edited, so no
/// domain event is raised and there is nothing for the outbox to lose. The case that matters is
/// the race that pre-check exists to narrow but cannot close — two readers loaded the same row,
/// both passed, the slower writer hits the <c>rowversion</c> guard inside <c>SaveChanges</c>. By
/// then the domain events have been dispatched and the outbox row staged; the save fails; ADR 0002
/// promises the row died with it. Driving the repositories through two scopes reproduces that
/// exact interleaving deterministically.
/// </para>
/// <para>
/// Read directly from <c>TrainingContext</c> rather than over HTTP because the outbox has no
/// endpoint, deliberately: its only reader will be the delivery worker.
/// </para>
/// </remarks>
/// <typeparam name="TFactory">The suite's fixture — one per host, since the wiring under test is
/// each host's own.</typeparam>
public abstract class OutboxTest<TFactory>(TFactory factory) : IntegrationTest<TFactory>(factory)
    where TFactory : IResettableDatabase, IServiceScopeSource, IHttpClientSource, IServerErrorSource, IMailboxSource
{
    /// <summary>
    /// Registering a trainer, commits the trainer-created fact, into the outbox.
    /// </summary>
    [Fact]
    public async Task RegisteringATrainer_CommitsTheTrainerCreatedFact_IntoTheOutbox()
    {
        var request = AuthHelper.CreateUniqueRegisterRequest();

        var response = await AuthHelper.RegisterAsync(Factory.CreateClient(), request);
        response.EnsureSuccessStatusCode();

        using var scope = Factory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();

        // Nothing here asserts on the delivery columns: the worker polls while this test runs, and
        // whether the row is still owed or already delivered is its business — the envelope and the
        // payload are what committing the fact promised.
        var message = (await context.Set<OutboxMessage>().ToListAsync())
            .Should().ContainSingle(m => m.Name == "TrainerCreated").Subject;

        message.Version.Should().Be(1);

        var fact = IntegrationEventSerializer.Deserialize(message.Name, message.Version, message.Payload)
            .Should().BeOfType<TrainerCreatedIntegrationEvent>().Subject;
        fact.ContactEmail.Should().Be(request.Email);
        fact.Firstname.Should().Be(request.Firstname);
        fact.Lastname.Should().Be(request.Lastname);
    }

    /// <summary>
    /// Registering a trainer, stamps the committing trace, on the envelope.
    /// </summary>
    [Fact]
    public async Task RegisteringATrainer_StampsTheCommittingTrace_OnTheEnvelope()
    {
        var request = AuthHelper.CreateUniqueRegisterRequest();

        var response = await AuthHelper.RegisterAsync(Factory.CreateClient(), request);
        response.EnsureSuccessStatusCode();

        using var scope = Factory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();

        var message = (await context.Set<OutboxMessage>().ToListAsync())
            .Should().ContainSingle(m => m.Name == "TrainerCreated").Subject;

        // ASP.NET Core opens an activity for every request whether or not anything is exporting —
        // this suite runs with the endpoint blanked — so the stamp exists here too, and it parses
        // as the W3C context the delivery's own trace will link back to (ADR 0097).
        message.TraceParent.Should().NotBeNull();
        ActivityContext.TryParse(message.TraceParent, null, out _).Should().BeTrue(
            "the envelope's trace context is what the delivery span links back to");
    }

    /// <summary>
    /// The worker, delivers the committed fact, and stamps the envelope.
    /// </summary>
    [Fact]
    public async Task TheWorker_DeliversTheCommittedFact_AndStampsTheEnvelope()
    {
        var request = AuthHelper.CreateUniqueRegisterRequest();

        var response = await AuthHelper.RegisterAsync(Factory.CreateClient(), request);
        response.EnsureSuccessStatusCode();

        var delivered = await WaitForMessageAsync(
            "TrainerCreated",
            message => message.ProcessedOnUtc is not null);

        delivered.ClaimedBy.Should().NotBeNull("a delivery starts with a claim, and the claim leaves provenance");
        delivered.Attempts.Should().Be(0, "a delivery that succeeds first time never spent the retry budget");
        delivered.Error.Should().BeNull();
    }

    /// <summary>
    /// A message nobody can read, spends its budget, and is left poisoned.
    /// </summary>
    /// <remarks>
    /// The row is planted with a wire name the registry does not know, so every delivery attempt
    /// fails at deserialization. The suite runs with an attempt budget of two: the worker must try
    /// twice, record why, then leave the row alone — and keep delivering everyone else, which the
    /// second half of the test proves with an ordinary registration.
    /// </remarks>
    [Fact]
    public async Task AMessageNobodyCanRead_SpendsItsBudget_AndIsLeftPoisoned()
    {
        var messageId = Guid.CreateVersion7();

        using (var scope = Factory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();
            context.Set<OutboxMessage>().Add(new OutboxMessage(
                messageId,
                "NobodyKnowsThisName",
                1,
                "{}",
                DateTime.UtcNow));
            await context.SaveChangesAsync();
        }

        var poisoned = await WaitForMessageAsync(
            "NobodyKnowsThisName",
            message => message.Attempts >= 2);

        poisoned.ProcessedOnUtc.Should().BeNull("a message that cannot be read is never marked delivered");
        poisoned.Error.Should().Contain("No integration event is registered");
        poisoned.NextAttemptOnUtc.Should().NotBeNull(
            "every failed attempt books the next one — the schedule was written before the budget ran out");

        // The half of this test's name that nothing used to assert. "Left alone" is a claim about
        // what happens next, so it is checked after the worker has had several more polls: the
        // counter stops at the budget, and the claim query is what has to keep it there.
        await Task.Delay(TimeSpan.FromSeconds(1));

        using (var scope = Factory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();
            var stillPoisoned = await context.Set<OutboxMessage>()
                .SingleAsync(message => message.Id == messageId);

            stillPoisoned.Attempts.Should().Be(2,
                "a spent budget is not tried again — a counter that kept climbing would mean the " +
                "claim query stopped excluding poison, and the row would be retried forever");
        }

        // The gauge ADR 0037 built over exactly this state, observed in the state it exists for.
        // Every other fact about it sees it healthy, which proves only that it does not throw.
        var readiness = await Factory.CreateClient().GetAsync("/health/ready");
        using (var report = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync()))
        {
            report.RootElement.GetProperty("status").GetString().Should().Be("Degraded",
                "poison is operator evidence that halts nothing, so readiness reports it without " +
                "taking the host out of rotation");
        }

        // The transition was announced: one Error line, naming the message, reached the host's
        // sinks — the smallest dead-letter surface ADR 0025 deferred, delivered by ADR 0033.
        Factory.ServerErrors.Should().Contain(
            error => error.Contains(messageId.ToString(), StringComparison.Ordinal),
            "poisoning is the moment the system gives up on a committed fact, and it must say so");

        // The poison did not stop the line: a fact committed afterwards still gets delivered.
        var request = AuthHelper.CreateUniqueRegisterRequest();
        (await AuthHelper.RegisterAsync(Factory.CreateClient(), request)).EnsureSuccessStatusCode();
        await WaitForMessageAsync("TrainerCreated", message => message.ProcessedOnUtc is not null);
    }

    /// <summary>
    /// A failing neighbor, does not replay a delivered consumer.
    /// </summary>
    /// <remarks>
    /// The per-consumer isolation of ADR 0034, end to end. The marked registration routes its
    /// trainer-created fact to two consumers: the production welcome email, then the test kit's
    /// <see cref="FailOnceWhenTrainerCreatedIntegrationEventHandler"/>, which throws on its first
    /// delivery. Attempt one delivers the welcome and records it in the ledger; attempt two must
    /// skip the welcome and re-run only the failed neighbor. The mailbox count is the assertion
    /// that matters: before the ledger, a replayed welcome would have passed every suite silently.
    /// </remarks>
    [Fact]
    public async Task AFailingNeighbor_DoesNotReplayADeliveredConsumer()
    {
        var request = AuthHelper.CreateUniqueRegisterRequest(
            FailOnceWhenTrainerCreatedIntegrationEventHandler.Marker);

        var response = await AuthHelper.RegisterAsync(Factory.CreateClient(), request);
        response.EnsureSuccessStatusCode();

        var delivered = await WaitForMessageAsync(
            "TrainerCreated",
            message => message.ProcessedOnUtc is not null);

        delivered.Attempts.Should().Be(1, "the failing neighbor spent exactly one attempt of the message's budget");
        delivered.Error.Should().Contain("TestKit.FailOnce",
            "the failed pass's evidence stays on the envelope, naming the consumer that owed it");

        using var scope = Factory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();

        var ledger = await context.Set<OutboxMessageConsumer>()
            .Where(delivery => delivery.MessageId == delivered.Id)
            .Select(delivery => delivery.ConsumerName)
            .ToListAsync();
        ledger.Should().BeEquivalentTo(["SendWelcomeEmail", "TestKit.FailOnce"],
            "every consumer settled exactly once, across two attempts");

        var welcomes = await CountWelcomeEmailsAsync(request.Email);
        welcomes.Should().Be(1,
            "the retry must skip the delivered welcome — a duplicate here is the replay the ledger exists to prevent");
    }

    /// <summary>
    /// A delivered message, outlives its retention, then is swept.
    /// </summary>
    /// <remarks>
    /// Three rows tell the boundary apart: one delivered long before the suite's retention window,
    /// one delivered just now, one unreadable and never delivered. The sweep must take exactly the
    /// first — delivered history is trimmed, fresh history is kept, and poison is an operator's
    /// evidence that nothing may delete (ADR 0033).
    /// </remarks>
    [Fact]
    public async Task ADeliveredMessage_OutlivesItsRetention_ThenIsSwept()
    {
        using (var scope = Factory.CreateScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();

            var stale = new OutboxMessage(
                Guid.CreateVersion7(), "DeliveredLongAgo", 1, "{}", DateTime.UtcNow.AddMinutes(-11));
            stale.MarkProcessed(DateTime.UtcNow.AddMinutes(-10));

            var fresh = new OutboxMessage(
                Guid.CreateVersion7(), "DeliveredJustNow", 1, "{}", DateTime.UtcNow);
            fresh.MarkProcessed(DateTime.UtcNow);

            context.Set<OutboxMessage>().AddRange(
                stale,
                fresh,
                new OutboxMessage(Guid.CreateVersion7(), "NobodyKnowsThisOneEither", 1, "{}", DateTime.UtcNow));

            // A ledger row on the stale delivery: the sweep's ExecuteDelete only names the
            // envelope, so this row going with it is the cascade doing its job (ADR 0034).
            context.Set<OutboxMessageConsumer>().Add(new OutboxMessageConsumer(
                stale.Id, "SweptWithItsMessage", DateTime.UtcNow.AddMinutes(-10)));

            await context.SaveChangesAsync();
        }

        await WaitUntilGoneAsync("DeliveredLongAgo");

        using var assertScope = Factory.CreateScope();
        var context2 = assertScope.ServiceProvider.GetRequiredService<TrainingContext>();
        var messages = await context2.Set<OutboxMessage>().ToListAsync();

        messages.Should().Contain(
            message => message.Name == "DeliveredJustNow",
            "a delivered message inside its retention is history an operator may still want");
        messages.Should().Contain(
            message => message.Name == "NobodyKnowsThisOneEither",
            "an undelivered message is never swept — poison waits for an operator, not for a broom");

        var orphanedLedger = await context2.Set<OutboxMessageConsumer>()
            .Where(delivery => delivery.ConsumerName == "SweptWithItsMessage")
            .ToListAsync();
        orphanedLedger.Should().BeEmpty(
            "the cascade ties the ledger to its envelope: sweeping the message takes its deliveries with it");
    }

    /// <summary>
    /// A failed save, takes its outbox row down with it.
    /// </summary>
    [Fact]
    public async Task AFailedSave_TakesItsOutboxRowDownWithIt()
    {
        var trainerId = await SeedTrainerAsync();

        // The loser of the race reads first: from here on, scope A holds a trainer whose
        // rowversion is about to go stale.
        using var scopeA = Factory.CreateScope();
        var staleTrainers = scopeA.ServiceProvider.GetRequiredService<ITrainerRepository>();
        var staleTrainer = await staleTrainers.GetByIdAsync(trainerId);
        staleTrainer.Should().NotBeNull();

        // The winner edits and commits: one TrainerContactEmailChanged fact lands with it.
        using (var scopeB = Factory.CreateScope())
        {
            var trainers = scopeB.ServiceProvider.GetRequiredService<ITrainerRepository>();
            var unitOfWork = scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var trainer = await trainers.GetByIdAsync(trainerId);
            trainer!.Edit(trainer.Name, Required(Email.Create("committed@example.com")), trainer.Bio);
            trainers.Update(trainer);

            await unitOfWork.SaveChangesAsync();
        }

        // The loser edits the stale instance. The domain event is raised, the interceptor
        // dispatches it, the handler stages a second fact — and the rowversion guard fails the
        // save underneath them all.
        staleTrainer.Edit(staleTrainer.Name, Required(Email.Create("doomed@example.com")), staleTrainer.Bio);
        staleTrainers.Update(staleTrainer);

        var losingSave = () => scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        await losingSave.Should().ThrowAsync<ConcurrencyConflictException>();

        // One fact in the table: the winner's. The loser's row died with the loser's save.
        using var assertScope = Factory.CreateScope();
        var context = assertScope.ServiceProvider.GetRequiredService<TrainingContext>();

        var message = (await context.Set<OutboxMessage>().ToListAsync())
            .Should().ContainSingle(m => m.Name == "TrainerContactEmailChanged").Subject;

        var fact = IntegrationEventSerializer.Deserialize(message.Name, message.Version, message.Payload)
            .Should().BeOfType<TrainerContactEmailChangedIntegrationEvent>().Subject;
        fact.NewContactEmail.Should().Be("committed@example.com");
    }

    private async Task<TrainerId> SeedTrainerAsync()
    {
        using var scope = Factory.CreateScope();
        var trainers = scope.ServiceProvider.GetRequiredService<ITrainerRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var trainerId = TrainerId.Generate();
        trainers.Add(Trainer.Create(
            trainerId,
            UserId.Generate(),
            Required(Name.Create("Ada", "Lovelace")),
            Required(Email.Create("ada.lovelace@example.com")),
            bio: null));

        await unitOfWork.SaveChangesAsync();
        return trainerId;
    }

    /// <summary>
    /// Polls the outbox until the single message with <paramref name="name"/> satisfies
    /// <paramref name="condition"/>, and answers it. Fails with the message's actual state when
    /// the worker does not get there in time.
    /// </summary>
    /// <remarks>
    /// Polling is the honest shape here: the worker is a real background loop on a real timer, and
    /// the alternative — hooking its internals to signal the test — would prove a different
    /// pipeline than the one production runs. Each probe uses a fresh scope so the answer comes
    /// from the database, never from a change tracker.
    /// </remarks>
    private async Task<OutboxMessage> WaitForMessageAsync(
        string name,
        Func<OutboxMessage, bool> condition)
    {
        var timeout = TimeSpan.FromSeconds(15);
        var started = DateTime.UtcNow;
        OutboxMessage? lastSeen = null;

        while (DateTime.UtcNow - started < timeout)
        {
            using (var scope = Factory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();
                lastSeen = (await context.Set<OutboxMessage>().ToListAsync())
                    .SingleOrDefault(message => message.Name == name);

                if (lastSeen is not null && condition(lastSeen))
                {
                    return lastSeen;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"The worker did not bring '{name}' to the expected state within {timeout.TotalSeconds}s. " +
            (lastSeen is null
                ? "No such message exists."
                : $"Last seen: ProcessedOnUtc={lastSeen.ProcessedOnUtc?.ToString("O") ?? "null"}, " +
                  $"Attempts={lastSeen.Attempts}, Error={lastSeen.Error ?? "null"}."));
    }

    /// <summary>
    /// Polls the outbox until no message with <paramref name="name"/> remains — the sweep's
    /// mirror of <see cref="WaitForMessageAsync"/>. Fails with the survivor's state when the
    /// worker does not sweep it in time.
    /// </summary>
    private async Task WaitUntilGoneAsync(string name)
    {
        var timeout = TimeSpan.FromSeconds(15);
        var started = DateTime.UtcNow;
        OutboxMessage? lastSeen = null;

        while (DateTime.UtcNow - started < timeout)
        {
            using (var scope = Factory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<TrainingContext>();
                lastSeen = (await context.Set<OutboxMessage>().ToListAsync())
                    .SingleOrDefault(message => message.Name == name);

                if (lastSeen is null)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException(
            $"The worker did not sweep '{name}' within {timeout.TotalSeconds}s. Last seen: " +
            $"ProcessedOnUtc={lastSeen!.ProcessedOnUtc?.ToString("O") ?? "null"}, Attempts={lastSeen.Attempts}.");
    }

    /// <summary>
    /// Counts the welcome emails the mailbox holds for one recipient. Read once, after the
    /// message is processed, because by then the count is final — there is nothing to poll for.
    /// </summary>
    private async Task<int> CountWelcomeEmailsAsync(string recipient)
    {
        using var client = new HttpClient { BaseAddress = Factory.MailboxApiBaseAddress };

        var mailbox = await client.GetFromJsonAsync<MailpitMessageList>("/api/v1/messages");

        return (mailbox?.Messages ?? []).Count(message =>
            message.Subject == "Welcome aboard!"
            && message.To.Any(address => address.Address == recipient));
    }

    /// <summary>
    /// Unwraps a result the fixture expects to succeed. A failure here is a broken test, not a
    /// failing assertion, so it throws rather than reporting.
    /// </summary>
    private static T Required<T>(Result<T> result) => result.Match(
        value => value,
        errors => throw new InvalidOperationException(
            $"The fixture built an invalid value: {string.Join("; ", errors)}"));

    // The slices of Mailpit's API this proof reads — the same minimal shape EmailTest carves for
    // itself. Property names follow the wire; everything not asserted on is left out.

    private sealed record MailpitMessageList(List<MailpitMessageSummary> Messages);

    private sealed record MailpitMessageSummary(string Subject, List<MailpitAddress> To);

    private sealed record MailpitAddress(string Address);
}
