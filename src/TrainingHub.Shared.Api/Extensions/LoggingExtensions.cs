using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Sinks.OpenTelemetry;
using TrainingHub.Shared.Api.Logging;
using TrainingHub.Shared.Api.Telemetry;

namespace TrainingHub.Shared.Api.Extensions;

/// <summary>
/// What an API host logs, and where it goes: the console, and rolling text files.
/// </summary>
/// <remarks>
/// Shared by both stacks for the same reason as <c>CorsExtensions</c>: a pipeline configured in
/// one host's <c>Program.cs</c> only observes that host, and the other one silently keeps the
/// default — console only, nothing on disk. Everything a deployment tunes is in
/// <see cref="ApiLoggingOptions"/>; nothing is read from a <c>Serilog</c> configuration section
/// on purpose, so the whole logging surface stays a validated, typed class. See ADR 0026.
/// </remarks>
public static class LoggingExtensions
{
    // One template for both sinks, so a line found in a file can be matched to what the console
    // showed. {SourceContext} is the addition over Serilog's default: without the category, "now
    // listening" and a failed storage call read as the same narrator. {User} is the caller the
    // enricher stamps — a property a text sink never shows unless the template names it.
    private const string OutputTemplate =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] [{User}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Binds <see cref="ApiLoggingOptions"/> and replaces the logging pipeline with Serilog.
    /// </summary>
    /// <remarks>
    /// <c>writeToProviders: true</c> is the one argument here that must never change quietly. By
    /// default Serilog replaces the logger factory and stops feeding every
    /// <c>ILoggerProvider</c> registered beside it — including the recording provider the
    /// integration suite attaches to read what a host logged while answering a 500. That
    /// regression reds no assertion by itself, which is why the test kit carries a probe test
    /// whose only job is to fail when this stops being true.
    /// </remarks>
    public static IServiceCollection AddApiLogging(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Without this every console line appears twice: writeToProviders feeds the providers the
        // default builder already registered, and the framework's own console provider prints the
        // event beside Serilog's console sink. Clearing here removes only what is registered so
        // far — a provider attached afterwards, like the recording one the integration suite adds,
        // is exactly what writeToProviders exists to keep feeding.
        services.AddLogging(logging => logging.ClearProviders());

        // For the identity enricher. AddApiIdentity registers the accessor too, but relying on
        // that would make logging depend on the order a host happens to call its extensions in —
        // the same argument IdentityExtensions already records for itself. TryAdd inside makes
        // the repetition free.
        services.AddHttpContextAccessor();

        services.AddOptions<ApiLoggingOptions>()
            .Bind(configuration.GetSection(ApiLoggingOptions.SectionName))
            .Validate(
                options => !options.WriteToFile || !string.IsNullOrWhiteSpace(options.Path),
                $"'{ApiLoggingOptions.SectionName}:{nameof(ApiLoggingOptions.Path)}' must name a file while the file sink is on.")
            .Validate(
                options => options.RetainedFileCountLimit > 0,
                $"'{ApiLoggingOptions.SectionName}:{nameof(ApiLoggingOptions.RetainedFileCountLimit)}' must be positive, or old files would never be deleted.")
            .ValidateOnStart();

        return services.AddSerilog(
            (serviceProvider, loggerConfiguration) =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<ApiLoggingOptions>>().Value;

                // Every line names its caller — the signed-in username, "Anonymous", or "System"
                // outside a request. See UserIdentityEnricher for why this is an enricher rather
                // than a middleware pushing LogContext, and ADR 0027 for the decision.
                loggerConfiguration.Enrich.With(
                    new UserIdentityEnricher(serviceProvider.GetRequiredService<IHttpContextAccessor>()));

                loggerConfiguration.MinimumLevel.Is(options.MinimumLevel);

                foreach (var (sourceContext, level) in options.LevelOverrides)
                {
                    loggerConfiguration.MinimumLevel.Override(sourceContext, level);
                }

                // Invariant culture on both sinks: a timestamp or a number in a log line must
                // read the same whatever locale the machine happens to run under (CA1305).
                loggerConfiguration.WriteTo.Console(
                    outputTemplate: OutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture);

                if (options.WriteToFile)
                {
                    loggerConfiguration.WriteTo.File(
                        options.Path,
                        outputTemplate: OutputTemplate,
                        formatProvider: CultureInfo.InvariantCulture,
                        rollingInterval: options.RollingInterval,
                        retainedFileCountLimit: options.RetainedFileCountLimit);
                }

                // The OTLP sibling of the two text sinks — the day ADR 0026 reserved, arrived
                // with ADR 0095. On the same switch as the rest of the telemetry, and stamped
                // with the same service name, so a log line and the span it was written under
                // meet in the aggregator: this sink ships the structured properties and the
                // enrichers' work — the caller included — with the trace and span identifiers
                // attached, which the text template never carries.
                var otlpEndpoint = configuration
                    .GetSection(TelemetryOptions.SectionName)[nameof(TelemetryOptions.OtlpEndpoint)];

                if (!string.IsNullOrWhiteSpace(otlpEndpoint) && !OpenApiDocumentGeneration.IsInProgress())
                {
                    var environment = serviceProvider.GetRequiredService<IHostEnvironment>();

                    loggerConfiguration.WriteTo.OpenTelemetry(sink =>
                    {
                        sink.Endpoint = otlpEndpoint;
                        sink.Protocol = OtlpProtocol.Grpc;
                        sink.ResourceAttributes = new Dictionary<string, object>
                        {
                            ["service.name"] = environment.ApplicationName,
                            ["deployment.environment.name"] = environment.EnvironmentName,
                        };
                    });
                }
            },
            writeToProviders: true);
    }

    /// <summary>
    /// Condenses the framework's per-request narration into one line per request.
    /// </summary>
    /// <remarks>
    /// The counterpart of the default <c>Microsoft.AspNetCore</c> override in
    /// <see cref="ApiLoggingOptions.LevelOverrides"/>: the override silences the framework's
    /// half-dozen Information lines per request, and this middleware puts back the single one
    /// worth keeping — method, path, status and elapsed time.
    /// </remarks>
    public static IApplicationBuilder UseApiLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseSerilogRequestLogging();
    }
}
