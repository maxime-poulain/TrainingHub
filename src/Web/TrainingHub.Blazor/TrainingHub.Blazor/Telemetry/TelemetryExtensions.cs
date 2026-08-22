using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TrainingHub.Blazor.Telemetry;

/// <summary>
/// What the BFF observes about itself, and where it goes: traces, metrics and logs, over OTLP.
/// </summary>
/// <remarks>
/// The BFF's own counterpart of the API hosts' telemetry seam, the way its liveness endpoint and
/// its culture-forwarding handler are counterparts — deliberately not a reference to
/// <c>TrainingHub.Shared.Api</c>, which would drag the whole backend into a host that owns no
/// domain and no persistence. It reads the same <c>Telemetry</c> section shape, and it matters
/// beyond symmetry: ASP.NET Core and the proxy's HttpClient propagate the W3C trace context to the
/// API whether or not anything here records, so without this wiring every API trace would name a
/// parent span no backend ever received. See ADR 0095.
/// </remarks>
public static class TelemetryExtensions
{
    private const string SectionName = "Telemetry";

    private const string OtlpEndpointKey = "OtlpEndpoint";

    private const string TracesSampleRatioKey = "TracesSampleRatio";

    /// <summary>
    /// When an endpoint is configured, registers the OpenTelemetry pipeline: request and
    /// forwarded-call spans, runtime and platform meters, the framework's logs, and one OTLP
    /// exporter for all of it. With no endpoint this registers nothing at all.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configuration">The configuration the section is read from.</param>
    /// <param name="environment">The host environment — the resource identity's source.</param>
    public static IServiceCollection AddBffTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var section = configuration.GetSection(SectionName);
        var otlpEndpoint = section[OtlpEndpointKey];

        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            return services;
        }

        // Fail at startup, in the style of the address checks beside AddBff: a wrong value
        // refuses the host now rather than exporting into nowhere for a week.
        if (!Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                $"'{SectionName}:{OtlpEndpointKey}' must be an absolute http or https address, or blank to turn telemetry off.");
        }

        var sampleRatio = section.GetValue<double?>(TracesSampleRatioKey) ?? 1.0;
        if (sampleRatio is not (> 0 and <= 1))
        {
            throw new InvalidOperationException(
                $"'{SectionName}:{TracesSampleRatioKey}' must be above zero and at most one — turning telemetry off is the endpoint's job.");
        }

        var version = (Assembly.GetEntryAssembly() ?? typeof(TelemetryExtensions).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        services.AddOpenTelemetry()
            // The same identity convention as the API hosts: the application name is how this
            // deployment already tells its processes apart.
            .ConfigureResource(resource => resource
                .AddService(serviceName: environment.ApplicationName, serviceVersion: version)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", environment.EnvironmentName)]))
            .WithTracing(tracing => tracing
                .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    instrumentation.RecordException = true;

                    // The compose healthcheck curls the liveness endpoint every few seconds,
                    // forever; recording it would make the noise the signal.
                    instrumentation.Filter = httpContext =>
                        !httpContext.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
                })
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            // The framework's own logging pipeline gains the OpenTelemetry provider beside its
            // console — this host has no Serilog, deliberately (ADR 0026), so the provider is the
            // whole answer here.
            .WithLogging()
            .UseOtlpExporter(OtlpExportProtocol.Grpc, endpoint);

        return services;
    }
}
