namespace TrainingHub.Shared.Infrastructure.ThirdParty.EfCore.Outbox;

/// <summary>
/// The envelope an integration event is stored in: the row the transactional outbox is made of.
/// </summary>
/// <remarks>
/// A persistence shape, not a domain one — deliberately neither an aggregate nor auditable nor a
/// holder of domain events, so neither interceptor looks at it twice. The identity of the message
/// is the envelope, not the payload: <see cref="Id"/> is minted once at publish time and doubles as
/// the key the delivery ledger settles consumers under, making at-least-once delivery safe, and
/// <see cref="Name"/>/<see cref="Version"/> say what the payload deserializes into without trusting
/// a CLR type name that a refactoring could change.
/// <para>
/// The delivery columns tell one message's story. A worker claims the row by writing
/// <see cref="ClaimedBy"/> and <see cref="ClaimedUntil"/> — the lease ADR 0002 promised, taken in
/// the database so a claimant that dies simply lets its lease lapse. A delivery that succeeds
/// stamps <see cref="ProcessedOnUtc"/>; one that fails counts itself in <see cref="Attempts"/>,
/// parks the reason in <see cref="Error"/>, and books its next try in
/// <see cref="NextAttemptOnUtc"/> on a doubling schedule, so an ailing dependency is probed, not
/// hammered. A poison message is the row whose attempts exhausted the budget, still unprocessed,
/// with the last failure sitting beside it (ADR 0024, ADR 0025, ADR 0033).
/// </para>
/// </remarks>
public sealed class OutboxMessage
{
    /// <summary>
    /// Stores an integration event under its envelope.
    /// </summary>
    /// <param name="id">The message identifier — also the delivery ledger's deduplication key.</param>
    /// <param name="name">The stable wire name of the event, from the registry.</param>
    /// <param name="version">The version of that wire name.</param>
    /// <param name="payload">The event, serialized as JSON.</param>
    /// <param name="occurredOnUtc">When the fact was recorded, in UTC.</param>
    /// <param name="traceParent">
    /// The W3C trace context of the operation that committed the fact, if one was being traced.
    /// </param>
    public OutboxMessage(Guid id, string name, int version, string payload, DateTime occurredOnUtc, string? traceParent = null)
    {
        Id = id;
        Name = name;
        Version = version;
        Payload = payload;
        OccurredOnUtc = occurredOnUtc;
        TraceParent = traceParent;
    }

    // EF Core materializes rows through this constructor; the public one is for the publisher.
    private OutboxMessage()
    {
        Name = string.Empty;
        Payload = string.Empty;
    }

    /// <summary>The message identifier — minted once at publish time, the ledger's deduplication key.</summary>
    public Guid Id { get; }

    /// <summary>The stable wire name of the event, as registered — never a CLR type name.</summary>
    public string Name { get; }

    /// <summary>The version of the wire name, so a payload outlives its first schema.</summary>
    public int Version { get; }

    /// <summary>The event, serialized as JSON.</summary>
    public string Payload { get; }

    /// <summary>When the fact was recorded, in UTC — the order the worker delivers in.</summary>
    public DateTime OccurredOnUtc { get; }

    /// <summary>
    /// The W3C <c>traceparent</c> of the operation that committed the fact;
    /// <see langword="null"/> when nothing was being traced. The delivery links its own trace
    /// back to this one — a pointer to telemetry, never telemetry itself (ADR 0097).
    /// </summary>
    public string? TraceParent { get; }

    /// <summary>When the worker delivered the message; <see langword="null"/> while it is owed.</summary>
    public DateTime? ProcessedOnUtc { get; private set; }

    /// <summary>How many deliveries have failed — what the retry budget is spent against.</summary>
    public int Attempts { get; private set; }

    /// <summary>The last delivery failure, kept beside the message it poisoned.</summary>
    public string? Error { get; private set; }

    /// <summary>
    /// When the message may be tried again; <see langword="null"/> until a delivery has failed,
    /// and again once one has succeeded. The claim refuses rows whose schedule has not come due.
    /// </summary>
    public DateTime? NextAttemptOnUtc { get; private set; }

    /// <summary>Which worker holds the current lease — kept after delivery, as provenance.</summary>
    public string? ClaimedBy { get; private set; }

    /// <summary>When the current lease lapses; a lapsed lease makes the row claimable again.</summary>
    public DateTime? ClaimedUntil { get; private set; }

    /// <summary>
    /// When an operator last put this message back in the pool; <see langword="null"/> for a
    /// message no operator has touched (ADR 0061).
    /// </summary>
    public DateTime? RequeuedOnUtc { get; private set; }

    /// <summary>
    /// Whether the delivery ledger may already name consumers for this message — the question the
    /// processor asks before paying for the ledger's read.
    /// </summary>
    /// <remarks>
    /// The counter alone used to answer it, and stopped being able to the moment a requeue could
    /// set it back to zero: the ledger's rows outlive the budget on purpose, so that a retry
    /// re-runs only what is still owed (ADR 0034). Asked here rather than at the call site because
    /// it is a statement about this row's history, and a row is the only thing that knows its own.
    /// </remarks>
    public bool MayHaveSettledConsumers => Attempts > 0 || RequeuedOnUtc is not null;

    /// <summary>
    /// Records a successful delivery: the message is done, no lease needs to outlive it, and a
    /// delivered message schedules nothing.
    /// </summary>
    /// <param name="processedOnUtc">When the delivery completed, in UTC.</param>
    public void MarkProcessed(DateTime processedOnUtc)
    {
        ProcessedOnUtc = processedOnUtc;
        ClaimedUntil = null;
        NextAttemptOnUtc = null;
    }

    /// <summary>
    /// Records a failed delivery attempt, releases the lease, and books the next try one doubling
    /// further out — <c>retryDelay × 2^(attempts−1)</c> — so any worker may retry the message
    /// while the attempt budget lasts, but none before the schedule says so (ADR 0033).
    /// </summary>
    /// <param name="error">What the failed attempt reported.</param>
    /// <param name="failedOnUtc">When the attempt failed, in UTC — the schedule's anchor.</param>
    /// <param name="retryDelay">The base delay the doubling starts from.</param>
    public void RecordFailure(string error, DateTime failedOnUtc, TimeSpan retryDelay)
    {
        Attempts++;
        Error = error;
        ClaimedUntil = null;
        NextAttemptOnUtc = failedOnUtc + (retryDelay * Math.Pow(2, Attempts - 1));
    }

    /// <summary>
    /// Hands a poison message back to the pool: the budget is fresh, the schedule is now, and no
    /// worker holds it (ADR 0061).
    /// </summary>
    /// <remarks>
    /// The last error is deliberately kept. It is what an operator was looking at when they decided
    /// to retry, and the next attempt overwrites it by itself — clearing it here would erase the
    /// evidence one moment before finding out whether it was still true.
    /// </remarks>
    /// <param name="requeuedOnUtc">When the operator asked for the retry, in UTC.</param>
    public void Requeue(DateTime requeuedOnUtc)
    {
        Attempts = 0;
        ClaimedUntil = null;
        NextAttemptOnUtc = null;
        RequeuedOnUtc = requeuedOnUtc;
    }
}
