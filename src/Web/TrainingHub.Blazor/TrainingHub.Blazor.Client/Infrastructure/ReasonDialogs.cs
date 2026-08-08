using MudBlazor;
using TrainingHub.Blazor.Client.Components;

namespace TrainingHub.Blazor.Client.Infrastructure;

/// <summary>
/// Opens the reason dialog and answers what the administrator wrote, or nothing.
/// </summary>
/// <remarks>
/// Both sanctions ask the same question in the same shape, so the plumbing between a page and
/// <see cref="ReasonDialog"/> — the parameter bag, the options, unwrapping the result — is written
/// once. What differs between the two is only the words, and those are arguments.
/// </remarks>
public static class ReasonDialogs
{
    /// <summary>
    /// Asks for a reason, and answers <see langword="null"/> when the administrator backs out.
    /// </summary>
    /// <param name="dialogService">The dialog service the page injects.</param>
    /// <param name="title">The dialog's own title bar.</param>
    /// <param name="prompt">The verb before the subject — "Suspend", "Withhold".</param>
    /// <param name="subject">What is being acted on: a trainer's name, a training's title.</param>
    /// <param name="consequence">What this does and what it does not.</param>
    /// <param name="verb">The confirming button's label.</param>
    public static async Task<string?> AskAsync(
        IDialogService dialogService,
        string title,
        string prompt,
        string subject,
        string consequence,
        string verb)
    {
        ArgumentNullException.ThrowIfNull(dialogService);

        var parameters = new DialogParameters<ReasonDialog>
        {
            { dialog => dialog.Prompt, prompt },
            { dialog => dialog.Subject, subject },
            { dialog => dialog.Consequence, consequence },
            { dialog => dialog.Verb, verb }
        };

        var options = new DialogOptions
        {
            CloseButton = true,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        var dialog = await dialogService.ShowAsync<ReasonDialog>(title, parameters, options);
        var result = await dialog.Result;

        return result is { Canceled: false, Data: string reason } ? reason : null;
    }
}
