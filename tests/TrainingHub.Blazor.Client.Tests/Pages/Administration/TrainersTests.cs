using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MudBlazor;
using TrainingHub.Blazor.Client.Components;
using TrainingHub.Blazor.Client.Pages.Administration;
using TrainingHub.GeneratedClients;
using Xunit;

namespace TrainingHub.Blazor.Client.Tests.Pages.Administration;

/// <summary>
/// Behaviour covered for the administrative trainers page.
/// </summary>
/// <remarks>
/// The page reads a list this API deliberately withdrew from everybody else, so what it asks for is
/// the part worth pinning: the coordinates it owns and the criteria it forwards, unchanged. The
/// reason is pinned too — it is what the sanctioned trainer is told, word for word, and a dialog
/// that dropped it would produce a sanction nobody can explain.
/// </remarks>
public sealed class TrainersTests : ComponentTest
{
    private readonly Mock<IAdministrationClient> _administration = new();
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>Trainers tests.</summary>
    public TrainersTests()
    {
        Services.AddSingleton(_administration.Object);
        Services.AddSingleton(_dialogs.Object);

        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(page: 1, totalPages: 1, totalCount: 0));
    }

    /// <summary>
    /// Renders, asks the server for the first page and lets it choose the size.
    /// </summary>
    /// <remarks>
    /// The size travels unset on purpose: the default is the server's to choose and the cap the
    /// server's to hold (ADR 0029), so the page names only the coordinate it owns.
    /// </remarks>
    [Fact]
    public void Renders_AsksTheServerForTheFirstPage_AndLetsItChooseTheSize()
    {
        // Act
        Render<Trainers>();

        // Assert
        _administration.Verify(client => client.GetTrainersAsync(null, null, 1, null), Times.Once);
    }

    /// <summary>
    /// Renders, a suspended trainer, shows the reason beside them.
    /// </summary>
    /// <remarks>
    /// The one column this API publishes nowhere else. Without it a suspension reads as an
    /// unexplained absence, which is the state ADR 0052 gave a reason in order to prevent.
    /// </remarks>
    [Fact]
    public void Renders_ASuspendedTrainer_ShowsTheReasonBesideThem()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Suspended("Ada", "Lovelace", "Repeated breaches.")));

        // Act
        var page = Render<Trainers>();

        // Assert
        page.Markup.Should().Contain("Repeated breaches.");
        page.Markup.Should().Contain("Suspended");
    }

    /// <summary>
    /// Choosing a standing, forwards it to the server unchanged.
    /// </summary>
    /// <remarks>
    /// The two words are the domain's own (ADR 0050) and the server compares them ordinally, so a
    /// page that translated or lower-cased them would earn a 400 rather than a filtered list.
    /// </remarks>
    [Fact]
    public async Task ChoosingAStanding_ForwardsItToTheServerUnchanged()
    {
        // Arrange
        var page = Render<Trainers>();
        var standing = page.FindComponent<MudSelect<string>>();

        // Act
        await page.InvokeAsync(() => standing.Instance.ValueChanged.InvokeAsync("Suspended"));

        // Assert
        page.WaitForAssertion(() => _administration.Verify(
            client => client.GetTrainersAsync("Suspended", null, 1, null), Times.Once));
    }

    /// <summary>
    /// Searching while standing on a later page, asks for the first page of the new question.
    /// </summary>
    /// <remarks>
    /// A term narrows the set, so the page number the caller was standing on describes something
    /// else once it changes: keeping it would show them page three of a list that may not have one,
    /// which the server answers by clamping — an empty screen for a search that matched.
    /// </remarks>
    [Fact]
    public async Task SearchingWhileStandingOnALaterPage_AsksForTheFirstPageOfTheNewQuestion()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 3, 45, Active("Ada", "Lovelace")));

        var page = Render<Trainers>();
        page.FindAll("button").Single(button => button.TextContent.Trim() == "2").Click();

        var search = page.FindComponent<MudTextField<string>>();

        // Act
        await page.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("lovelace"));

        // Assert
        page.WaitForAssertion(() => _administration.Verify(
            client => client.GetTrainersAsync(null, "lovelace", 1, null), Times.Once));
    }

    /// <summary>
    /// Renders, one page of trainers, shows no pager.
    /// </summary>
    [Fact]
    public void Renders_OnePageOfTrainers_ShowsNoPager()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Active("Ada", "Lovelace")));

        // Act
        var page = Render<Trainers>();

        // Assert
        page.Markup.Should().NotContain("mud-pagination");
    }

    /// <summary>
    /// Walking to another page, asks the server for that page.
    /// </summary>
    [Fact]
    public void WalkingToAnotherPage_AsksTheServerForThatPage()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 3, 45, Active("Ada", "Lovelace")));

        var page = Render<Trainers>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "2").Click();

        // Assert
        page.WaitForAssertion(() =>
            _administration.Verify(client => client.GetTrainersAsync(null, null, 2, null), Times.Once));
    }

    /// <summary>
    /// Suspending, the reason the dialog collected, is what reaches the API.
    /// </summary>
    /// <remarks>
    /// The seam that matters is between the dialog's answer and the request body: a page that sent
    /// an empty reason — or its own wording — would produce a sanction the administrator never
    /// wrote, and the trainer is told this text word for word. The dialog's own refusal to hand
    /// back an empty one is pinned where it lives, in <c>ReasonDialogTests</c>.
    /// </remarks>
    [Fact]
    public void Suspending_TheReasonTheDialogCollected_IsWhatReachesTheApi()
    {
        // Arrange
        var trainer = Active("Ada", "Lovelace");

        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, trainer));

        Answering("Repeated breaches of the content policy.");

        var page = Render<Trainers>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Suspend").Click();

        // Assert
        page.WaitForAssertion(() => _administration.Verify(
            client => client.SuspendTrainerAsync(
                trainer.Id,
                It.Is<SuspendTrainerHttpRequest>(body =>
                    body.Reason == "Repeated breaches of the content policy.")),
            Times.Once));
    }

    /// <summary>
    /// Suspending, the dialog dismissed, asks the API for nothing.
    /// </summary>
    /// <remarks>
    /// Backing out of the dialog is not a decision to suspend somebody, and a page that read a
    /// cancelled result as an empty reason would send one anyway.
    /// </remarks>
    [Fact]
    public void Suspending_TheDialogDismissed_AsksTheApiForNothing()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Active("Ada", "Lovelace")));

        Answering(reason: null);

        var page = Render<Trainers>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Suspend").Click();

        // Assert
        _administration.Verify(
            client => client.SuspendTrainerAsync(It.IsAny<Guid>(), It.IsAny<SuspendTrainerHttpRequest>()),
            Times.Never);
    }

    /// <summary>
    /// Reinstating, asks for no reason at all.
    /// </summary>
    /// <remarks>
    /// A lifting restores rather than decides, so there is nothing to record about it that the
    /// trainer needs to read (ADR 0050). Asserted on the dialog never opening, because the natural
    /// symmetry with suspending is exactly the mistake.
    /// </remarks>
    [Fact]
    public void Reinstating_AsksForNoReasonAtAll()
    {
        // Arrange
        var trainer = Suspended("Ada", "Lovelace", "Repeated breaches.");

        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, trainer));

        var page = Render<Trainers>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Reinstate").Click();

        // Assert
        page.WaitForAssertion(() =>
            _administration.Verify(client => client.ReinstateTrainerAsync(trainer.Id), Times.Once));

        _dialogs.Verify(
            service => service.ShowAsync<ReasonDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ReasonDialog>>(), It.IsAny<DialogOptions>()),
            Times.Never);
    }

    /// <summary>
    /// Loading, the api was unreachable, does not show the generator's own sentence.
    /// </summary>
    [Fact]
    public void Loading_TheApiWasUnreachable_DoesNotShowTheGeneratorsOwnSentence()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(Unreachable());

        // Act
        var page = Render<Trainers>();

        // Assert
        page.WaitForAssertion(() => Shown().Should().ContainSingle()
            .Which.Message.Should().Be("The trainers could not be loaded. Try again in a moment."));
    }

    /// <summary>
    /// Loading, the server refused with a document, shows what the server wrote.
    /// </summary>
    [Fact]
    public void Loading_TheServerRefusedWithADocument_ShowsWhatTheServerWrote()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainersAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(new ApiException<ProblemDetails>(
                "refused",
                400,
                "",
                new Dictionary<string, IEnumerable<string>>(),
                new ProblemDetails { Detail = "The Status field must name a status this API knows." },
                null));

        // Act
        var page = Render<Trainers>();

        // Assert
        page.WaitForAssertion(() => Shown().Should().ContainSingle()
            .Which.Message.Should().Be("The Status field must name a status this API knows."));
    }

    /// <summary>
    /// Arms the dialog to answer <paramref name="reason"/>, or to be dismissed when it is null.
    /// </summary>
    /// <remarks>
    /// The dialog is substituted rather than driven, because MudBlazor renders it through a
    /// provider that lives in the layout: a page rendered on its own has nowhere to put one. What
    /// the dialog itself does with what is typed into it is pinned where it lives.
    /// </remarks>
    private void Answering(string? reason)
    {
        var reference = new Mock<IDialogReference>();

        reference
            .Setup(dialog => dialog.Result)
            .ReturnsAsync(reason is null ? DialogResult.Cancel() : DialogResult.Ok(reason));

        _dialogs
            .Setup(service => service.ShowAsync<ReasonDialog>(
                It.IsAny<string>(), It.IsAny<DialogParameters<ReasonDialog>>(), It.IsAny<DialogOptions>()))
            .ReturnsAsync(reference.Object);
    }

    private static ApiException Unreachable() => new(
        "The HTTP status code of the response was not expected (503).",
        503,
        "",
        new Dictionary<string, IEnumerable<string>>(),
        null);

    private static AdministrationTrainerHttpResponse Active(string firstname, string lastname)
    {
        return new AdministrationTrainerHttpResponse
        {
            Id = Guid.NewGuid(),
            Firstname = firstname,
            Lastname = lastname,
            ContactEmail = $"{firstname}@example.com",
            Status = "Active"
        };
    }

    private static AdministrationTrainerHttpResponse Suspended(
        string firstname, string lastname, string reason)
    {
        var trainer = Active(firstname, lastname);
        trainer.Status = "Suspended";
        trainer.SuspensionReason = reason;

        return trainer;
    }

    private static PagedHttpResponseOfAdministrationTrainerHttpResponse Page(
        int page,
        int totalPages,
        int totalCount,
        params AdministrationTrainerHttpResponse[] trainers)
    {
        return new PagedHttpResponseOfAdministrationTrainerHttpResponse
        {
            Items = [.. trainers],
            Page = page,
            PageSize = 20,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasNextPage = page < totalPages,
            HasPreviousPage = page > 1
        };
    }
}
