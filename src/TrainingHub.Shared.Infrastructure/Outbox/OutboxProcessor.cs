using System.Diagnostics;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TrainingHub.Shared.Infrastructure.Outbox;

/// <summary>
/// Delivers one claimed batch of outbox messages: claim under a lease, dispatch, record the
/// outcome on each envelope.
/// </summary>
/// <remarks>
/// The claim is a single <c>UPDATE … OUTPUT</c> against the unprocessed rows, oldest first, and it
/// is what makes several workers — one per host, as ADR 0002 promised — safe against each other:
/// <c>READPAST</c> makes competing claimants skip each other's locked rows instead of queueing on
/// them, and the lease written by the claim (<c>ClaimedUntil</c>) keeps the claim owned across the
/// batch, so a worker that dies mid-delivery merely lets its lease lapse and the rows return to
/// the pool. Each message's outcome is saved as it happens, not at the end of the batch, and
/// settled per consumer: every success lands in the delivery ledger in that same save, so a crash
/// or a failing neighbor re-runs at most the consumers of the message in flight that had not yet
/// settled — the platform-side half of the deduplication ADR 0024 promised on the envelope's id
/// (ADR 0025, ADR 0034).
/// </remarks>
public sealed class OutboxProcessor(
    TrainingContext trainingContext,
    IntegrationEventDispatcher dispatcher,
    TimeProvider timeProvider,
    IOptions<OutboxOptions> options,
    ILogger<OutboxProcessor> logger)
{
    /// <summary>
    /// Claims and delivers at most one batch. Answers how many messages were claimed, so the
    /// caller knows whether the table may hold more.
    /// </summary>
    /// <param name="claimant">Who is claiming — recorded on each row as provenance.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    public async Task<int> ProcessBatchAsync(string claimant, CancellationToken cancellationToken)
    {
        var configured = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var leaseEnd = now + configured.LeaseDuration;

        // One statement claims and reads: rows come back already leased to this worker, and rows
        // another worker holds are skipped (READPAST), not waited on. Raw SQL because the claim is
        // the one query EF cannot say: UPDATE TOP … OUTPUT with locking hints.
        var claimed = await trainingContext.Set<OutboxMessage>()
            .FromSqlInterpolated($@"
WITH claimable AS (
    SELECT TOP ({configured.BatchSize}) *
    FROM OutboxMessage WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE ProcessedOnUtc IS NULL
      AND Attempts < {configured.MaxAttempts}
      AND (ClaimedUntil IS NULL OR ClaimedUntil < {now})
      AND (NextAttemptOnUtc IS NULL OR NextAttemptOnUtc < {now})
    ORDER BY OccurredOnUtc, Id
)
UPDATE claimable
SET ClaimedBy = {claimant}, ClaimedUntil = {leaseEnd}
OUTPUT inserted.*")
            .ToListAsync(cancellationToken);

        foreach (var message in claimed.OrderBy(message => message.OccurredOnUtc).ThenBy(message => message.Id))
        {
            // One delivery attempt, one root span of its own, linked to the trace that committed
            // the fact — never its child: this attempt may run minutes and retries later, and the
            // request that produced the fact has long answered (ADR 0097). Everything below,
            // consumer spans and the outcome's save included, nests under it.
            using var activity = OutboxTelemetry.StartDeliveryActivity($"Deliver {message.Name}", message.TraceParent);
            activity?.SetTag(OutboxTelemetry.FactNameTag, message.Name);

            var started = Stopwatch.GetTimestamp();
            var fullyDelivered = false;

            try
            {
                var fact = IntegrationEventSerializer.Deserialize(message.Name, message.Version, message.Payload);
                var alreadyDelivered = await DeliveredConsumersOfAsync(message, cancellationToken);
                var outcome = await dispatcher.DispatchAsync(fact, alreadyDelivered, cancellationToken);

                // Each success is written to the ledger in the same save as the message's
                // outcome, so a retry re-runs only the consumers still owed (ADR 0034).
                foreach (var consumerName in outcome.Delivered)
                {
                    trainingContext.Set<OutboxMessageConsumer>().Add(new OutboxMessageConsumer(
                        message.Id,
                        consumerName,
                        timeProvider.GetUtcNow().UtcDateTime));
                }

                if (outcome.EveryConsumerSettled)
                {
                    message.MarkProcessed(timeProvider.GetUtcNow().UtcDateTime);
                    fullyDelivered = true;
                }
                else
                {
                    RecordFailure(
                        message,
                        string.Join(Environment.NewLine, outcome.Failures
                            .Select(failure => $"{failure.ConsumerName}: {failure.Exception}")),
                        outcome.Failures.Count == 1
                            ? outcome.Failures[0].Exception
                            : new AggregateException(outcome.Failures.Select(failure => failure.Exception)),
                        configured);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // What reaches this catch never ran a consumer: a payload nobody can deserialize
                // or a fact with no route fails before the dispatch loop starts, so there is no
                // outcome to split and the whole message fails, exactly as before ADR 0034. No
                // consumer span exists to carry the cause, so the delivery span does — the one
                // place this exception appears in the trace.
                activity?.AddException(exception);
                RecordFailure(message, exception.ToString(), exception, configured);
            }

            // Saved per message rather than per batch: an outcome, once known, survives whatever
            // the next message does to this process.
            await trainingContext.SaveChangesAsync(cancellationToken);

            if (fullyDelivered)
            {
                activity?.SetTag(OutboxTelemetry.OutcomeTag, OutboxTelemetry.DeliveredOutcome);

                // The business half of the metrics: every fact, counted once under its wire name
                // at the moment its last consumer settled — which is why the counter lives here
                // and not in any handler (ADR 0096).
                OutboxTelemetry.FactsDelivered.Add(1, new KeyValuePair<string, object?>(OutboxTelemetry.FactNameTag, message.Name));
            }
            else
            {
                activity?.SetTag(OutboxTelemetry.OutcomeTag, OutboxTelemetry.FailedOutcome);
                activity?.SetStatus(ActivityStatusCode.Error, "The attempt failed; the envelope records why.");
            }

            OutboxTelemetry.DeliveryDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(OutboxTelemetry.FactNameTag, message.Name),
                new KeyValuePair<string, object?>(OutboxTelemetry.OutcomeTag, fullyDelivered ? OutboxTelemetry.DeliveredOutcome : OutboxTelemetry.FailedOutcome));
        }

        return claimed.Count;
    }

    /// <summary>
    /// The ledger's answer for one message: which consumers it already reached. A message with no
    /// history skips the query — the ledger commits in the same save as the failure that would make
    /// a second attempt exist, so a row nothing has touched has no ledger rows.
    /// </summary>
    /// <remarks>
    /// The envelope answers whether it has a history, rather than this method reading its attempt
    /// counter. The counter was exact until an operator could put it back to zero, and a requeue
    /// does exactly that: asking it directly would skip the ledger for a message the ledger knows
    /// about, and re-run consumers that already succeeded (ADR 0034, ADR 0061).
    /// </remarks>
    private async Task<IReadOnlySet<string>> DeliveredConsumersOfAsync(
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        if (!message.MayHaveSettledConsumers)
        {
            return new HashSet<string>();
        }

        var delivered = await trainingContext.Set<OutboxMessageConsumer>()
            .Where(delivery => delivery.MessageId == message.Id)
            .Select(delivery => delivery.ConsumerName)
            .ToListAsync(cancellationToken);

        return delivered.ToHashSet();
    }

    /// <summary>
    /// Records a failed attempt on the envelope and says so — Warning while the budget lasts,
    /// one Error at the moment the message poisons (ADR 0033).
    /// </summary>
    private void RecordFailure(OutboxMessage message, string error, Exception exception, OutboxOptions configured)
    {
        // The broad catches feeding this are the retry mechanism, not a shrug: whatever failed is
        // recorded on the envelope, the attempt is counted, and the message returns to the pool —
        // one doubling later — until the budget in MaxAttempts is spent.
        message.RecordFailure(error, timeProvider.GetUtcNow().UtcDateTime, configured.RetryDelay);

        if (message.Attempts >= configured.MaxAttempts)
        {
            // Counted at the same transition the log announces, and under the same fact name the
            // other outbox instruments use: this is the alarm's metric, where the health probe's
            // Degraded is its screen (ADR 0061, ADR 0096).
            OutboxTelemetry.Poisoned.Add(1, new KeyValuePair<string, object?>(OutboxTelemetry.FactNameTag, message.Name));

            // Error, once, at the transition: this is the moment the system gives up on a
            // committed fact. It was the whole of the dead-letter surface until ADR 0061, and is
            // still the only half that reaches a log aggregator rather than a screen.
            logger.LogError(
                exception,
                "Outbox message {MessageId} ({Name} v{Version}) is poison after {Attempts} attempts; it stays in the table for an operator.",
                message.Id,
                message.Name,
                message.Version,
                message.Attempts);
        }
        else
        {
            logger.LogWarning(
                "Delivering outbox message {MessageId} ({Name} v{Version}) failed on attempt {Attempts} of {MaxAttempts}; next try after {NextAttemptOnUtc:O}.",
                message.Id,
                message.Name,
                message.Version,
                message.Attempts,
                configured.MaxAttempts,
                message.NextAttemptOnUtc);
        }
    }

    /// <summary>
    /// Deletes messages delivered longer ago than the retention period. Answers how many rows
    /// went, so the caller can say so when the count is worth a sentence.
    /// </summary>
    /// <remarks>
    /// Only delivered rows are ever swept: a poison row is an operator's evidence and deleting it
    /// would be the mechanism destroying its own crime scene. The filtered index over delivered
    /// rows makes this a range seek that finds nothing almost every poll (ADR 0033).
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    public async Task<int> SweepDeliveredAsync(CancellationToken cancellationToken)
    {
        var boundary = timeProvider.GetUtcNow().UtcDateTime - options.Value.RetentionPeriod;

        var swept = await trainingContext.Set<OutboxMessage>()
            .Where(message => message.ProcessedOnUtc != null && message.ProcessedOnUtc < boundary)
            .ExecuteDeleteAsync(cancellationToken);

        if (swept > 0)
        {
            logger.LogInformation(
                "Swept {Count} delivered outbox messages older than {RetentionPeriod}.",
                swept,
                options.Value.RetentionPeriod);
        }

        return swept;
    }
}
