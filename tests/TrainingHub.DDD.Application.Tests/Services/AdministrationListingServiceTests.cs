using AwesomeAssertions;
using TrainingHub.DDD.Application.Tests.Helpers;
using TrainingHub.Shared.Application.Dtos.Trainer;
using TrainingHub.Shared.Application.Dtos.Training;
using TrainingHub.Shared.Common.Pagination;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate.ValueObjects;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate.ValueObjects;
using TrainingHub.Shared.Domain.Tests.Helpers;
using Moq;
using Xunit;

namespace TrainingHub.DDD.Application.Tests.Services;

/// <summary>
/// Behaviour covered for the administrative listings of both layered application services.
/// </summary>
/// <remarks>
/// What the criteria mean is the repository's, proven where the SQL is. What only this layer decides
/// is the two things asserted here: the criteria reach the named question unchanged, and the paging
/// envelope crosses the mapping intact.
/// <para>
/// The second is the one worth a test. A page carries a count about a set and items about the same
/// set, and rebuilding the envelope by hand after mapping is how the two end up describing different
/// ones — a defect a pager renders as perfectly plausible numbers.
/// </para>
/// </remarks>
public sealed class AdministrationListingServiceTests
{
    private readonly TrainerServiceTestFixture _trainers = new();
    private readonly TrainingServiceTestFixture _trainings = new();

    /// <summary>
    /// Get administered page async, a standing and a term, asks the repository for exactly those.
    /// </summary>
    [Fact]
    public async Task GetAdministeredPageAsync_AStandingAndATerm_AsksTheRepositoryForExactlyThose()
    {
        _trainers.TrainerRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainerStatus?>(), It.IsAny<string?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Trainer>([], 1, 20, 0));

        var paging = new PageRequest { Page = 3, PageSize = 5 };

        await _trainers.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainerRequest { Status = TrainerStatus.Suspended, Search = "lovelace" }, paging);

        _trainers.TrainerRepository.Verify(
            repository => repository.GetPageAsync(
                TrainerStatus.Suspended, "lovelace", paging, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Get administered page async, a page of trainers, maps them and keeps the envelope.
    /// </summary>
    [Fact]
    public async Task GetAdministeredPageAsync_APageOfTrainers_MapsThemAndKeepsTheEnvelope()
    {
        var suspended = new TrainerBuilder().Build();
        suspended.Suspend(SuspensionReason.Create("Repeated breaches.").ShouldBeSuccess()).ShouldBeSuccess();

        _trainers.TrainerRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainerStatus?>(), It.IsAny<string?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Trainer>([suspended], Page: 2, PageSize: 5, TotalCount: 11));

        var page = await _trainers.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainerRequest(), new PageRequest { Page = 2, PageSize = 5 });

        // The counts describe the whole set and survive the mapping, which is what lets a caller
        // build a pager from what they were handed rather than from what they guessed.
        page.Page.Should().Be(2);
        page.PageSize.Should().Be(5);
        page.TotalCount.Should().Be(11);
        page.TotalPages.Should().Be(3);
        page.HasNextPage.Should().BeTrue();
        page.HasPreviousPage.Should().BeTrue();

        var row = page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(suspended.Id.Value);
        row.Status.Should().Be(TrainerStatus.Suspended.Name);
        row.SuspensionReason.Should().Be("Repeated breaches.");
    }

    /// <summary>
    /// Get administered page async, a page of trainings, names the owner it could only identify.
    /// </summary>
    /// <remarks>
    /// The column no aggregate can answer. A <c>Training</c> knows its owner's identifier and has
    /// never known their name, so this is the one row member the layered reader fetches rather than
    /// maps — and the one that would silently stay <see langword="null"/> if the lookup were
    /// dropped.
    /// </remarks>
    [Fact]
    public async Task GetAdministeredPageAsync_APageOfTrainings_NamesTheOwnerItCouldOnlyIdentify()
    {
        var withheld = await new TrainingBuilder().BuildValidAsync();

        _trainings.TrainingRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainingStatus?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Training>([withheld], 1, 20, 1));

        _trainings.TrainerNames
            .Setup(names => names.GetAsync(
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(withheld.TrainerId.Value)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [withheld.TrainerId.Value] = "Ada Lovelace" });

        var page = await _trainings.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainingRequest(), new PageRequest());

        page.Items.Should().ContainSingle().Which.TrainerName.Should().Be("Ada Lovelace");
    }

    /// <summary>
    /// Get administered page async, an owner nobody answers to, leaves the name unset rather than
    /// dropping the row.
    /// </summary>
    /// <remarks>
    /// A moderator has to see a training whose owner has since gone, not lose it from the list. The
    /// name is what is missing, not the row.
    /// </remarks>
    [Fact]
    public async Task GetAdministeredPageAsync_AnOwnerNobodyAnswersTo_LeavesTheNameUnsetRatherThanDroppingTheRow()
    {
        var orphaned = await new TrainingBuilder().BuildValidAsync();

        _trainings.TrainingRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainingStatus?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Training>([orphaned], 1, 20, 1));

        var page = await _trainings.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainingRequest(), new PageRequest());

        var row = page.Items.Should().ContainSingle().Subject;
        row.Id.Should().Be(orphaned.Id.Value);
        row.TrainerName.Should().BeNull();
    }

    /// <summary>
    /// Get administered page async, a page of trainings, carries the withholding reason out.
    /// </summary>
    /// <remarks>
    /// The one column that exists nowhere else on this API. Without it a withheld training reads as
    /// merely "not published", which is the state ADR 0052 exists to keep distinguishable.
    /// </remarks>
    [Fact]
    public async Task GetAdministeredPageAsync_APageOfTrainings_CarriesTheWithholdingReasonOut()
    {
        var withheld = await new TrainingBuilder().BuildValidAsync();
        withheld.Withhold(WithholdingReason.Create("Misleading claims.").ShouldBeSuccess()).ShouldBeSuccess();

        _trainings.TrainingRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainingStatus?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Training>([withheld], 1, 20, 1));

        var page = await _trainings.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainingRequest { Status = TrainingStatus.Withheld }, new PageRequest());

        var row = page.Items.Should().ContainSingle().Subject;
        row.Status.Should().Be(TrainingStatus.Withheld.Name);
        row.WithholdingReason.Should().Be("Misleading claims.");
        row.TrainerId.Should().Be(withheld.TrainerId.Value);
    }

    /// <summary>
    /// Get administered page async, nothing matching, answers an empty page rather than nothing.
    /// </summary>
    /// <remarks>
    /// A page of nothing is still page 1 of 1, which is what a pager needs in order to render at
    /// all — the reason <c>TotalPages</c> is derived rather than divided.
    /// </remarks>
    [Fact]
    public async Task GetAdministeredPageAsync_NothingMatching_AnswersAnEmptyPageRatherThanNothing()
    {
        _trainings.TrainingRepository
            .Setup(repository => repository.GetPageAsync(
                It.IsAny<TrainingStatus?>(), It.IsAny<PageRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<Training>([], 1, 20, 0));

        var page = await _trainings.CreateSut().GetAdministeredPageAsync(
            new AdministrationTrainingRequest(), new PageRequest());

        page.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
        page.TotalPages.Should().Be(1);
        page.HasNextPage.Should().BeFalse();
    }
}
