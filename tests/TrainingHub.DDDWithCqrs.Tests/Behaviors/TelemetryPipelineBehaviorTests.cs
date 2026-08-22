using System.Diagnostics;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using TrainingHub.DDDWithCqrs.Application.Features.Trainers.Create;
using TrainingHub.DDDWithCqrs.Application.Features.Trainers.GetById;
using TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator.Behaviors;
using TrainingHub.Shared.Application.Dtos.Trainer;
using TrainingHub.Shared.Common.Results;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using Mediator;
using Xunit;

namespace TrainingHub.DDDWithCqrs.Tests.Behaviors;

/// <summary>
/// Behavior covered for <c>TelemetryPipelineBehavior</c>.
/// </summary>
/// <remarks>
/// The listeners are the BCL's own — an <see cref="ActivityListener"/> scoped to the one source
/// the behavior speaks from, a <see cref="MeterListener"/> scoped to its meter — so what is proved
/// here is exactly what any subscriber would see, OpenTelemetry included, without this suite
/// referencing it. Span and tag names are quoted as literals on purpose: they are the recorded
/// vocabulary dashboards are written against (ADR 0096), and a test that read them from the
/// constants would follow a rename instead of failing on it.
/// </remarks>
public sealed class TelemetryPipelineBehaviorTests
{
    private static ActivityListener Listening(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TrainingHub.Application",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static TelemetryPipelineBehavior<CreateTrainerCommand, Result> CommandBehavior() => new();

    // -- A successful command --

    /// <summary>
    /// Handle, successful command, names the span and says success.
    /// </summary>
    [Fact]
    public async Task Handle_SuccessfulCommand_NamesTheSpanAndSaysSuccess()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        MessageHandlerDelegate<CreateTrainerCommand, Result> next =
            (_, _) => new ValueTask<Result>(Result.Success());

        await CommandBehavior().Handle(new CreateTrainerCommand(), next, CancellationToken.None);

        // The message type name and nothing else: the naming records already made it say
        // everything, so telemetry does not coin a trimmed second vocabulary.
        var activity = stopped.Should().ContainSingle().Subject;
        activity.DisplayName.Should().Be("CreateTrainerCommand");
        activity.GetTagItem("command.name").Should().Be("CreateTrainerCommand");
        activity.GetTagItem("outcome").Should().Be("success");
        activity.GetTagItem("error.code").Should().BeNull();
        activity.Status.Should().Be(ActivityStatusCode.Unset);
    }

    // -- A refused command --

    /// <summary>
    /// Handle, failed command, says failure and the refusal's code.
    /// </summary>
    [Fact]
    public async Task Handle_FailedCommand_SaysFailureAndTheRefusalsCode()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        MessageHandlerDelegate<CreateTrainerCommand, Result> next =
            (_, _) => new ValueTask<Result>(Result.Failure(TrainerErrorCodes.BioEmpty, "The bio is empty."));

        await CommandBehavior().Handle(new CreateTrainerCommand(), next, CancellationToken.None);

        // A refusal is an outcome, not an error: the status stays unset — mirroring the 4xx the
        // funnel answers — and the code is the domain's own, never the localized sentence.
        var activity = stopped.Should().ContainSingle().Subject;
        activity.GetTagItem("outcome").Should().Be("failure");
        activity.GetTagItem("error.code").Should().Be(TrainerErrorCodes.BioEmpty.Value);
        activity.Status.Should().Be(ActivityStatusCode.Unset);
    }

    // -- An escaped exception --

    /// <summary>
    /// Handle, throwing command, marks the span as an error and rethrows.
    /// </summary>
    [Fact]
    public async Task Handle_ThrowingCommand_MarksTheSpanAsAnErrorAndRethrows()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        MessageHandlerDelegate<CreateTrainerCommand, Result> next =
            (_, _) => throw new InvalidOperationException("The handler broke.");

        Func<Task> act = async () =>
            await CommandBehavior().Handle(new CreateTrainerCommand(), next, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = stopped.Should().ContainSingle().Subject;
        activity.GetTagItem("outcome").Should().Be("error");
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.Events.Should().Contain(
            activityEvent => activityEvent.Name == "exception",
            "the cause belongs on the span that failed, where the trace shows it");
    }

    // -- A query --

    /// <summary>
    /// Handle, query, names the span with the query tag.
    /// </summary>
    [Fact]
    public async Task Handle_Query_NamesTheSpanWithTheQueryTag()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        var behavior = new TelemetryPipelineBehavior<GetTrainerByIdQuery, TrainerDto?>();

        MessageHandlerDelegate<GetTrainerByIdQuery, TrainerDto?> next =
            (_, _) => new ValueTask<TrainerDto?>((TrainerDto?)null);

        await behavior.Handle(new GetTrainerByIdQuery(Guid.NewGuid()), next, CancellationToken.None);

        var activity = stopped.Should().ContainSingle().Subject;
        activity.DisplayName.Should().Be("GetTrainerByIdQuery");
        activity.GetTagItem("query.name").Should().Be("GetTrainerByIdQuery");
        activity.GetTagItem("command.name").Should().BeNull();
        activity.GetTagItem("outcome").Should().Be("success");
    }

    // -- A message that is neither --

    /// <summary>
    /// Handle, notification, opens no span.
    /// </summary>
    [Fact]
    public async Task Handle_Notification_OpensNoSpan()
    {
        var stopped = new List<Activity>();
        using var listener = Listening(stopped);

        var behavior = new TelemetryPipelineBehavior<SomethingHappenedNotification, Unit>();

        var nextCalled = false;
        MessageHandlerDelegate<SomethingHappenedNotification, Unit> next = (_, _) =>
        {
            nextCalled = true;
            return new ValueTask<Unit>(Unit.Value);
        };

        await behavior.Handle(new SomethingHappenedNotification(), next, CancellationToken.None);

        // A domain event runs inside the very span its command opened; a second span there would
        // say nothing new, so the behavior steps aside entirely.
        nextCalled.Should().BeTrue();
        stopped.Should().BeEmpty();
    }

    // -- The measurement --

    /// <summary>
    /// Handle, failed command, records the duration under name, outcome and code.
    /// </summary>
    [Fact]
    public async Task Handle_FailedCommand_RecordsTheDurationUnderNameOutcomeAndCode()
    {
        var measurements = new List<Dictionary<string, object?>>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "TrainingHub.Application"
                && instrument.Name == "traininghub.commands.duration")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var byName = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                byName[tag.Key] = tag.Value;
            }

            measurements.Add(byName);
        });
        listener.Start();

        MessageHandlerDelegate<CreateTrainerCommand, Result> next =
            (_, _) => new ValueTask<Result>(Result.Failure(TrainerErrorCodes.BioEmpty, "The bio is empty."));

        await CommandBehavior().Handle(new CreateTrainerCommand(), next, CancellationToken.None);

        // One histogram carries the count, the failure rate and the latency at once, which is why
        // there is no executed counter and no failed counter beside it (ADR 0096). Every tag is
        // from a bounded set: message names the codebase closes, three outcomes, the arch-ruled
        // error vocabulary.
        var tags = measurements.Should().ContainSingle().Subject;
        tags.Should().Contain("command.name", "CreateTrainerCommand");
        tags.Should().Contain("outcome", "failure");
        tags.Should().Contain("error.code", TrainerErrorCodes.BioEmpty.Value);
    }

    /// <summary>
    /// A message that is neither a command nor a query, for the pass-through proof.
    /// </summary>
    public sealed record SomethingHappenedNotification : INotification;
}
