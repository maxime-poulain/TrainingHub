using System.Linq.Expressions;
using TrainingHub.Shared.Application.Dtos.Training;
using TrainingHub.Shared.Domain.Aggregates.TrainingAggregate;

namespace TrainingHub.Shared.Application.Projections;

/// <summary>
/// The single description of how a <see cref="Training"/> becomes a <see cref="TrainingDto"/>.
/// </summary>
/// <remarks>
/// Written once as an <see cref="Expression"/> and consumed two ways: the CQRS query handlers hand
/// <see cref="ToDtoExpression"/> to EF Core, which translates it into the <c>SELECT</c> list, while
/// the layered application services call <see cref="ToDto"/> on aggregates already in memory.
/// <para>
/// The expression is the source and the delegate the derivative, never the reverse — an expression
/// can always be compiled, a compiled delegate can never be translated to SQL. Keeping the two
/// mappings side by side, as they used to be, meant a field added to the DTO could reach one stack
/// and silently stay <see langword="null"/> on the other.
/// </para>
/// <para>
/// Consequence to keep in mind when editing: the body must stay EF-translatable, which is a
/// stricter rule than C#. Null-conditional access is the usual casualty — see
/// <see cref="TrainerProjections"/>, where a ternary stands in for <c>?.</c>.
/// </para>
/// </remarks>
public static class TrainingProjections
{
    /// <summary>
    /// The mapping itself, in the form EF Core can translate.
    /// </summary>
    public static readonly Expression<Func<Training, TrainingDto>> ToDtoExpression = training => new TrainingDto
    {
        RowVersion = training.RowVersion,
        Id = training.Id.Value,
        Title = training.Title.Value,
        TrainerId = training.TrainerId.Value,
        Topics = training.Topics.Select(topic => topic.Name).ToList(),
        Description = training.Description.Value,
        Prerequisites = training.Prerequisites.Value,
        AcquiredSkills = training.AcquiredSkills.Value,
        Status = training.Status.Name
    };

    // Compiled once for the lifetime of the process: compilation is expensive enough that doing
    // it per call would be a poor trade for what is otherwise a field-by-field copy.
    private static readonly Func<Training, TrainingDto> Compiled = ToDtoExpression.Compile();

    /// <summary>
    /// Maps an aggregate already loaded in memory.
    /// </summary>
    public static TrainingDto ToDto(this Training training) => Compiled(training);

    /// <summary>
    /// Maps a sequence of aggregates already loaded in memory.
    /// </summary>
    public static List<TrainingDto> ToDtos(this IEnumerable<Training> trainings) => [.. trainings.Select(ToDto)];

    /// <summary>
    /// Maps an aggregate already loaded in memory, for the administration.
    /// </summary>
    /// <remarks>
    /// A second shape rather than a superset of the first (ADR 0055): two audiences, two shapes,
    /// and no column that crosses between them by accident. This one names six members where the
    /// other names nine, and they are not a subset — a moderation list does not read a training's
    /// content, and it does read whose training it is.
    /// <para>
    /// <strong>A method rather than an expression, and the owner's name arrives as an argument.</strong>
    /// Its sibling above is an <see cref="Expression"/> because the CQRS reader hands it to EF Core;
    /// this one cannot be, because the column it needs is not on the aggregate. A <c>Training</c>
    /// knows its owner's identifier and has never known their name — that is what makes them two
    /// aggregates — so the layered reader looks the names up for the whole page and passes each row
    /// its own. The row is still built exactly once, which is the point of taking the name here
    /// rather than filling it in afterwards (ADR 0044).
    /// </para>
    /// <para>
    /// The CQRS reader gets the same column in the same statement instead, because it can see the
    /// trainers' table and this cannot. Two mechanisms for one shape, held together by an
    /// end-to-end fact both hosts answer — the same arrangement as every other paged read here.
    /// </para>
    /// </remarks>
    /// <param name="training">The training to map.</param>
    /// <param name="trainerName">
    /// Its owner's name, or <see langword="null"/> when no trainer answers to its identifier.
    /// </param>
    public static AdministrationTrainingDto ToAdministrationDto(this Training training, string? trainerName)
    {
        ArgumentNullException.ThrowIfNull(training);

        return new AdministrationTrainingDto
        {
            Id = training.Id.Value,
            TrainerId = training.TrainerId.Value,
            TrainerName = trainerName,
            Title = training.Title.Value,
            Status = training.Status.Name,
            WithholdingReason = training.WithholdingReason?.Value
        };
    }
}
