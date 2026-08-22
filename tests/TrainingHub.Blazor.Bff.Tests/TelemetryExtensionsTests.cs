using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TrainingHub.Blazor.Telemetry;
using Xunit;

namespace TrainingHub.Blazor.Bff.Tests;

/// <summary>
/// Behavior covered for <c>AddBffTelemetry</c>: the endpoint switch, the two startup refusals,
/// and the liveness filter — the BFF's own small counterpart of the API seam (ADR 0095).
/// </summary>
/// <remarks>
/// Asserted against the extension directly, the way the missing-address guard already is: the
/// factory blanks the endpoint for every hosted test, so the wiring past the guard is only
/// reachable here. The endpoint used is a refused loopback port, so nothing ever connects and
/// disposal flushes into an immediate failure rather than a timeout.
/// </remarks>
public sealed class TelemetryExtensionsTests
{
    /// <summary>
    /// The bff registers no telemetry without an endpoint.
    /// </summary>
    [Fact]
    public void The_bff_registers_no_telemetry_without_an_endpoint()
    {
        using var provider = Services(otlpEndpoint: null).BuildServiceProvider();

        provider.GetService<TracerProvider>().Should().BeNull(
            "a blank endpoint means no pipeline at all, which is what every hosted test runs under");
        provider.GetService<MeterProvider>().Should().BeNull();
    }

    /// <summary>
    /// The bff builds the telemetry pipeline when an endpoint is configured.
    /// </summary>
    [Fact]
    public void The_bff_builds_the_telemetry_pipeline_when_an_endpoint_is_configured()
    {
        using var provider = Services("http://localhost:4317").BuildServiceProvider();

        // Resolving is the proof: constructing the providers executes the registration whole —
        // resource, sampler, instrumentation options and the logging provider beside them.
        provider.GetRequiredService<TracerProvider>().Should().NotBeNull();
        provider.GetRequiredService<MeterProvider>().Should().NotBeNull();
    }

    /// <summary>
    /// The bff refuses a telemetry endpoint that is not an address.
    /// </summary>
    /// <remarks>
    /// The same posture as the missing API address: fail at startup, loudly, naming the key —
    /// instead of exporting into nowhere for a week.
    /// </remarks>
    [Fact]
    public void The_bff_refuses_a_telemetry_endpoint_that_is_not_an_address()
    {
        var registering = () => Services("not-an-address");

        registering.Should().Throw<InvalidOperationException>()
            .WithMessage("*Telemetry:OtlpEndpoint*");
    }

    /// <summary>
    /// The bff refuses a sampling ratio outside its range.
    /// </summary>
    [Fact]
    public void The_bff_refuses_a_sampling_ratio_outside_its_range()
    {
        var registering = () => Services("http://localhost:4317", tracesSampleRatio: "0");

        registering.Should().Throw<InvalidOperationException>()
            .WithMessage("*TracesSampleRatio*",
                "zero is not a sampling decision — turning telemetry off is the endpoint's job");
    }

    /// <summary>
    /// The bffs liveness probe is not recorded.
    /// </summary>
    /// <remarks>
    /// The compose healthcheck curls <c>/health/live</c> every few seconds, forever; recording it
    /// would make the noise the signal. Only the <c>/health</c> prefix, deliberately — this host
    /// serves no dashboard, so there is no <c>/healthchecks-ui</c> to name (ADR 0095).
    /// </remarks>
    [Fact]
    public void The_bffs_liveness_probe_is_not_recorded()
    {
        using var provider = Services("http://localhost:4317").BuildServiceProvider();

        // Resolving the tracer provider runs the deferred registration callbacks; without it the
        // instrumentation options are never configured and the filter would be null.
        _ = provider.GetRequiredService<TracerProvider>();

        var filter = provider.GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .CurrentValue.Filter!;

        filter(new DefaultHttpContext { Request = { Path = "/health/live" } }).Should().BeFalse();
        filter(new DefaultHttpContext { Request = { Path = "/catalog" } }).Should().BeTrue(
            "the filter exists to drop the probe, never a page somebody opened");
    }

    /// <remarks>
    /// The framework's own settable environment rather than a stub: the extension reads two
    /// properties of it, and the type exists precisely to be constructed in tests.
    /// </remarks>
    private static IServiceCollection Services(string? otlpEndpoint, string? tracesSampleRatio = null)
    {
        var settings = new Dictionary<string, string?>();
        if (otlpEndpoint is not null)
        {
            settings["Telemetry:OtlpEndpoint"] = otlpEndpoint;
        }

        if (tracesSampleRatio is not null)
        {
            settings["Telemetry:TracesSampleRatio"] = tracesSampleRatio;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection().AddBffTelemetry(
            configuration,
            new HostingEnvironment
            {
                ApplicationName = "TrainingHub.Blazor.Tests",
                EnvironmentName = Environments.Development,
            });
    }
}
