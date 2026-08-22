using System.Diagnostics;
using Mediator;
using TrainingHub.Shared.Common.Results;
using TrainingHub.Shared.CQS;

namespace TrainingHub.DDDWithCqrs.Infrastructure.ThirdParty.Mediator.Behaviors;

/// <summary>
/// Wraps every command and query in a span and a duration measurement, so the pipeline is the one
/// place operation telemetry exists and no handler carries any.
/// </summary>
/// <remarks>
/// Registered first, ahead of validation, on purpose: a rejected command fails like every other
/// command (ADR 0016), so a rejection must count as a failed command here too, carrying the
/// pipeline's own error code the way any refusal carries the code of whoever refused. The span is
/// named by the message type alone — <c>CreateTrainerCommand</c>, not a trimmed second vocabulary
/// — because ADR 0081 and its neighbors already spent the naming budget making that name say
/// everything (ADR 0096). Messages that are neither commands nor queries — the domain events the
/// same mediator publishes — pass through untouched: they run inside the very span this behavior
/// opened for their command, and spans of their own would say nothing new (ADR 0096).
/// <para>
/// A failed <see cref="Result"/> is an outcome, not an error: the span keeps its unset status and
/// says <c>outcome = failure</c> with the first error's code, mirroring the 4xx the funnel will
/// answer. Only an escaped exception marks the span as an error — and cancellation stays a
/// shutdown rather than an outcome, exactly as the outbox treats it.
/// </para>
/// </remarks>
public sealed class TelemetryPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull, IMessage
{
    /// <summary>
    /// Runs the message inside a span, and records its duration under its name and outcome.
    /// </summary>
    public async ValueTask<TResponse> Handle(TRequest message, MessageHandlerDelegate<TRequest, TResponse> next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(next);

        var isCommand = message is ICommandBase;
        if (!isCommand && message is not IQuery)
        {
            return await next(message, cancellationToken);
        }

        var name = message.GetType().Name;
        var nameTag = isCommand ? ApplicationTelemetry.CommandNameTag : ApplicationTelemetry.QueryNameTag;
        var duration = isCommand ? ApplicationTelemetry.CommandDuration : ApplicationTelemetry.QueryDuration;

        using var activity = ApplicationTelemetry.Source.StartActivity(name);
        activity?.SetTag(nameTag, name);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await next(message, cancellationToken);

            var outcome = ApplicationTelemetry.SuccessOutcome;
            string? errorCode = null;

            // Match and Switch are the Result's only doors, and that is enough: telemetry needs
            // the outcome and the first refusal's code, never the value — which is also why this
            // observes nothing a span should not carry.
            if (response is Result result)
            {
                result.Switch(
                    () => { },
                    errors =>
                    {
                        outcome = ApplicationTelemetry.FailureOutcome;
                        errorCode = errors[0].ErrorCode.Value;
                    });
            }

            activity?.SetTag(ApplicationTelemetry.OutcomeTag, outcome);

            if (errorCode is null)
            {
                duration.Record(
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    new KeyValuePair<string, object?>(nameTag, name),
                    new KeyValuePair<string, object?>(ApplicationTelemetry.OutcomeTag, outcome));
            }
            else
            {
                activity?.SetTag(ApplicationTelemetry.ErrorCodeTag, errorCode);

                duration.Record(
                    Stopwatch.GetElapsedTime(started).TotalSeconds,
                    new KeyValuePair<string, object?>(nameTag, name),
                    new KeyValuePair<string, object?>(ApplicationTelemetry.OutcomeTag, outcome),
                    new KeyValuePair<string, object?>(ApplicationTelemetry.ErrorCodeTag, errorCode));
            }

            return response;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetTag(ApplicationTelemetry.OutcomeTag, ApplicationTelemetry.ErrorOutcome);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.AddException(exception);

            duration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(nameTag, name),
                new KeyValuePair<string, object?>(ApplicationTelemetry.OutcomeTag, ApplicationTelemetry.ErrorOutcome));

            throw;
        }
    }
}
