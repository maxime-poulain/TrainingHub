using Microsoft.EntityFrameworkCore;
using TrainingHub.Shared.Application.Queries;
using TrainingHub.Shared.Domain.Aggregates.TrainerAggregate;
using TrainingHub.Shared.Infrastructure.ThirdParty.EfCore;

namespace TrainingHub.Shared.Infrastructure.Queries;

/// <inheritdoc />
/// <remarks>
/// One <c>IN</c> for the whole page. The identifiers are converted back to <see cref="TrainerId"/>
/// before the comparison rather than compared as <see cref="Guid"/>: the column is value-converted,
/// and EF Core translates equality against the converted type and not against what is underneath.
/// <para>
/// An empty set never reaches the database. Left to itself EF would emit <c>WHERE Id IN ()</c>, or
/// a constant-false predicate depending on the provider, for a question whose answer is already
/// known.
/// </para>
/// </remarks>
public sealed class TrainerNamesQuery(TrainingContext trainingContext) : ITrainerNamesQuery
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        IReadOnlyCollection<Guid> trainerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trainerIds);

        if (trainerIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = trainerIds.Distinct().Select(TrainerId.Create).ToList();

        var named = await trainingContext.Trainers
            .AsNoTracking()
            .Where(trainer => ids.Contains(trainer.Id))
            .Select(trainer => new
            {
                Id = trainer.Id.Value,
                trainer.Name.Firstname,
                trainer.Name.Lastname,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return named.ToDictionary(
            trainer => trainer.Id,
            trainer => $"{trainer.Firstname} {trainer.Lastname}");
    }
}
