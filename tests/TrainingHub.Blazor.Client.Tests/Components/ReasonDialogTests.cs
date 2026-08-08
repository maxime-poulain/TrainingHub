using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using TrainingHub.Blazor.Client.Components;
using Xunit;

namespace TrainingHub.Blazor.Client.Tests.Components;

/// <summary>
/// Behaviour covered for the dialog both sanctions ask their question through.
/// </summary>
/// <remarks>
/// The reason is not a field on this dialog, it is the dialog: ADR 0052 made a withholding a state
/// distinct from a withdrawal so that the person it lands on could be told the difference, and
/// ADR 0056 sends that text to them by email, word for word. So what is pinned here is the pair of
/// answers this component may give — a reason somebody wrote, or nothing at all — and never a third.
/// <para>
/// Rendered through <see cref="MudDialogProvider"/> rather than on its own, because that is the
/// only place a MudBlazor dialog exists. The pages that open it substitute the service instead, and
/// pin the other half: that what comes back out of here is what reaches the API.
/// </para>
/// </remarks>
public sealed class ReasonDialogTests : ComponentTest
{
    private const string Verb = "Suspend";

    /// <summary>
    /// Rendered, the reason still empty, leaves the confirming button disabled.
    /// </summary>
    /// <remarks>
    /// The first thing an administrator sees, and the cheapest place to refuse: a dialog that
    /// opened ready to confirm invites the empty sanction rather than preventing it.
    /// </remarks>
    [Fact]
    public async Task Rendered_TheReasonStillEmpty_LeavesTheConfirmingButtonDisabled()
    {
        // Act
        var (dialog, _) = await OpenAsync();

        // Assert
        Confirming(dialog).HasAttribute("disabled").Should().BeTrue();
    }

    /// <summary>
    /// Confirming, a reason written, closes with what was typed and nothing around it.
    /// </summary>
    /// <remarks>
    /// The surrounding whitespace goes because the text is quoted to a person in a notice, not
    /// because the server would refuse it — it would not, and a reason of three spaces is exactly
    /// the empty one this dialog is here to prevent.
    /// </remarks>
    [Fact]
    public async Task Confirming_AReasonWritten_ClosesWithWhatWasTyped()
    {
        // Arrange
        var (dialog, reference) = await OpenAsync();
        Write(dialog, "  Repeated breaches of the content policy.  ");

        // The button follows the field rather than the keystroke: the form validates on a later
        // render, so this waits for the state the administrator would actually see.
        dialog.WaitForAssertion(() => Confirming(dialog).HasAttribute("disabled").Should().BeFalse());

        // Act
        Confirming(dialog).Click();

        // Assert
        var result = await ClosedAsync(dialog, reference);

        result.Canceled.Should().BeFalse();
        result.Data.Should().Be("Repeated breaches of the content policy.");
    }

    /// <summary>
    /// Writing nothing but whitespace, refuses it as an absent reason.
    /// </summary>
    /// <remarks>
    /// Three spaces satisfy <c>Required</c> and would satisfy the server too — the contract asks
    /// for one character. What they do not do is explain anything to the person they are sent to,
    /// which is the only reason this field exists.
    /// </remarks>
    [Fact]
    public async Task WritingNothingButWhitespace_RefusesItAsAnAbsentReason()
    {
        // Arrange
        var (dialog, reference) = await OpenAsync();

        // Act
        Write(dialog, "   ");

        // Assert
        dialog.WaitForAssertion(() => dialog.Markup.Should().Contain("A reason is required"));

        Confirming(dialog).HasAttribute("disabled").Should().BeTrue();
        reference.Result.IsCompleted.Should().BeFalse();
    }

    /// <summary>
    /// Writing a reason beyond the bound, refuses it and names the bound.
    /// </summary>
    /// <remarks>
    /// Five hundred is what both contracts publish, so refusing here spares a round trip rather
    /// than inventing a rule. It is checked and not only advertised through <c>MaxLength</c>: an
    /// attribute the browser honours is not a decision this component made, and nothing stops the
    /// text arriving by a paste the attribute never sees.
    /// </remarks>
    [Fact]
    public async Task WritingAReasonBeyondTheBound_RefusesItAndNamesTheBound()
    {
        // Arrange
        var (dialog, reference) = await OpenAsync();

        // Act
        Write(dialog, new string('x', 501));

        // Assert
        dialog.WaitForAssertion(() =>
            dialog.Markup.Should().Contain("A reason cannot exceed 500 characters"));

        Confirming(dialog).HasAttribute("disabled").Should().BeTrue();
        reference.Result.IsCompleted.Should().BeFalse();
    }

    /// <summary>
    /// Cancelling, answers nothing at all.
    /// </summary>
    /// <remarks>
    /// Backing out is not a decision, and the pages read a cancelled result as "do not send". A
    /// dialog that closed with an empty reason instead would have them send one.
    /// </remarks>
    [Fact]
    public async Task Cancelling_AnswersNothingAtAll()
    {
        // Arrange
        var (dialog, reference) = await OpenAsync();
        Write(dialog, "Repeated breaches of the content policy.");

        // Act
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        // Assert
        var result = await ClosedAsync(dialog, reference);

        result.Canceled.Should().BeTrue();
    }

    private async Task<(IRenderedComponent<MudDialogProvider> Dialog, IDialogReference Reference)> OpenAsync()
    {
        var dialog = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var parameters = new DialogParameters<ReasonDialog>
        {
            { component => component.Prompt, "Suspend" },
            { component => component.Subject, "Ada Lovelace" },
            { component => component.Consequence, "Their catalogue leaves public view." },
            { component => component.Verb, Verb }
        };

        var reference = await dialog.InvokeAsync(
            () => dialogService.ShowAsync<ReasonDialog>("Suspend trainer", parameters));

        return (dialog, reference);
    }

    /// <remarks>
    /// The wait comes before the await, and that ordering is the point: a dialog that never closes
    /// leaves <see cref="IDialogReference.Result"/> pending forever, so awaiting it first turns a
    /// failing assertion into a suite that hangs rather than one that goes red.
    /// </remarks>
    private static async Task<DialogResult> ClosedAsync(
        IRenderedComponent<MudDialogProvider> dialog, IDialogReference reference)
    {
        dialog.WaitForAssertion(() => reference.Result.IsCompleted.Should().BeTrue());

        var result = await reference.Result;

        result.Should().NotBeNull();

        return result;
    }

    private static void Write(IRenderedComponent<MudDialogProvider> dialog, string reason) =>
        // The immediate event rather than the change one: the field validates as it is typed, which
        // is what makes the confirming button follow along.
        dialog.Find("textarea").Input(reason);

    private static AngleSharp.Dom.IElement Confirming(IRenderedComponent<MudDialogProvider> dialog) =>
        dialog.FindAll("button").Single(button => button.TextContent.Trim() == Verb);
}
