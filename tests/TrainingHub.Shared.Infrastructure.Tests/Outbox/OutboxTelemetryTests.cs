using System.Diagnostics;
using AwesomeAssertions;
using TrainingHub.Shared.Infrastructure.Outbox;
using Xunit;

namespace TrainingHub.Shared.Infrastructure.Tests.Outbox;

/// <summary>
/// Behavior covered for <c>OutboxTelemetry</c> — the helper that reopens an envelope's stored
/// trace as a link on the delivery's own root span (ADR 0097).
/// </summary>
public sealed class OutboxTelemetryTests
{
    private static ActivityListener Listening(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TrainingHub.Outbox",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>
    /// Start delivery activity, with a stored context, links the delivery to it.
    /// </summary>
    [Fact]
    public void StartDeliveryActivity_WithAStoredContext_LinksTheDeliveryToIt()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        const string traceParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

        OutboxTelemetry.StartDeliveryActivity("Deliver TrainerCreated", traceParent)?.Dispose();

        // Its own trace, pointing at its origin: a root span carrying one link, never a child —
        // the request that committed the fact has long answered, and a retry minutes later cannot
        // honestly claim to still be part of it.
        var activity = stopped.Should().ContainSingle().Subject;
        activity.DisplayName.Should().Be("Deliver TrainerCreated");
        activity.Parent.Should().BeNull();
        activity.ParentId.Should().BeNull();

        var link = activity.Links.Should().ContainSingle().Subject;
        link.Context.TraceId.ToHexString().Should().Be("0af7651916cd43dd8448eb211c80319c");
        link.Context.SpanId.ToHexString().Should().Be("b7ad6b7169203331");
    }

    /// <summary>
    /// Start delivery activity, with no stored context, starts a plain root.
    /// </summary>
    [Fact]
    public void StartDeliveryActivity_WithNoStoredContext_StartsAPlainRoot()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        OutboxTelemetry.StartDeliveryActivity("Deliver TrainerCreated", traceParent: null)?.Dispose();

        // The delivery is worth watching even when its origin was never traced.
        var activity = stopped.Should().ContainSingle().Subject;
        activity.ParentId.Should().BeNull();
        activity.Links.Should().BeEmpty();
    }

    /// <summary>
    /// Start delivery activity, with a context that does not parse, ignores it.
    /// </summary>
    [Fact]
    public void StartDeliveryActivity_WithAContextThatDoesNotParse_IgnoresIt()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        OutboxTelemetry.StartDeliveryActivity("Deliver TrainerCreated", "not-a-traceparent")?.Dispose();

        var activity = stopped.Should().ContainSingle().Subject;
        activity.Links.Should().BeEmpty();
    }

    /// <summary>
    /// Start delivery activity, inside an ambient activity, still starts a root.
    /// </summary>
    [Fact]
    public void StartDeliveryActivity_InsideAnAmbientActivity_StillStartsARoot()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        // Whatever the worker's poll happens to run under must not adopt the delivery: the root
        // claim has to survive an ambient activity somebody adds around the loop later.
        using var ambient = new Activity("ambient").Start();

        OutboxTelemetry.StartDeliveryActivity("Deliver TrainerCreated", traceParent: null)?.Dispose();

        var activity = stopped.Should().ContainSingle().Subject;
        activity.ParentId.Should().BeNull();
    }
}
