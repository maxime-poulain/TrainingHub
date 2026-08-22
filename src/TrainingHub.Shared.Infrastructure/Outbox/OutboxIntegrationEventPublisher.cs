using System.Diagnostics;
using TrainingHub.Shared.Application.IntegrationEvents;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Outbox;

namespace TrainingHub.Shared.Infrastructure.Outbox;

/// <summary>
/// The transactional outbox: publishes an integration event by staging its envelope in the same
/// <see cref="TrainingContext"/> the business change is being saved through.
/// </summary>
/// <remarks>
/// This is the whole trick, and it is deliberately small. Domain events are dispatched by the
/// interceptor during <c>SavingChangesAsync</c>, before anything is written — so a row this class
/// adds while a handler runs is swept into the very save that is in flight, exactly as the cascade
/// delete's staged deletions are. The fact and the state change that justified it become one atomic
/// commit; a save that fails takes its messages down with it. Nothing here opens a transaction,
/// because the ambient one is the point.
/// </remarks>
public sealed class OutboxIntegrationEventPublisher(TrainingContext trainingContext, TimeProvider timeProvider)
    : IIntegrationEventPublisher
{
    /// <summary>
    /// Stages the event's envelope in the outbox table, atomically with the unit of work in
    /// progress.
    /// </summary>
    /// <param name="integrationEvent">The fact to publish.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    public Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        var (name, version) = IntegrationEventTypes.NameOf(integrationEvent.GetType());

        // A version-7 GUID: minted here rather than by the database because it is the delivery
        // ledger's deduplication key, and time-ordered so the clustered key follows insertion order.
        // The current activity's identifier — the W3C traceparent, since the host runs under that
        // format — rides along, so the delivery can link its own trace back to the operation that
        // committed the fact. ASP.NET Core opens an activity for every request whether or not
        // telemetry is on, which is why the stamp is not gated on anything (ADR 0097).
        var message = new OutboxMessage(
            Guid.CreateVersion7(),
            name,
            version,
            IntegrationEventSerializer.Serialize(integrationEvent),
            timeProvider.GetUtcNow().UtcDateTime,
            Activity.Current?.Id);

        trainingContext.Set<OutboxMessage>().Add(message);

        // Adding to the change tracker is synchronous; the port stays asynchronous because
        // publishing is not promised to be a local write — that is this implementation's secret.
        return Task.CompletedTask;
    }
}
