namespace TrainingHub.Shared.Api.Telemetry;

/// <summary>
/// What a deployment tunes about telemetry: where the OTLP exporter sends, and how much of the
/// traffic is traced.
/// </summary>
/// <remarks>
/// The endpoint doubles as the switch: blank means nothing is registered at all, which is what CI,
/// the test hosts and a developer who never started the dashboard get without doing anything. A
/// typed class rather than the <c>OTEL_*</c> environment variables, for the same reason
/// <see cref="Logging.ApiLoggingOptions"/> is not a <c>Serilog</c> section: the whole surface stays
/// a validated, bindable class a reader can enumerate. See ADR 0095.
/// </remarks>
public sealed class TelemetryOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Telemetry";

    /// <summary>
    /// Where the OTLP exporter sends, as an absolute http(s) address —
    /// <c>http://localhost:4317</c> against the compose dashboard. Blank or absent turns telemetry
    /// off entirely.
    /// </summary>
    /// <remarks>
    /// A string rather than a <see cref="Uri"/>, so a test host can neutralize a committed
    /// Development value with an empty setting: binding <c>""</c> to a <see cref="Uri"/> yields an
    /// empty relative address rather than <see langword="null"/>, and the switch would quietly
    /// stop switching.
    /// </remarks>
    public string? OtlpEndpoint { get; set; }

    /// <summary>
    /// The fraction of new traces that are sampled — above zero, at most one. Development keeps
    /// the default of one and sees every trace; a production deployment dials it down. Spans that
    /// continue a sampled trace stay sampled whatever the ratio says, so a trace is always whole.
    /// </summary>
    public double TracesSampleRatio { get; set; } = 1.0;
}
