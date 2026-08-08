namespace TrainingHub.Shared.Application.Dtos.Training;

/// <summary>
/// A training as the administration reads it: what it is called, whose it is, and where it stands.
/// </summary>
/// <remarks>
/// A second shape rather than fields added to <see cref="TrainingDto"/>, for the reason ADR 0055
/// gives: two audiences, two shapes, and no field that can cross between them by accident.
/// <para>
/// The content is deliberately absent — no description, no prerequisites, no acquired skills, no
/// topics. Moderating a training means deciding whether it may stay on offer, and that decision is
/// taken on the full training at its own address rather than on a list row; carrying the whole
/// content into a page of twenty would read four columns nobody looks at, twenty times.
/// </para>
/// <para>
/// The owner is here because it is the one thing a list row cannot be understood without: a
/// withheld training belongs to somebody, and the next question an administrator asks is always
/// about them. It travels as both an identifier and a name — the identifier is what the
/// administrative endpoints take, the name is what a person reads, and a row carrying only the
/// first answers the question with a value nobody can read.
/// </para>
/// </remarks>
public sealed class AdministrationTrainingDto
{
    /// <summary>The training's identifier, which the administrative endpoints take.</summary>
    public required Guid Id { get; init; }

    /// <summary>The trainer who owns it.</summary>
    public required Guid TrainerId { get; init; }

    /// <summary>
    /// That trainer's name, or <see langword="null"/> when no trainer answers to
    /// <see cref="TrainerId"/> any more.
    /// </summary>
    /// <remarks>
    /// Not on the aggregate — a <c>Training</c> knows its owner's identifier and has never known
    /// their name — so it is read alongside the page rather than projected from the row.
    /// </remarks>
    public string? TrainerName { get; init; }

    /// <summary>The training's title.</summary>
    public required string Title { get; init; }

    /// <summary>Whether the training is published, withdrawn by its owner, or withheld.</summary>
    public required string Status { get; init; }

    /// <summary>
    /// Why the training was withheld, or <see langword="null"/> when it was not.
    /// </summary>
    public string? WithholdingReason { get; init; }
}
