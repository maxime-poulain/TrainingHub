using System.Diagnostics;
using AwesomeAssertions;
using TrainingHub.Shared.Application.Notifications;
using TrainingHub.Shared.Infrastructure.Email;
using Microsoft.Extensions.Options;
using Xunit;

namespace TrainingHub.Shared.Infrastructure.Tests.Email;

/// <summary>
/// The telemetry half of <c>SmtpEmailSender</c>: one span per SMTP conversation, and nothing
/// personal on it (ADR 0096).
/// </summary>
/// <remarks>
/// Driven against a port nothing listens on, so the send fails at the connection — which is
/// exactly the path worth proving: the failure must still propagate whole for the outbox's retry
/// budget, and the span it leaves behind must carry the outcome and not the letter.
/// </remarks>
public sealed class SmtpEmailSenderTelemetryTests
{
    /// <summary>
    /// Send, against a dead server, marks the span and carries no personal data.
    /// </summary>
    [Fact]
    public async Task Send_AgainstADeadServer_MarksTheSpanAndCarriesNoPersonalData()
    {
        var stopped = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TrainingHub.Email",
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions
        {
            Host = "localhost",
            // A port from the discard block nothing binds during a test run: the connection is
            // refused immediately instead of timing out.
            Port = 9,
            SenderAddress = "no-reply@traininghub.test",
        }));

        var message = new EmailMessage("someone@example.test", "A subject", "A body");

        Func<Task> act = async () => await sender.SendAsync(message, CancellationToken.None);

        // The failure still propagates whole — the outbox owns the retry budget, and an adapter
        // that swallowed it would disarm that whole mechanism.
        await act.Should().ThrowAsync<Exception>();

        var activity = stopped.Should().ContainSingle().Subject;
        activity.DisplayName.Should().Be("SendEmail");
        activity.GetTagItem("outcome").Should().Be("failed");
        activity.Status.Should().Be(ActivityStatusCode.Error);

        // The span says how the conversation went, never what the letter said or to whom: an
        // address in a span attribute would outlive every retention policy the database has.
        var recordedValues = activity.TagObjects.Select(tag => tag.Value?.ToString()).ToArray();
        recordedValues.Should().NotContain(value =>
            value != null && (value.Contains(message.Recipient) || value.Contains(message.Subject) || value.Contains(message.Body)));
    }
}
