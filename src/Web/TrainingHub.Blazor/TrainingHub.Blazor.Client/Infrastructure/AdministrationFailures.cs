using MudBlazor;
using TrainingHub.GeneratedClients;

namespace TrainingHub.Blazor.Client.Infrastructure;

/// <summary>
/// What happened to an administrative action, as far as the page needs to know.
/// </summary>
public enum AdministrationOutcome
{
    /// <summary>The API accepted it.</summary>
    Done,

    /// <summary>The API refused it, and said why.</summary>
    Refused,

    /// <summary>Nothing was reached, so nothing is known about the state of anything.</summary>
    Unreachable
}

/// <summary>
/// The one error-handling body the two administration pages share.
/// </summary>
/// <remarks>
/// <para>
/// The four administrative actions fail in exactly the same four ways — <c>400</c> on a reason the
/// domain refuses, <c>404</c> when the row is gone, <c>409</c> when the state is already the one
/// being asked for, <c>403</c> when the caller's authority has lapsed — and eight copies of the
/// same catch stack would drift apart at precisely the two that matter. It lives here rather than
/// on a base page because the pages share no markup, and because Sonar measures duplication in
/// <c>src/Web/</c> like anywhere else.
/// </para>
/// <para>
/// The copy rule this enforces is the one the trainer's own pages already follow: a problem
/// document's <c>Detail</c> is shown as written, because the server is the authority on its own
/// refusal; everything else gets a sentence of ours and a line in the console.
/// <c>ApiException.Message</c> embeds the raw response body and the generator's own wording, so it
/// never reaches a screen.
/// </para>
/// </remarks>
public static class AdministrationFailures
{
    private const int StatusForbidden = 403;
    private const int StatusNotFound = 404;

    /// <summary>
    /// Runs an administrative action and says what became of it.
    /// </summary>
    /// <param name="snackbar">Where the outcome is told.</param>
    /// <param name="action">The call to make.</param>
    /// <param name="success">What to say when it worked.</param>
    /// <param name="verb">The past participle for the failure sentences — "suspended", "withheld".</param>
    /// <param name="subject">What was acted on — "trainer", "training".</param>
    public static async Task<AdministrationOutcome> RunAsync(
        ISnackbar snackbar,
        Func<Task> action,
        string success,
        string verb,
        string subject)
    {
        ArgumentNullException.ThrowIfNull(snackbar);
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            await action();
            snackbar.Add(success, Severity.Success);
            return AdministrationOutcome.Done;
        }
        catch (ApiException<ProblemDetails> exception) when (exception.StatusCode == StatusNotFound)
        {
            // Gone between this list being read and the button being pressed. Not an error the
            // administrator caused, and the list is simply stale.
            snackbar.Add($"That {subject} no longer exists.", Severity.Info);
            return AdministrationOutcome.Refused;
        }
        catch (ApiException<ProblemDetails> exception)
        {
            // A 409 lands here too — the state was already the one being asked for — and the
            // server's own sentence says so better than anything this page could invent. So does
            // the 400 a reason the domain refuses produces.
            snackbar.Add(
                exception.Result.Detail ?? exception.Result.Title ?? "The request was rejected.",
                Severity.Error);
            return AdministrationOutcome.Refused;
        }
        catch (ApiException exception) when (exception.StatusCode == StatusForbidden)
        {
            // Reachable even here: this page was reached with the administrator role, and the role
            // can have been taken away since. A 403 carries no body, so there is nothing to read
            // out of it.
            Console.Error.WriteLine(exception);
            snackbar.Add(
                "You are no longer allowed to do that. Sign in again to see where you stand.",
                Severity.Error);
            return AdministrationOutcome.Refused;
        }
        catch (ApiException exception)
        {
            Console.Error.WriteLine(exception);
            snackbar.Add($"The {subject} could not be {verb}. Try again in a moment.", Severity.Error);
            return AdministrationOutcome.Unreachable;
        }
        catch (Exception exception)
        {
            // Whatever this is, it is ours. A NullReferenceException must not become interface copy.
            Console.Error.WriteLine(exception);
            snackbar.Add($"Something went wrong; the {subject} was not {verb}.", Severity.Error);
            return AdministrationOutcome.Unreachable;
        }
    }

    /// <summary>
    /// Reports a failed read, which has nothing to undo and nothing to report but itself.
    /// </summary>
    /// <param name="snackbar">Where the outcome is told.</param>
    /// <param name="exception">What went wrong.</param>
    /// <param name="fallback">What to say when the server said nothing readable.</param>
    public static void Report(ISnackbar snackbar, Exception exception, string fallback)
    {
        ArgumentNullException.ThrowIfNull(snackbar);

        switch (exception)
        {
            case ApiException<ProblemDetails> refused:
                snackbar.Add(
                    refused.Result.Detail ?? refused.Result.Title ?? "The request was rejected.",
                    Severity.Error);
                break;

            case ApiException:
                Console.Error.WriteLine(exception);
                snackbar.Add($"{fallback} Try again in a moment.", Severity.Error);
                break;

            default:
                Console.Error.WriteLine(exception);
                snackbar.Add(fallback, Severity.Error);
                break;
        }
    }
}
