using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TrainingHub.Shared.Infrastructure.Outbox;

/// <summary>
/// The outbox's telemetry vocabulary: the activity source its delivery spans come from, the meter
/// its instruments live on, and the helper that reopens a stored trace as a link.
/// </summary>
/// <remarks>
/// Platform primitives only — <see cref="ActivitySource"/> and <see cref="Meter"/> are the BCL's,
/// so this project stays free of OpenTelemetry and the seam in <c>TrainingHub.Shared.Api</c> is
/// the one place that subscribes to these names (ADR 0095). The names are public constants
/// because they are what an operator's dashboards are written against, and an architecture rule
/// pins them (ADR 0096). With nothing subscribed, every member here is a no-op the platform
/// designed to cost nearly nothing.
/// </remarks>
public static class OutboxTelemetry
{
    /// <summary>The activity source the delivery spans come from.</summary>
    public const string ActivitySourceName = "TrainingHub.Outbox";

    /// <summary>The meter the outbox instruments live on — the same name as the source.</summary>
    public const string MeterName = ActivitySourceName;

    internal const string FactNameTag = "fact.name";

    internal const string OutcomeTag = "outcome";

    internal const string DeliveredOutcome = "delivered";

    internal const string FailedOutcome = "failed";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> DeliveryDuration = Meter.CreateHistogram<double>(
        "traininghub.outbox.delivery.duration",
        unit: "s",
        description: "How long one delivery attempt of one outbox message took, by fact name and outcome.");

    internal static readonly Counter<long> Poisoned = Meter.CreateCounter<long>(
        "traininghub.outbox.poisoned",
        description: "Messages whose attempt budget ran out, by fact name — each one is an operator's case (ADR 0061).");

    internal static readonly Counter<long> FactsDelivered = Meter.CreateCounter<long>(
        "traininghub.facts.delivered",
        description: "Business facts fully delivered, by wire name — the business half of the metrics (ADR 0096).");

    /// <summary>
    /// Starts the root span of one delivery attempt, linked to the trace that committed the
    /// message when the envelope carries one.
    /// </summary>
    /// <remarks>
    /// A delivery is its own trace on purpose: the request that committed the fact finished long
    /// ago, and a retry minutes later cannot honestly claim to still be part of it. The stored
    /// context becomes a link — the shape OpenTelemetry names for asynchronous work — so the two
    /// traces stay one click apart while their sampling stays independent (ADR 0097). An envelope
    /// with no context, or one whose context does not parse, starts a plain root: the delivery is
    /// worth watching even when its origin is not known.
    /// </remarks>
    /// <param name="name">The span name — the delivery verb and the fact's wire name.</param>
    /// <param name="traceParent">The W3C <c>traceparent</c> the envelope stored, if any.</param>
    public static Activity? StartDeliveryActivity(string name, string? traceParent)
    {
        // The worker loop is nobody's child: whatever activity the poll happens to sit under must
        // not become this delivery's parent, or the root claim above quietly stops being true.
        Activity.Current = null;

        if (traceParent is not null
            && ActivityContext.TryParse(traceParent, traceState: null, isRemote: true, out var producer))
        {
            return Source.StartActivity(
                name,
                ActivityKind.Consumer,
                parentContext: default,
                tags: null,
                links: [new ActivityLink(producer)]);
        }

        return Source.StartActivity(name, ActivityKind.Consumer);
    }
}
