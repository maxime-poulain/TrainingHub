using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Instrumentation.SqlClient;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TrainingHub.Shared.Api.Extensions;
using TrainingHub.Shared.Api.Telemetry;
using Xunit;

namespace TrainingHub.Shared.Api.Tests.Extensions;

/// <summary>
/// Behavior covered for <c>AddApiTelemetry</c>: the endpoint switch, the startup refusals, and
/// the three noise filters ADR 0095 decides.
/// </summary>
/// <remarks>
/// Unlike the pipeline-behavior tests, which deliberately stay on the BCL's listeners because
/// their subject is what any subscriber sees, this suite names OpenTelemetry's option types on
/// purpose: the subject here is the one seam that owns the library, and its filters live on those
/// options. The filters are invoked directly rather than through a served request — they are
/// plain delegates, and what each one drops is the decision worth asserting, not the plumbing
/// around it. In the collection of the environment-variable tests because one fact stages the
/// document-generation signal.
/// </remarks>
[Collection(EnvironmentVariableCollection.Name)]
public sealed class TelemetryExtensionsTests
{
    private const string Endpoint = "Telemetry:OtlpEndpoint";

    // A refused loopback port: batched exporters never connect during a test, and flushing on
    // disposal fails fast instead of waiting out a timeout against an address that black-holes.
    private const string LocalCollector = "http://localhost:4317";

    // -- The switch --

    /// <summary>
    /// Add api telemetry, with no endpoint, registers no pipeline.
    /// </summary>
    /// <remarks>
    /// The off switch is an absence, not a disabled pipeline, so the assertion is the absence:
    /// no tracer provider, no meter provider, nothing to pay for. This is what CI and every test
    /// factory run under (ADR 0095).
    /// </remarks>
    [Fact]
    public void AddApiTelemetry_WithNoEndpoint_RegistersNoPipeline()
    {
        using var provider = Services((Endpoint, "")).BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().BeNull(
            "a blank endpoint means no telemetry at all — not a pipeline exporting nowhere");
        provider.GetService<MeterProvider>().Should().BeNull();
    }

    /// <summary>
    /// Add api telemetry, with an endpoint, builds the tracer and meter providers.
    /// </summary>
    [Fact]
    public void AddApiTelemetry_WithAnEndpoint_BuildsTheTracerAndMeterProviders()
    {
        using var provider = Services((Endpoint, LocalCollector)).BuildServiceProvider();

        // Resolving is the proof: constructing the providers executes the whole registration —
        // the resource, the sampler, and every instrumentation option this file configures.
        provider.GetRequiredService<TracerProvider>().Should().NotBeNull();
        provider.GetRequiredService<MeterProvider>().Should().NotBeNull();
    }

    /// <summary>
    /// Add api telemetry, during document generation, registers no pipeline.
    /// </summary>
    /// <remarks>
    /// <c>generate-clients.sh</c> boots the layered host under Development, whose committed
    /// settings name the local dashboard — so without this gate every client regeneration would
    /// open an exporter. The same precedent the migrations and the seeders follow (ADR 0095).
    /// </remarks>
    [Fact]
    public void AddApiTelemetry_DuringDocumentGeneration_RegistersNoPipeline()
    {
        using var generation = new TemporaryEnvironmentVariable(OpenApiDocumentGeneration.EnvironmentVariable, "1");

        using var provider = Services((Endpoint, LocalCollector)).BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().BeNull(
            "the boot that only emits the OpenAPI document should not open an exporter either");
    }

    // -- The refusals --

    /// <summary>
    /// Add api telemetry, with an endpoint that is not an address, refuses the host.
    /// </summary>
    /// <remarks>
    /// The registration itself steps aside for a value that does not parse, so the refusal is the
    /// options validation's recorded sentence rather than a <c>UriFormatException</c> thrown from
    /// the middle of a registration chain — and it names the key a deployment has to fix.
    /// </remarks>
    [Fact]
    public void AddApiTelemetry_WithAnEndpointThatIsNotAnAddress_RefusesTheHost()
    {
        using var provider = Services((Endpoint, "not-an-address")).BuildServiceProvider();

        var reading = () => provider.GetRequiredService<IOptions<TelemetryOptions>>().Value;

        reading.Should().Throw<OptionsValidationException>()
            .WithMessage("*Telemetry:OtlpEndpoint*",
                "a wrong value must refuse the host with the key's name, not export into nowhere for a week");
    }

    /// <summary>
    /// Add api telemetry, with a ratio outside its range, refuses the host.
    /// </summary>
    [Fact]
    public void AddApiTelemetry_WithARatioOutsideItsRange_RefusesTheHost()
    {
        using var provider = Services((Endpoint, LocalCollector), ("Telemetry:TracesSampleRatio", "0")).BuildServiceProvider();

        var reading = () => provider.GetRequiredService<IOptions<TelemetryOptions>>().Value;

        reading.Should().Throw<OptionsValidationException>()
            .WithMessage("*TracesSampleRatio*",
                "zero is not a sampling decision — turning telemetry off is the endpoint's job, and the sentence says so");
    }

