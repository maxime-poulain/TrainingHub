using System.Diagnostics;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using TrainingHub.Shared.Application.Notifications;

namespace TrainingHub.Shared.Infrastructure.Email;

/// <summary>
/// An <see cref="IEmailSender"/> over anything that speaks SMTP.
/// </summary>
/// <remarks>
/// This is the only type in the solution that knows the mail protocol, and an architecture rule
/// keeps it that way. It talks to a Mailpit container in development and would talk to a hosted
/// relay in production without a line changing. Each send opens its own connection: the client is
/// not safe to share, this singleton serves both hosts' outbox workers, and the volume — one
/// message per trainer-lifecycle fact, dispatched sequentially — buys nothing from a pooled
/// connection that would need liveness checks and a lock. A failed send is caught only long
/// enough to mark the span it failed inside, then propagates whole: the outbox processor records
/// the exception on the envelope and retries within its budget, which is the retry policy this
/// adapter would otherwise duplicate.
/// </remarks>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    private readonly SmtpOptions _options = options.Value;

    /// <inheritdoc />
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // One span for the whole SMTP conversation — the external dependency a notice waits on.
        // It deliberately carries no recipient, subject or body: which notice this is, the
        // consumer span above already says, and the rest is personal data (ADR 0096). A failure
        // still propagates whole — the outbox owns the retry — but leaves as a marked span first.
        using var activity = EmailTelemetry.Source.StartActivity("SendEmail");
        var started = Stopwatch.GetTimestamp();

        try
        {
            await DeliverAsync(message, cancellationToken);

            activity?.SetTag(EmailTelemetry.OutcomeTag, EmailTelemetry.SentOutcome);
            EmailTelemetry.SendDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(EmailTelemetry.OutcomeTag, EmailTelemetry.SentOutcome));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetTag(EmailTelemetry.OutcomeTag, EmailTelemetry.FailedOutcome);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            activity?.AddException(exception);
            EmailTelemetry.SendDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>(EmailTelemetry.OutcomeTag, EmailTelemetry.FailedOutcome));

            throw;
        }
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.SenderName, _options.SenderAddress));
        mime.To.Add(MailboxAddress.Parse(message.Recipient));
        mime.Subject = message.Subject;
        mime.Body = new TextPart(TextFormat.Plain) { Text = message.Body };

        // The From stays this application's own even when the message is somebody else's: sending
        // as the visitor would be a forgery every receiving domain with an SPF record is entitled
        // to refuse. Reply-To is the header that exists for exactly this — the message comes from
        // the platform, and the answer goes to the person (ADR 0082).
        if (!string.IsNullOrWhiteSpace(message.ReplyTo))
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(message.ReplyTo));
        }

        using var client = new SmtpClient();

        await client.ConnectAsync(
            _options.Host,
            _options.Port,
            _options.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
            cancellationToken);

        // The start-up validation guarantees these come as a pair; testing both is for the
        // compiler's flow analysis, which cannot see that far.
        if (!string.IsNullOrEmpty(_options.Username) && !string.IsNullOrEmpty(_options.Password))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}
