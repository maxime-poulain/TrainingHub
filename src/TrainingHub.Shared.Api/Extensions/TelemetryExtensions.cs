using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TrainingHub.Shared.Api.Telemetry;
using TrainingHub.Shared.Infrastructure.Email;
using TrainingHub.Shared.Infrastructure.Outbox;

namespace TrainingHub.Shared.Api.Extensions;

/// <summary>
/// What an API host observes about itself, and where it goes: traces, metrics and logs, over OTLP.
/// </summary>
/// <remarks>
/// Shared by both stacks for the same reason as <c>LoggingExtensions</c>: a pipeline configured in
/// one host's <c>Program.cs</c> only observes that host, and the other one silently stays dark.
/// This is the one place that references OpenTelemetry — custom instrumentation everywhere else
/// speaks the BCL's <see cref="ActivitySource"/> and <c>Meter</c>, and this seam is what
/// subscribes to those names — and an architecture rule keeps the library here. Everything a
/// deployment tunes is in <see cref="TelemetryOptions"/>. See ADR 0095.
/// </remarks>
public static class TelemetryExtensions
{
    // The name the SqlClient bridge starts its activities from — quoted here so the root-drop
    // filter below can tell whether it is looking at the command's own activity or at whatever
    // was current when the command began.
    private const string SqlClientActivitySourceName = "OpenTelemetry.Instrumentation.SqlClient";

    /// <summary>
    /// Binds <see cref="TelemetryOptions"/> and, when an endpoint is configured, registers the
    /// OpenTelemetry pipeline: request, dependency and database spans, runtime and platform
    /// meters, the solution's own sources, and one OTLP exporter for all of it.
    /// </summary>
    /// <remarks>
    /// With no endpoint this registers nothing at all — not a disabled pipeline, no pipeline —
    /// which is what CI and the test hosts run under. The gate on
    /// <see cref="OpenApiDocumentGeneration.IsInProgress"/> follows the precedent of the
    /// migrations and the seeders: the boot that only emits the OpenAPI document should not open
    /// an exporter either. A configured endpoint nobody listens on costs dropped batches and
    /// nothing else: the exporter is batched and background, and no business operation ever waits
    /// on it.
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">The configuration the options are bound from.</param>
    /// <param name="environment">The host environment — the resource identity's source.</param>
    /// <param name="additionalSources">
    /// Activity source and meter names a host adds beyond the shared set — the CQRS host passes
    /// its application source, the layered host passes none.
    /// </param>
    public static IServiceCollection AddApiTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment,
        params string[] additionalSources)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(additionalSources);

        // Bound and validated even when telemetry is off: a wrong value refuses the host at
        // startup rather than the day somebody turns the endpoint on (ADR 0033).
        services.AddOptions<TelemetryOptions>()
            .Bind(configuration.GetSection(TelemetryOptions.SectionName))
            .Validate(
                options => string.IsNullOrWhiteSpace(options.OtlpEndpoint)
                    || (Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out var endpoint)
                        && endpoint.Scheme is "http" or "https"),
                $"'{TelemetryOptions.SectionName}:{nameof(TelemetryOptions.OtlpEndpoint)}' must be an absolute http or https address, or blank to turn telemetry off.")
            .Validate(
                options => options.TracesSampleRatio is > 0 and <= 1,
                $"'{TelemetryOptions.SectionName}:{nameof(TelemetryOptions.TracesSampleRatio)}' must be above zero and at most one — turning telemetry off is the endpoint's job.")
            .ValidateOnStart();

        // Read raw rather than through the options: the decision whether to register the pipeline
        // is taken now, while the service provider that could answer IOptions does not exist yet.
        var telemetrySection = configuration.GetSection(TelemetryOptions.SectionName);
        var otlpEndpoint = telemetrySection[nameof(TelemetryOptions.OtlpEndpoint)];

        if (string.IsNullOrWhiteSpace(otlpEndpoint) || OpenApiDocumentGeneration.IsInProgress())
        {
            return services;
        }

        // A value that is not an address registers nothing either: the startup validation above
        // then refuses the host with the recorded sentence, which beats a raw UriFormatException
        // thrown from the middle of a registration chain.
        if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint))
        {
            return services;
        }

        var sampleRatio = telemetrySection.GetValue<double?>(nameof(TelemetryOptions.TracesSampleRatio)) ?? 1.0;

        // The version the resource reports is the host's own informational version; the entry
        // assembly is that host in every process that gets this far — the document-generation
        // boot, whose entry assembly is the tooling's, returned above.
        var version = (Assembly.GetEntryAssembly() ?? typeof(TelemetryExtensions).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        services.AddOpenTelemetry()
            // The same identity the health dashboard registers: the application name is how this
            // deployment already tells its processes apart, so telemetry does not invent a second.
            .ConfigureResource(resource => resource
                .AddService(serviceName: environment.ApplicationName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                // Every trace locally, a dial in production; spans continuing a sampled trace stay
                // sampled either way, so a trace is always whole. Standard samplers on purpose.
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    instrumentation.RecordException = true;

                    // The probes are watched by orchestrators and the Development dashboard, every
                    // few seconds, forever: recording them would make the noise the signal. The
                    // parent-based sampler then drops the probes' own dependency spans for free.
                    instrumentation.Filter = httpContext =>
                        !httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                        && !httpContext.Request.Path.StartsWithSegments("/healthchecks-ui", StringComparison.OrdinalIgnoreCase);
                })
                .AddHttpClientInstrumentation(instrumentation =>
                    // The mirror of the server-side filter: in Development the dashboard's
                    // collector polls /health/ui through HttpClient every ten seconds, and that
                    // poll would otherwise be a fresh root trace each time.
                    instrumentation.FilterHttpRequestMessage = request =>
                        request.RequestUri is not { IsAbsoluteUri: true } address
                        || !address.AbsolutePath.StartsWith("/health", StringComparison.Ordinal))
                .AddSqlClientInstrumentation(instrumentation =>
                {
                    instrumentation.RecordException = true;

                    // A database command with no enclosing operation is not a trace: the outbox
                    // poll and the startup migrations would otherwise mint a root trace every few
                    // seconds, saying nothing. Written to answer the same way whichever activity
                    // the bridge shows the filter — the command's own, whose parent is the
                    // enclosing operation, or the enclosing operation itself.
                    instrumentation.Filter = _ =>
                        Activity.Current is { } current
                        && (current.Parent is not null
                            || current.ParentId is not null
                            || !string.Equals(current.Source.Name, SqlClientActivitySourceName, StringComparison.Ordinal));
                })
                .AddSource(OutboxTelemetry.ActivitySourceName)
                .AddSource(EmailTelemetry.ActivitySourceName)
                .AddSource(additionalSources))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(OutboxTelemetry.MeterName)
                .AddMeter(EmailTelemetry.MeterName)
                // EF Core's own meter, built into the framework since EF 9 — the reason no EF
                // bridge package is referenced anywhere.
                .AddMeter("Microsoft.EntityFrameworkCore")
                .AddMeter(additionalSources))
            // One exporter for every signal registered above; logs travel through the Serilog
            // sink instead, so the caller ADR 0027 stamps rides along. Batched and background:
            // nothing here ever blocks a request.
            .UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);

        return services;
    }
}
