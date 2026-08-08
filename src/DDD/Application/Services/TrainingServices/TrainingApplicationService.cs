using TrainingHub.Shared;
using TrainingHub.Shared.Application.Projections;
using TrainingHub.Shared.Application.Queries;
using TrainingHub.Shared.Application.Dtos.Training;
using TrainingHub.Shared.Common;
using TrainingHub.Shared.Common.Errors;
using TrainingHub.Shared.Common.Pagination;
using TrainingHub.Shared.Common.Results;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate.ValueObjects;
using TrainingHub.Shared.Application.Factories;

namespace TrainingHub.DDD.Application.Services.TrainingServices;

// A good alternative would have been to have one application service per use case.
// This would have allowed us to have a more granular control over the dependencies.
// Also it makes easier to understand what the underlying class does.
// Example: `ITrainingCreator` or `ITrainingCreationService` is more meaningful
// than `ITrainingApplicationService`.

/// <summary>
/// Application service interface for managing training operations (create, read, update, delete).
/// </summary>
public interface ITrainingApplicationService
{
    /// <summary>
    /// Creates a new training from the given request.
    /// </summary>
    Task<Result<TrainingDto>> CreateAsync(TrainingCreationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves one of the calling trainer's trainings by its identifier.
    /// </summary>
    /// <remarks>
    /// A training belonging to another trainer is reported as not found. The identifier is the
    /// caller's to supply, so this read is scoped rather than argument-free — but it answers only
    /// about what they own.
    /// </remarks>
    Task<Result<TrainingDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Edits an existing training identified by <paramref name="trainingId"/>.
    /// </summary>
    Task<Result<TrainingDto>> EditAsync(Guid trainingId, TrainingEditionRequest request, byte[] expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves one page of the trainings belonging to the calling trainer, newest first.
    /// </summary>
    /// <remarks>
    /// It takes no trainer: it resolves the caller itself, exactly as <see cref="CreateAsync"/> does
    /// for the owner of a new training. There used to be a sibling reading whichever trainer the
    /// caller named; it went with the endpoint in front of it, and having no parameter is what makes
    /// "only your own" true here rather than a convention.
    /// <para>
    /// Paged since ADR 0029: it used to answer the whole collection, which is a defect that grows
    /// with the data — and an asymmetry with the CQRS host that the harmonisation removed.
    /// </para>
    /// </remarks>
    Task<PagedResult<TrainingDto>> GetMineAsync(PageRequest paging, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a training by its unique identifier.
    /// </summary>
    Task<Result> DeleteAsync(Guid trainingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands a training over to another trainer, when the recipient's catalogue allows it
    /// (ADR 0036).
    /// </summary>
    Task<Result> TransferAsync(Guid trainingId, Guid recipientTrainerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Offers a withdrawn training to the public again (ADR 0050).
    /// </summary>
    Task<Result> PublishAsync(Guid trainingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws a training from public view, keeping it in its owner's own listing (ADR 0050).
    /// </summary>
    Task<Result> UnpublishAsync(Guid trainingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a training out of public view by administrative decision, with a stated reason
    /// (ADR 0052).
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="UnpublishAsync"/> in what it *permits* rather than in who called it:
    /// the owner can undo their own withdrawal and cannot undo this one, which is why the two land
    /// on different states. Who is entitled to it is settled at the API boundary and nowhere else;
    /// this layer never asks, and could not name the authority that answers without depending on
    /// the API to do so (ADR 0051).
    /// </remarks>
    /// <param name="trainingId">The training being withheld.</param>
    /// <param name="reason">Why, as the caller wrote it.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>Success, why the reason was refused, or why the training could not be withheld.</returns>
    Task<Result> WithholdAsync(Guid trainingId, string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lifts the interdiction on a withheld training, which lands on <c>Unpublished</c> (ADR 0052).
    /// </summary>
    /// <param name="trainingId">The training being released.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>Success, or a refusal when the training was not withheld.</returns>
    Task<Result> ReleaseAsync(Guid trainingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of trainings across every trainer, as the administration sees them, newest
    /// first (ADR 0055).
    /// </summary>
    /// <remarks>
    /// <see cref="GetMineAsync"/> takes no trainer because it resolves the caller, and this one
    /// takes none because it is about nobody in particular. That is the whole difference between the
    /// read this API withdrew and the read it now has: not the columns, but who may ask.
    /// <para>
    /// The state arrives already resolved, for the reason the trainer service's sibling gives: a
    /// name that no state answers to is refused at the boundary, where the parameter that carried it
    /// can be named.
    /// </para>
    /// </remarks>
    /// <param name="request">The state to narrow by.</param>
    /// <param name="paging">The page asked for.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>The page, empty when nothing matches.</returns>
    Task<PagedResult<AdministrationTrainingDto>> GetAdministeredPageAsync(
        AdministrationTrainingRequest request,
        PageRequest paging,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Implementation of <see cref="ITrainingApplicationService"/> that orchestrates
/// training CRUD operations using domain services and repositories.
/// </summary>
public sealed class TrainingApplicationService(
    ITrainerRepository trainerRepository,
    ITrainerNamesQuery trainerNames,
    IUniquenessTitleChecker uniquenessTitleChecker,
    ITrainingCounter trainingCounter,
    ITrainerStanding trainerStanding,
    ITrainingRepository trainingRepository,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : ITrainingApplicationService
{
    /// <inheritdoc />
    public async Task<Result<TrainingDto>> CreateAsync(TrainingCreationRequest request, CancellationToken cancellationToken = default)
    {
        var trainerId = TrainerId.Create(currentUserService.TrainerId);

        if (!await trainerRepository.ExistsAsync(trainerId, cancellationToken))
        {
            return Result<TrainingDto>.Failure(
                ErrorCodes.NotFound,
                $"Trainer with id `{trainerId.Value}` not found.");
        }

        var detailsResult = TrainingDetailsFactory.Create(
            request.Title, request.Description, request.Prerequisites, request.AcquiredSkills, request.Topics);

        var result = await detailsResult.MatchAsync(
            async details => await Training.CreateAsync(
                TrainingId.Generate(),
                trainerId,
                details.Title,
                details.Description,
                details.Prerequisites,
                details.AcquiredSkills,
                details.Topics,
                uniquenessTitleChecker,
                trainingCounter,
                trainerStanding,
                cancellationToken),
            Result<Training>.FailureAsync);

        return await result.MatchAsync(async training =>
        {
            trainingRepository.Add(training);
            try
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintViolationException)
            {
                // A concurrent request slipped past the uniqueness pre-check;
                // the unique index is the authoritative guard, so a lost race
                // is the same business failure as a detected duplicate.
                return Result<TrainingDto>.Failure(TrainingErrorCodes.DuplicateTitle,
                    "A training with the same title already exists for this trainer.");
            }
            return Result<TrainingDto>.Success(training.ToDto());
        }, Result<TrainingDto>.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<Result<TrainingDto>> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(id), cancellationToken);

        // A training belonging to somebody else is reported as not found, not as forbidden: a 403
        // would confirm that the identifier names something real. Both cases produce the same
        // sentence for the same reason. The ownership question itself is the domain's
        // (TrainingOwnedBySpecification, worn by the aggregate) — this layer only decides what
        // refusing it means over HTTP.
        if (training is null || !training.IsOwnedBy(TrainerId.Create(currentUserService.TrainerId)))
        {
            return Result<TrainingDto>.Failure(ErrorCodes.NotFound, $"Training with id `{id}` not found.");
        }

        return Result<TrainingDto>.Success(training.ToDto());
    }

    /// <inheritdoc />
    public async Task<Result<TrainingDto>> EditAsync(Guid trainingId, TrainingEditionRequest request, byte[] expectedVersion, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);

        if (training is null)
        {
            return Result<TrainingDto>.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        if (!training.IsAtVersion(expectedVersion))
        {
            return Result<TrainingDto>.Failure(ErrorCodes.ConcurrencyConflict, ConcurrencyMessage);
        }

        var detailsResult = TrainingDetailsFactory.Create(
            request.Title, request.Description, request.Prerequisites, request.AcquiredSkills, request.Topics);

        var result = await detailsResult.MatchAsync(
            async details => await training.EditAsync(
                details.Title,
                details.Description,
                details.Prerequisites,
                details.AcquiredSkills,
                details.Topics,
                uniquenessTitleChecker,
                cancellationToken),
            Result.FailureAsync);

        return await result.MatchAsync(
            onSuccess: async () =>
            {
                trainingRepository.Update(training);
                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (UniqueConstraintViolationException)
                {
                    // A concurrent request slipped past the uniqueness pre-check;
                    // the unique index is the authoritative guard, so a lost race
                    // is the same business failure as a detected duplicate.
                    return Result<TrainingDto>.Failure(TrainingErrorCodes.DuplicateTitle,
                        "A training with the same title already exists for this trainer.");
                }
                catch (ConcurrencyConflictException)
                {
                    // Same layering for the version: the concurrency token is the
                    // authoritative guard behind the pre-check above.
                    return Result<TrainingDto>.Failure(ErrorCodes.ConcurrencyConflict, ConcurrencyMessage);
                }
                return Result<TrainingDto>.Success(training.ToDto());
            },
            onFailure: Result<TrainingDto>.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<PagedResult<TrainingDto>> GetMineAsync(PageRequest paging, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paging);

        // Resolved here, not received: this read cannot be pointed at anybody else's trainings,
        // because there is no parameter to point it with.
        var trainerId = TrainerId.Create(currentUserService.TrainerId);

        // A page of aggregates, then a page of DTOs. Map carries the counts and the position
        // across unchanged — the repository decided what the page holds, this layer only decides
        // what each item looks like to a caller.
        var page = await trainingRepository.GetPageByTrainerIdAsync(trainerId, paging, cancellationToken);
        return page.Map(training => training.ToDto());
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync(Guid trainingId, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        // The aggregate announces its own disappearance before the repository stages it. Deleting
        // used to raise nothing at all, which is how a training could vanish from the database and
        // stay in the search index for ever (ADR 0050).
        training.MarkForDeletion();

        trainingRepository.Delete(training);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <inheritdoc />
    public async Task<Result> PublishAsync(Guid trainingId, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        var result = await training.PublishAsync(trainerStanding, trainingCounter, cancellationToken);

        return await result.MatchAsync(
            onSuccess: async () =>
            {
                trainingRepository.Update(training);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            onFailure: Result.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<Result> UnpublishAsync(Guid trainingId, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        var result = training.Unpublish();

        return await result.MatchAsync(
            onSuccess: async () =>
            {
                trainingRepository.Update(training);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            onFailure: Result.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<Result> WithholdAsync(Guid trainingId, string reason, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        // The reason is judged before the training is asked anything: a sanction whose motive the
        // domain refuses must leave no trace, and the aggregate takes a value object precisely so
        // that this layer cannot hand it a sentence nobody vetted.
        var reasonResult = WithholdingReason.Create(reason);

        return await reasonResult.MatchAsync<Result>(
            onSuccess: async withholdingReason => await training.Withhold(withholdingReason).MatchAsync(
                onSuccess: async () =>
                {
                    trainingRepository.Update(training);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return Result.Success();
                },
                onFailure: Result.FailureAsync),
            onFailure: Result.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<Result> ReleaseAsync(Guid trainingId, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        return await training.Release().MatchAsync(
            onSuccess: async () =>
            {
                trainingRepository.Update(training);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            onFailure: Result.FailureAsync);
    }

    /// <inheritdoc />
    public async Task<PagedResult<AdministrationTrainingDto>> GetAdministeredPageAsync(
        AdministrationTrainingRequest request,
        PageRequest paging,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A page of aggregates from the repository, mapped here — the layered half of ADR 0029.
        // Map carries the counts and the position across untouched.
        var page = await trainingRepository.GetPageAsync(request.Status, paging, cancellationToken);

        // The one column the aggregates cannot answer: a Training knows its owner's identifier and
        // has never known their name, which is what makes them two aggregates. One question for the
        // whole page rather than one per row, and the row is still built once — the name arrives as
        // an argument rather than being filled in afterwards.
        var names = await trainerNames.GetAsync(
            [.. page.Items.Select(training => training.TrainerId.Value)], cancellationToken);

        return page.Map(training =>
            training.ToAdministrationDto(names.GetValueOrDefault(training.TrainerId.Value)));
    }

    /// <inheritdoc />
    public async Task<Result> TransferAsync(Guid trainingId, Guid recipientTrainerId, CancellationToken cancellationToken = default)
    {
        var training = await trainingRepository.GetByIdAsync(TrainingId.Create(trainingId), cancellationToken);
        if (training is null)
        {
            return Result.Failure(
                ErrorCodes.NotFound,
                $"Training with id `{trainingId}` not found.");
        }

        // Existence is referential integrity, not part of the transfer decision: the domain
        // service is well-defined for any TrainerId, and this layer refuses a ghost recipient
        // before consulting it — the same precondition creation treats as orchestration.
        var recipient = TrainerId.Create(recipientTrainerId);
        if (!await trainerRepository.ExistsAsync(recipient, cancellationToken))
        {
            return Result.Failure(TrainingErrorCodes.UnknownRecipient,
                $"No trainer with id `{recipientTrainerId}` exists to receive the training.");
        }

        var result = await TrainingTransferDomainService.TransferAsync(
            training, recipient, trainingCounter, uniquenessTitleChecker, trainerStanding, cancellationToken);

        return await result.MatchAsync(
            onSuccess: async () =>
            {
                trainingRepository.Update(training);
                try
                {
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                }
                catch (UniqueConstraintViolationException)
                {
                    // A concurrent request slipped past the uniqueness pre-check;
                    // the unique index is the authoritative guard, so a lost race
                    // is the same business failure as a detected duplicate.
                    return Result.Failure(TrainingErrorCodes.DuplicateTitle,
                        "The recipient already has a training under that title.");
                }
                return Result.Success();
            },
            onFailure: Result.FailureAsync);
    }

    private const string ConcurrencyMessage =
        "The training was modified by someone else since it was read. Reload it and try again.";
}
