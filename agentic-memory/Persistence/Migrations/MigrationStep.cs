using LiteDB;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Persistence.Migrations;

/// <summary>What a step is handed: the open database, and somewhere to say what it is doing.</summary>
public sealed class MigrationContext(LiteDatabase database, ILogger? logger)
{
    public LiteDatabase Database { get; } = database;
    public ILogger? Logger { get; } = logger;
}

/// <summary>
/// One irreversible move from schema version N-1 to N.
///
/// Three rules, because a step is run once on a user's only copy of their memories and then lives
/// forever in the history of every install that passed through it:
///
///   1. <b>A step is frozen once shipped.</b> Editing one after release means two databases claim the
///      same version with different contents, which nothing downstream can detect. Fix a bad step
///      with a new step at the next version.
///   2. <b>A step should not lean on current model types.</b> Those evolve; the step must keep
///      meaning what it meant on the day it shipped. Reading and writing raw
///      <see cref="BsonDocument"/> is the safe choice — see
///      <see cref="Steps.ProjectsToWorkspacesStep"/>. Where a step does use a typed entity it is
///      accepting that it will need replacing if that type changes.
///   3. <b>A step must be idempotent</b> where it cheaply can be. The runner commits the version
///      stamp in the same transaction as the work, so a step is not normally re-run after it
///      succeeds — but a step that tolerates a second pass is one less way to lose data if that
///      guarantee is ever weakened.
/// </summary>
public interface IMigrationStep
{
    /// <summary>The schema version the database is at once this step has committed.</summary>
    int Version { get; }

    /// <summary>Short identifier recorded in the migration history, e.g. "scoped-memory-schema".</summary>
    string Name { get; }

    /// <summary>Applies the change. Returns the number of documents touched, for the log.</summary>
    int Apply(MigrationContext context);
}
