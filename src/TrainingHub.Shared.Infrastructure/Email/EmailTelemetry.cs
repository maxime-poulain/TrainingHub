using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace TrainingHub.Shared.Infrastructure.Email;

/// <summary>
/// The mail adapter's telemetry vocabulary: the activity source its send spans come from, and the
/// meter its one instrument lives on.
/// </summary>
/// <remarks>
/// Platform primitives only, like <see cref="Outbox.OutboxTelemetry"/> — this project never
/// references OpenTelemetry; the seam in <c>TrainingHub.Shared.Api</c> subscribes to these names
/// (ADR 0095) and an architecture rule pins them (ADR 0096). The span deliberately carries no
/// recipient, no subject and no body: which notice was sent is already the name of the consumer
/// span above it, and everything else about a mail is somebody's personal data (ADR 0096).
/// </remarks>
public static class EmailTelemetry
{
    /// <summary>The activity source the send spans come from.</summary>
    public const string ActivitySourceName = "TrainingHub.Email";

    /// <summary>The meter the send instrument lives on — the same name as the source.</summary>
    public const string MeterName = ActivitySourceName;

    internal const string OutcomeTag = "outcome";

    internal const string SentOutcome = "sent";

    internal const string FailedOutcome = "failed";

    internal static readonly ActivitySource Source = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    internal static readonly Histogram<double> SendDuration = Meter.CreateHistogram<double>(
        "traininghub.email.send.duration",
        unit: "s",
        description: "How long one SMTP send took, by outcome — the one external dependency a notice waits on.");
}
