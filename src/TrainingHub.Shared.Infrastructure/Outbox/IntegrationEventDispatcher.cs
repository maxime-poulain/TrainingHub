using System.Diagnostics;
using TrainingHub.Shared.Application.IntegrationEvents;

namespace TrainingHub.Shared.Infrastructure.Outbox;

/// <summary>
/// Routes a delivered integration event to every <see cref="IIntegrationEventHandler{TEvent}"/>
/// registered for it.
/// </summary>
/// <remarks>
/// A switch over the registered event types, fed by constructor injection, on purpose twice over.
/// The switch: reflection over <c>MakeGenericType</c> — or reusing the messaging library's
/// <c>Publish</c> — would route anything, and "routes anything" is how an in-process bus and a
/// post-commit outbox end up indistinguishable again (ADR 0024's rejected alternative). The
/// injection: the container already knows every consumer of every fact, so the dispatcher asks for
/// them in its constructor like any other dependency instead of going shopping with a service
/// locator. The set of integration events is closed and explicit in
/// <see cref="IntegrationEventTypes"/>; a dispatcher that lists the same events again is not
/// duplication, it is the same decision stated where the routing happens — and a unit test holds
/// the two lists together, exactly as one holds the serializer to the registry.
/// </remarks>
public sealed class IntegrationEventDispatcher(
    IEnumerable<IIntegrationEventHandler<TrainerCreatedIntegrationEvent>> trainerCreatedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainerContactEmailChangedIntegrationEvent>> contactEmailChangedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainerSuspendedIntegrationEvent>> trainerSuspendedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainerReinstatedIntegrationEvent>> trainerReinstatedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainerContactedIntegrationEvent>> trainerContactedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingCreatedIntegrationEvent>> trainingCreatedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingEditedIntegrationEvent>> trainingEditedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingTransferredIntegrationEvent>> trainingTransferredConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingPublishedIntegrationEvent>> trainingPublishedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingUnpublishedIntegrationEvent>> trainingUnpublishedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingWithheldIntegrationEvent>> trainingWithheldConsumers,
    IEnumerable<IIntegrationEventHandler<TrainingDeletedIntegrationEvent>> trainingDeletedConsumers,
    IEnumerable<IIntegrationEventHandler<TrainerDeletedIntegrationEvent>> trainerDeletedConsumers,
    IEnumerable<IIntegrationEventHandler<PasswordResetRequestedIntegrationEvent>> passwordResetRequestedConsumers,
    IEnumerable<IIntegrationEventHandler<EmailVerificationRequestedIntegrationEvent>> emailVerificationRequestedConsumers,
    IEnumerable<IIntegrationEventHandler<PasswordChangedIntegrationEvent>> passwordChangedConsumers,
    IEnumerable<IIntegrationEventHandler<AccountErasedIntegrationEvent>> accountErasedConsumers)
{
    /// <summary>
    /// Hands the fact to each of its registered consumers, in registration order, skipping the
    /// ones already delivered and isolating each from its neighbors' failures. Answers who
    /// delivered this pass and who failed (ADR 0034).
    /// </summary>
    /// <param name="integrationEvent">The fact, deserialized from its envelope.</param>
    /// <param name="alreadyDelivered">The ledger's answer: consumers this message already reached.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    public Task<DispatchOutcome> DispatchAsync(
        IIntegrationEvent integrationEvent,
        IReadOnlySet<string> alreadyDelivered,
        CancellationToken cancellationToken) =>
        integrationEvent switch
        {
            TrainerCreatedIntegrationEvent fact => HandleAllAsync(trainerCreatedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainerContactEmailChangedIntegrationEvent fact => HandleAllAsync(contactEmailChangedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainerSuspendedIntegrationEvent fact => HandleAllAsync(trainerSuspendedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainerReinstatedIntegrationEvent fact => HandleAllAsync(trainerReinstatedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainerContactedIntegrationEvent fact => HandleAllAsync(trainerContactedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingCreatedIntegrationEvent fact => HandleAllAsync(trainingCreatedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingEditedIntegrationEvent fact => HandleAllAsync(trainingEditedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingTransferredIntegrationEvent fact => HandleAllAsync(trainingTransferredConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingPublishedIntegrationEvent fact => HandleAllAsync(trainingPublishedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingUnpublishedIntegrationEvent fact => HandleAllAsync(trainingUnpublishedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingWithheldIntegrationEvent fact => HandleAllAsync(trainingWithheldConsumers, fact, alreadyDelivered, cancellationToken),
            TrainingDeletedIntegrationEvent fact => HandleAllAsync(trainingDeletedConsumers, fact, alreadyDelivered, cancellationToken),
            TrainerDeletedIntegrationEvent fact => HandleAllAsync(trainerDeletedConsumers, fact, alreadyDelivered, cancellationToken),
            PasswordResetRequestedIntegrationEvent fact => HandleAllAsync(passwordResetRequestedConsumers, fact, alreadyDelivered, cancellationToken),
            EmailVerificationRequestedIntegrationEvent fact => HandleAllAsync(emailVerificationRequestedConsumers, fact, alreadyDelivered, cancellationToken),
            PasswordChangedIntegrationEvent fact => HandleAllAsync(passwordChangedConsumers, fact, alreadyDelivered, cancellationToken),
            AccountErasedIntegrationEvent fact => HandleAllAsync(accountErasedConsumers, fact, alreadyDelivered, cancellationToken),
            _ => throw new InvalidOperationException(
                $"{integrationEvent.GetType().Name} has no route in {nameof(IntegrationEventDispatcher)}. " +
                "A new integration event is registered, serialized, consumed — and routed here."),
        };

    private static async Task<DispatchOutcome> HandleAllAsync<TEvent>(
        IEnumerable<IIntegrationEventHandler<TEvent>> consumers,
        TEvent integrationEvent,
        IReadOnlySet<string> alreadyDelivered,
        CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent
    {
        var delivered = new List<string>();
        var failures = new List<ConsumerFailure>();

        foreach (var consumer in consumers)
        {
            if (alreadyDelivered.Contains(consumer.ConsumerName))
            {
                continue;
            }

            // One span per consumer, named by the ledger identity the outcome is settled under:
            // the trace then shows exactly what the retry story is made of — which consumers
            // settled, which one failed, and what the next attempt still owes (ADR 0097).
            using var activity = OutboxTelemetry.Source.StartActivity(consumer.ConsumerName);

            try
            {
                await consumer.HandleAsync(integrationEvent, cancellationToken);
                delivered.Add(consumer.ConsumerName);
                activity?.SetTag(OutboxTelemetry.OutcomeTag, OutboxTelemetry.DeliveredOutcome);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The isolation itself: a consumer's failure is its own outcome, and its
                // neighbors still get the fact. Cancellation stays a shutdown, not an outcome —
                // it propagates whole, and the pass it aborts re-runs, which is the at-least-once
                // window the lease already implies.
                activity?.SetTag(OutboxTelemetry.OutcomeTag, OutboxTelemetry.FailedOutcome);
                activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
                activity?.AddException(exception);
                failures.Add(new ConsumerFailure(consumer.ConsumerName, exception));
            }
        }

        return new DispatchOutcome(delivered, failures);
    }
}
