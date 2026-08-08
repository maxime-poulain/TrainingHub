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
/// Behaviour covered for the administrative trainings page.
/// </summary>
/// <remarks>
/// The screen a moderator decides on, so what it shows about a row is as much the subject as what
/// it sends: an owner named by a GUID answers "whose is this?" with a value nobody can read, and a
/// withheld training that looked merely unpublished would hide the one state ADR 0052 gave a
/// reason to.
/// </remarks>
public sealed class TrainingsTests : ComponentTest
{
    private readonly Mock<IAdministrationClient> _administration = new();
    private readonly Mock<IDialogService> _dialogs = new();

    /// <summary>Trainings tests.</summary>
    public TrainingsTests()
    {
        Services.AddSingleton(_administration.Object);
        Services.AddSingleton(_dialogs.Object);

        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
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
        Render<Trainings>();

        // Assert
        _administration.Verify(client => client.GetTrainingsAsync(null, 1, null), Times.Once);
    }

    /// <summary>
    /// Renders, a training whose owner is known, names them rather than showing their identifier.
    /// </summary>
    /// <remarks>
    /// The column this whole page cost a join to obtain. Asserting on the name alone would pass
    /// against a row that also printed the GUID beside it, so the identifier's absence is asserted
    /// too — showing both is the outcome the enrichment was meant to replace.
    /// </remarks>
    [Fact]
    public void Renders_ATrainingWhoseOwnerIsKnown_NamesThemRatherThanShowingTheirIdentifier()
    {
        // Arrange
        var training = Published("Domain-Driven Design", "Ada Lovelace");

        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, training));

        // Act
        var page = Render<Trainings>();

        // Assert
        page.Markup.Should().Contain("Ada Lovelace");
        page.Markup.Should().NotContain(training.TrainerId.ToString());
    }

    /// <summary>
    /// Renders, a training whose owner is gone, keeps the row and says what is missing.
    /// </summary>
    /// <remarks>
    /// A moderator has to be able to act on a training whose owner has since left, so an unknown
    /// name drops the name and never the row — the failure mode an inner join would have produced
    /// silently.
    /// </remarks>
    [Fact]
    public void Renders_ATrainingWhoseOwnerIsGone_KeepsTheRowAndSaysWhatIsMissing()
    {
        // Arrange
        var training = Published("Domain-Driven Design", trainerName: null);

        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, training));

        // Act
        var page = Render<Trainings>();

        // Assert
        page.Markup.Should().Contain("Domain-Driven Design");
        page.Markup.Should().Contain("No longer a trainer");
    }

    /// <summary>
    /// Renders, a withheld training, shows the reason beside it.
    /// </summary>
    [Fact]
    public void Renders_AWithheldTraining_ShowsTheReasonBesideIt()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Withheld("Domain-Driven Design", "Plagiarised material.")));

        // Act
        var page = Render<Trainings>();

        // Assert
        page.Markup.Should().Contain("Plagiarised material.");
        page.Markup.Should().Contain("Withheld");
    }

    /// <summary>
    /// Choosing a state, forwards it to the server unchanged.
    /// </summary>
    /// <remarks>
    /// The three words are the domain's own (ADR 0052) and the server compares them ordinally, so a
    /// page that translated or lower-cased them would earn a 400 rather than a filtered list.
    /// </remarks>
    [Fact]
    public async Task ChoosingAState_ForwardsItToTheServerUnchanged()
    {
        // Arrange
        var page = Render<Trainings>();
        var state = page.FindComponent<MudSelect<string>>();

        // Act
        await page.InvokeAsync(() => state.Instance.ValueChanged.InvokeAsync("Withheld"));

        // Assert
        page.WaitForAssertion(() =>
            _administration.Verify(client => client.GetTrainingsAsync("Withheld", 1, null), Times.Once));
    }

    /// <summary>
    /// Renders, one page of trainings, shows no pager.
    /// </summary>
    [Fact]
    public void Renders_OnePageOfTrainings_ShowsNoPager()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Published("Domain-Driven Design", "Ada Lovelace")));

        // Act
        var page = Render<Trainings>();

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
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 3, 45, Published("Domain-Driven Design", "Ada Lovelace")));

        var page = Render<Trainings>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "2").Click();

        // Assert
        page.WaitForAssertion(() =>
            _administration.Verify(client => client.GetTrainingsAsync(null, 2, null), Times.Once));
    }

    /// <summary>
    /// Withholding, the reason the dialog collected, is what reaches the API.
    /// </summary>
    /// <remarks>
    /// The reason is what the owner is told, word for word, so a page that sent its own wording
    /// would produce an interdiction the moderator never wrote. The dialog's own refusal to hand
    /// back an empty one is pinned where it lives, in <c>ReasonDialogTests</c>.
    /// </remarks>
    [Fact]
    public void Withholding_TheReasonTheDialogCollected_IsWhatReachesTheApi()
    {
        // Arrange
        var training = Published("Domain-Driven Design", "Ada Lovelace");

        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, training));

        Answering("Plagiarised material.");

        var page = Render<Trainings>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Withhold").Click();

        // Assert
        page.WaitForAssertion(() => _administration.Verify(
            client => client.WithholdTrainingAsync(
                training.Id,
                It.Is<WithholdTrainingHttpRequest>(body => body.Reason == "Plagiarised material.")),
            Times.Once));
    }

    /// <summary>
    /// Withholding, the dialog dismissed, asks the API for nothing.
    /// </summary>
    [Fact]
    public void Withholding_TheDialogDismissed_AsksTheApiForNothing()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Published("Domain-Driven Design", "Ada Lovelace")));

        Answering(reason: null);

        var page = Render<Trainings>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Withhold").Click();

        // Assert
        _administration.Verify(
            client => client.WithholdTrainingAsync(
                It.IsAny<Guid>(), It.IsAny<WithholdTrainingHttpRequest>()),
            Times.Never);
    }

    /// <summary>
    /// Releasing, asks for no reason at all.
    /// </summary>
    /// <remarks>
    /// Lifting an interdiction takes nothing away from anybody (ADR 0052). Asserted on the dialog
    /// never opening, because the natural symmetry with withholding is exactly the mistake.
    /// </remarks>
    [Fact]
    public void Releasing_AsksForNoReasonAtAll()
    {
        // Arrange
        var training = Withheld("Domain-Driven Design", "Plagiarised material.");

        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, training));

        var page = Render<Trainings>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Release").Click();

        // Assert
        page.WaitForAssertion(() =>
            _administration.Verify(client => client.ReleaseTrainingAsync(training.Id), Times.Once));

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
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ThrowsAsync(Unreachable());

        // Act
        var page = Render<Trainings>();

        // Assert
        page.WaitForAssertion(() => Shown().Should().ContainSingle()
            .Which.Message.Should().Be("The trainings could not be loaded. Try again in a moment."));
    }

    /// <summary>
    /// Withholding, the server refused with a document, shows what the server wrote.
    /// </summary>
    /// <remarks>
    /// A training already withheld is the refusal a moderator actually meets, and the domain's own
    /// sentence says more than anything this page could invent.
    /// </remarks>
    [Fact]
    public void Withholding_TheServerRefusedWithADocument_ShowsWhatTheServerWrote()
    {
        // Arrange
        _administration
            .Setup(client => client.GetTrainingsAsync(
                It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int?>()))
            .ReturnsAsync(Page(1, 1, 1, Published("Domain-Driven Design", "Ada Lovelace")));

        _administration
            .Setup(client => client.WithholdTrainingAsync(
                It.IsAny<Guid>(), It.IsAny<WithholdTrainingHttpRequest>()))
            .ThrowsAsync(Refused("This training is already withheld."));

        Answering("Plagiarised material.");

        var page = Render<Trainings>();

        // Act
        page.FindAll("button").Single(button => button.TextContent.Trim() == "Withhold").Click();

        // Assert
        page.WaitForAssertion(() => Shown().Should().ContainSingle()
            .Which.Message.Should().Be("This training is already withheld."));
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

    private static ApiException<ProblemDetails> Refused(string detail) => new(
        "refused",
        409,
        "",
        new Dictionary<string, IEnumerable<string>>(),
        new ProblemDetails { Detail = detail },
        null);

    private static AdministrationTrainingHttpResponse Published(string title, string? trainerName)
    {
        return new AdministrationTrainingHttpResponse
        {
            Id = Guid.NewGuid(),
            TrainerId = Guid.NewGuid(),
            TrainerName = trainerName,
            Title = title,
            Status = "Published"
        };
    }

    private static AdministrationTrainingHttpResponse Withheld(string title, string reason)
    {
        var training = Published(title, "Ada Lovelace");
        training.Status = "Withheld";
        training.WithholdingReason = reason;

        return training;
    }

    private static PagedHttpResponseOfAdministrationTrainingHttpResponse Page(
        int page,
        int totalPages,
        int totalCount,
        params AdministrationTrainingHttpResponse[] trainings)
    {
        return new PagedHttpResponseOfAdministrationTrainingHttpResponse
        {
            Items = [.. trainings],
            Page = page,
            PageSize = 20,
            TotalCount = totalCount,
            TotalPages = totalPages,
            HasNextPage = page < totalPages,
            HasPreviousPage = page > 1
        };
    }
}
