using AgenticMemory.Persistence.Migrations.Steps;

namespace AgenticMemory.Persistence.Migrations;

/// <summary>
/// The ordered list of everything that has ever changed the shape of the stored data, and therefore
/// the definition of what "current" means for a database file.
///
/// To add a migration: write a step, give it the next version number, append it here. That is the
/// whole procedure — <see cref="Current"/> is derived, so there is no second place to remember to
/// bump and no way for the two to disagree.
/// </summary>
public static class DatabaseSchema
{
    /// <summary>
    /// What an unstamped database that already holds data is assumed to be.
    ///
    /// Version 1 is the original schema, from before any of this existed. Databases written then
    /// carry no version of their own, so they are recognised by having documents but no stamp, and
    /// every step from 2 upwards is applied to them.
    /// </summary>
    public const int Baseline = 1;

    /// <summary>
    /// Ordered by version. Append only — see <see cref="IMigrationStep"/> for why a shipped step is
    /// never edited in place.
    /// </summary>
    public static IReadOnlyList<IMigrationStep> Steps { get; } =
    [
        new ScopedMemorySchemaStep(),      // 2
        new ProjectsToWorkspacesStep(),    // 3
    ];

    /// <summary>
    /// The schema version this build understands. A fresh database is born at this version; an older
    /// one is brought up to it; a database claiming anything higher is refused.
    /// </summary>
    public static int Current { get; } = Steps.Count == 0 ? Baseline : Steps[^1].Version;

    /// <summary>The steps needed to bring a database at <paramref name="fromVersion"/> up to date.</summary>
    public static IReadOnlyList<IMigrationStep> Pending(int fromVersion) =>
        Steps.Where(step => step.Version > fromVersion).OrderBy(step => step.Version).ToList();
}
