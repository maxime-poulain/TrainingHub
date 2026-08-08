namespace TrainingHub.Shared.Application.Queries;

/// <summary>
/// Reads the names of several trainers at once, for a list that has to say whose each row is.
/// </summary>
/// <remarks>
/// <para>
/// The administrative listing of trainings knows the owner's identifier and nothing else: a
/// <c>Training</c> is its own aggregate and has never been able to name the trainer behind it. A
/// row that showed that identifier would answer the question it was put there for — <em>whose is
/// this?</em> — with a value nobody can read.
/// </para>
/// <para>
/// A set in and a map out, on purpose: a page of twenty rows costs one query rather than twenty,
/// and the caller cannot turn this into a loop without noticing. It answers no entry at all for a
/// trainer nobody answers to, which is the shape that lets a caller tell "no such trainer" from
/// "a trainer with a blank name" — the same reason <see cref="ITrainingOwnerQuery"/> guards its
/// <see langword="null"/>.
/// </para>
/// <para>
/// The third of this project's read ports, beside <see cref="ITrainingOwnerQuery"/> and
/// <see cref="ITrainerAccountQuery"/>, and it is here for the reason they are: nothing on this path
/// changes a trainer, so nothing on it needs an aggregate.
/// </para>
/// </remarks>
public interface ITrainerNamesQuery
{
    /// <summary>
    /// The name of each trainer that exists among <paramref name="trainerIds"/>.
    /// </summary>
    /// <param name="trainerIds">The trainers to name. An empty set answers an empty map.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// A map from identifier to name, holding an entry only for the trainers that exist.
    /// </returns>
    Task<IReadOnlyDictionary<Guid, string>> GetAsync(
        IReadOnlyCollection<Guid> trainerIds,
        CancellationToken cancellationToken = default);
}