    // -- The filters (ADR 0095's noise decisions, asserted rather than narrated) --

    /// <summary>
    /// The health probes, are not recorded.
    /// </summary>
    /// <remarks>
    /// Orchestrators and the Development dashboard watch the probes every few seconds, forever;
    /// recording them would make the noise the signal. Both prefixes matter — <c>/health</c>
    /// covers the pair and the dashboard's detailed endpoint, <c>/healthchecks-ui</c> is its own
    /// segment and would slip a prefix test on <c>/health</c> alone.
    /// </remarks>
    [Fact]
    public void TheHealthProbes_AreNotRecorded()
    {
        using var provider = BuiltPipeline();

        var filter = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .CurrentValue.Filter!;

        filter(RequestFor("/health/live")).Should().BeFalse();
        filter(RequestFor("/health/ready")).Should().BeFalse();
        filter(RequestFor("/healthchecks-ui")).Should().BeFalse();
        filter(RequestFor("/Training")).Should().BeTrue(
            "the filter exists to drop the probes, never a request somebody sent");
    }

    /// <summary>
    /// The dashboard's own poll, is not recorded.
    /// </summary>
    /// <remarks>
    /// In Development the health dashboard's collector polls <c>/health/ui</c> through
    /// <c>HttpClient</c> every ten seconds — a fresh root trace each time, without this mirror of
    /// the server-side filter.
    /// </remarks>
    [Fact]
    public void TheDashboardsOwnPoll_IsNotRecorded()
    {
        using var provider = BuiltPipeline();

        var filter = provider.GetRequiredService<IOptionsMonitor<HttpClientTraceInstrumentationOptions>>()
            .CurrentValue.FilterHttpRequestMessage!;

        using var poll = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:5085/health/ui"));
        using var call = new HttpRequestMessage(HttpMethod.Get, new Uri("http://localhost:5085/Training"));
        using var relative = new HttpRequestMessage(HttpMethod.Get, new Uri("health/ui", UriKind.Relative));

        filter(poll).Should().BeFalse();
        filter(call).Should().BeTrue("an outgoing call somebody wrote is exactly what the spans are for");
        filter(relative).Should().BeTrue("a relative address cannot be inspected, and dropping it would hide a real call");
    }

    /// <summary>
    /// A root sql command, is not recorded.
    /// </summary>
    /// <remarks>
    /// The outbox's five-second poll and the startup migrations would otherwise mint a root trace
    /// every few seconds, saying nothing. The filter is written to answer the same way whichever
    /// activity the bridge shows it — the command's own, or the enclosing operation — and every
    /// branch is walked here: no ambient activity, the command's own root, the command under an
    /// operation, and an operation itself.
    /// </remarks>
    [Fact]
    public void ARootSqlCommand_IsNotRecorded()
    {
        using var provider = BuiltPipeline();

        var filter = provider.GetRequiredService<IOptionsMonitor<SqlClientTraceInstrumentationOptions>>()
            .CurrentValue.Filter!;

        // The bridge's own source name, so the filter can tell whose activity it is looking at.
        using var sqlSource = new ActivitySource("OpenTelemetry.Instrumentation.SqlClient");
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "OpenTelemetry.Instrumentation.SqlClient",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);

        Activity.Current = null;
        filter("a command").Should().BeFalse("with nothing current there is no enclosing operation at all");

        using (sqlSource.StartActivity("SELECT"))
        {
            filter("a command").Should().BeFalse("the command's own activity has no parent, so it would be the root");
        }

        using (new Activity("an operation").Start())
        {
            using (sqlSource.StartActivity("SELECT"))
            {
                filter("a command").Should().BeTrue("the command runs inside an operation, which is the trace worth keeping");
            }

            filter("a command").Should().BeTrue("an enclosing operation of any other source means the command is not a root");
        }

        Activity.Current = null;
    }

    // -- The harness --

    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => setting.Value))
            .Build();

    /// <remarks>
    /// The framework's own settable environment rather than a stub of this suite's: the seam
    /// reads two properties, and the type exists precisely to be constructed in tests.
    /// </remarks>
    private static IServiceCollection Services(params (string Key, string? Value)[] settings) =>
        new ServiceCollection().AddApiTelemetry(
            Configuration(settings),
            new HostingEnvironment
            {
                ApplicationName = "TrainingHub.Tests.Host",
                EnvironmentName = Environments.Development,
            });

    /// <remarks>
    /// Resolving the tracer provider is what runs the deferred registration callbacks; without
    /// it the instrumentation options are never configured and every filter would be null.
    /// </remarks>
    private static ServiceProvider BuiltPipeline()
    {
        var provider = Services((Endpoint, LocalCollector)).BuildServiceProvider();
        _ = provider.GetRequiredService<TracerProvider>();

        return provider;
    }

    private static DefaultHttpContext RequestFor(string path) =>
        new() { Request = { Path = path } };
}
