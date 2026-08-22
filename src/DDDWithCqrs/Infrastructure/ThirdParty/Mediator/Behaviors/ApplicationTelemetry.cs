using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator.Behaviors;

/// <summary>
/// The application pipeline's telemetry vocabulary: the activity source the command and query
/// spans come from, and the meter their two histograms live on.
/// </summary>
/// <remarks>
/// Platform primitives only — <see cref="ActivitySource"/> and <see cref="Meter"/> are the BCL's,
/// so neither this project nor anything below it references OpenTelemetry; the seam in
/// <c>TrainingHub.Shared.Api</c> subscribes to these names, handed to it by this host's
/// <c>Program.cs</c> (ADR 0095). The names are public constants because they are what an
/// operator's dashboards are written against, and an architecture rule pins them (ADR 0096).
/// </remarks>
public static class ApplicationTelemetry
{
    /// <summary>The activity source the command and query spans come from.</summary>
    public const string ActivitySourceName = "TrainingHub.Application";

    /// <summary>The meter the two duration histograms live on — the same name as the source.</summary>
    public const string MeterName = ActivitySourceName;

    internal const string CommandNameTag = "command.name";

    internal const string QueryNameTag = "query.name";

    internal const string ErrorCodeTag = "error.code";

    internal const string OutcomeTag = "outcome";

    internal const string SuccessOutcome = "success";

    internal const string FailureOutcome = "failure";

    internal const string ErrorOutcome = "error";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> CommandDuration = Meter.CreateHistogram<double>(
        "traininghub.commands.duration",
        unit: "s",
        description: "How long one command took, by name and outcome — count and failure rate included, which is why there is no separate counter.");

    internal static readonly Histogram<double> QueryDuration = Meter.CreateHistogram<double>(
        "traininghub.queries.duration",
        unit: "s",
        description: "How long one query took, by name and outcome.");
}
