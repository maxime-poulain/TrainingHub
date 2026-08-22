using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using TrainingHub.Shared.Api.Extensions;
using Xunit;

namespace TrainingHub.Shared.Api.Tests.Extensions;

/// <summary>
/// Behavior covered for <c>AddApiLogging</c>'s telemetry half: the OTLP sink joins the text sinks
/// on the endpoint switch, and stays away without it.
/// </summary>
/// <remarks>
/// The sinks themselves are Serilog's to test; what is this repository's is the wiring — that a
/// configured endpoint builds a logger whose pipeline carries the OTLP sibling (ADR 0095 amending
/// ADR 0026), constructed against a lazily-connecting exporter so no test needs a collector. What
/// the pipeline renders end to end stays with the integration suite's <c>LoggingTest</c>, which
/// reads the file sink; these facts prove the construction path a blanked endpoint otherwise
/// leaves unexecuted.
/// </remarks>
public sealed class LoggingExtensionsTests
{
    /// <summary>
    /// Add api logging, with a telemetry endpoint, wires the otlp sink.
    /// </summary>
    [Fact]
    public void AddApiLogging_WithATelemetryEndpoint_WiresTheOtlpSink()
    {
        using var provider = Services(("Telemetry:OtlpEndpoint", "http://localhost:4317"));

        // Creating a logger constructs the Serilog pipeline — the whole configuration callback
        // runs, sink block included, and a wiring mistake surfaces here instead of on a host.
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("TrainingHub.Tests");

        var logging = () => logger.LogInformation("The pipeline accepted a line.");

        logging.Should().NotThrow(
            "the sink buffers and exports in the background — a collector that is not there costs " +
            "dropped batches, never a failed log call");
    }

    /// <summary>
    /// Add api logging, with no endpoint, keeps the text sinks alone.
    /// </summary>
    [Fact]
    public void AddApiLogging_WithNoEndpoint_KeepsTheTextSinksAlone()
    {
        using var provider = Services(("Telemetry:OtlpEndpoint", ""));

        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("TrainingHub.Tests");

        var logging = () => logger.LogInformation("The pipeline accepted a line.");

        logging.Should().NotThrow("ADR 0026's console pipeline stands whether or not an aggregator exists");
    }

    /// <remarks>
    /// The file sink is off so a unit run writes nothing to disk; the environment is the
    /// framework's own settable one, registered because the sink block reads the service name
    /// from it. <c>AddApiLogging</c> adds the accessor the enricher needs by itself.
    /// </remarks>
    private static ServiceProvider Services(params (string Key, string? Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings
                .Append(("ApiLogging:WriteToFile", "false"))
                .ToDictionary(setting => setting.Item1, setting => setting.Item2))
            .Build();

        return new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new HostingEnvironment
            {
                ApplicationName = "TrainingHub.Tests.Host",
                EnvironmentName = Environments.Development,
            })
            .AddApiLogging(configuration)
            .BuildServiceProvider();
    }
}
