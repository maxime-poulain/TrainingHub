using TrainingHub.DDD.Application.Services.TrainingServices;
using TrainingHub.Shared;
using TrainingHub.Shared.Application.Queries;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate;
using Moq;

namespace TrainingHub.DDD.Application.Tests.Helpers;

/// <summary>
/// Training service test fixture.
/// </summary>
public sealed class TrainingServiceTestFixture
{
    /// <summary>
    /// Trainer repository.
    /// </summary>
    public Mock<ITrainerRepository> TrainerRepository { get; } = new();

    /// <summary>
    /// Title checker.
    /// </summary>
    public Mock<IUniquenessTitleChecker> TitleChecker { get; } = new();

    /// <summary>
    /// Training counter. Answers zero unless a test raises it: an empty catalogue is the
    /// default, so only the tests about the capacity rule mention it.
    /// </summary>
    public Mock<ITrainingCounter> TrainingCounter { get; } = new();

    /// <summary>
    /// Training repository.
    /// </summary>
    public Mock<ITrainingRepository> TrainingRepository { get; } = new();

    /// <summary>
    /// Trainer standing — answers "not suspended" unless a test says otherwise (ADR 0050).
    /// </summary>
    public Mock<ITrainerStanding> TrainerStanding { get; } = new();

    /// <summary>
    /// Current user service.
    /// </summary>
    public Mock<ICurrentUserService> CurrentUserService { get; } = new();

    /// <summary>
    /// Unit of work.
    /// </summary>
    public Mock<IUnitOfWork> UnitOfWork { get; } = new();

    /// <summary>
    /// The names of a page's owners. Answers an empty map unless a test says otherwise, which is
    /// what a row with an owner nobody answers to looks like.
    /// </summary>
    public Mock<ITrainerNamesQuery> TrainerNames { get; } = Named([]);

    /// <summary>
    /// A names query answering exactly the map it is given.
    /// </summary>
    /// <remarks>
    /// Empty by default rather than left to Moq, which would answer a task carrying
    /// <see langword="null"/> and turn "this trainer has no name on file" — a state the row is
    /// designed for — into a reference exception three frames away.
    /// </remarks>
    /// <param name="names">The names to answer.</param>
    public static Mock<ITrainerNamesQuery> Named(Dictionary<Guid, string> names)
    {
        var query = new Mock<ITrainerNamesQuery>();

        query
            .Setup(names => names.GetAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(names);

        return query;
    }

    /// <summary>
    /// Create sut.
    /// </summary>
    public TrainingApplicationService CreateSut() => new(
        TrainerRepository.Object,
        TrainerNames.Object,
        TitleChecker.Object,
        TrainingCounter.Object,
        TrainerStanding.Object,
        TrainingRepository.Object,
        CurrentUserService.Object,
        UnitOfWork.Object);
}
