using System.Text.Json;
using System.Text.Json.Nodes;
using LiteDB;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Persistence.Migrations.Steps;

/// <summary>
/// v3 — reshapes the stored "projects" list into the "workspaces" list that replaced it.
///
/// This ran for a while as an unversioned startup fixup, guarded only by "has the new key appeared
/// yet". That works but is invisible: nothing recorded that it happened, it could not be ordered
/// against anything else, and it got neither the snapshot nor the transaction that a schema change
/// deserves. Same reshape, now with all three.
///
/// Deliberately written against raw JSON rather than the <c>WorkspaceRecord</c> type. A step has to
/// keep meaning what it meant on the day it shipped, and that type will keep evolving; binding to it
/// would quietly change what this migration produces for users upgrading from old versions.
/// </summary>
public sealed class ProjectsToWorkspacesStep : IMigrationStep
{
    public int    Version => 3;
    public string Name    => "projects-to-workspaces";

    private const string KeyValueCollection = "kv";
    private const string ProjectsKey        = "projects";
    private const string WorkspacesKey      = "workspaces";

    public int Apply(MigrationContext context)
    {
        var kv = context.Database.GetCollection(KeyValueCollection);

        // Already reshaped, either by an earlier run or by the startup fixup this replaced.
        if (kv.FindById(WorkspacesKey) is not null) return 0;

        var projects = ReadValue(kv, ProjectsKey);
        if (string.IsNullOrWhiteSpace(projects)) return 0;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(projects);
        }
        catch (JsonException ex)
        {
            // The old value is unreadable. Leaving it in place loses nothing — the workspaces list
            // is a convenience that the user can rebuild — and it is not worth failing startup over.
            context.Logger?.LogWarning(ex, "Stored project list is not valid JSON; leaving it alone");
            return 0;
        }

        if (parsed is not JsonArray array) return 0;

        var workspaces = new JsonArray();
        foreach (var project in array.OfType<JsonObject>())
        {
            var id       = Text(project, "Id");
            var name     = Text(project, "Name");
            var rootPath = Text(project, "RootPath");

            // A workspace without somewhere to point is not recoverable, and carrying it forward
            // would only surface as a broken entry in the dashboard.
            if (id is null || name is null || rootPath is null) continue;

            workspaces.Add(new JsonObject
            {
                ["Id"]          = id,
                ["Name"]        = name,
                ["RootPath"]    = rootPath,
                ["CreatedAt"]   = Text(project, "CreatedAt") ?? DateTime.UtcNow.ToString("O"),
                ["SubProjects"] = new JsonArray(),
            });
        }

        WriteValue(kv, WorkspacesKey, workspaces.ToJsonString());

        context.Logger?.LogInformation(
            "Reshaped {Count} project(s) into workspaces", workspaces.Count);
        return workspaces.Count;
    }

    private static string? ReadValue(ILiteCollection<BsonDocument> kv, string key) =>
        kv.FindById(key) is { } document && document.TryGetValue("Value", out var value) && value.IsString
            ? value.AsString
            : null;

    private static void WriteValue(ILiteCollection<BsonDocument> kv, string key, string value) =>
        kv.Upsert(key, new BsonDocument
        {
            ["_id"]       = key,
            ["Value"]     = value,
            ["UpdatedAt"] = DateTime.UtcNow,
        });

    private static string? Text(JsonObject source, string property) =>
        source.TryGetPropertyValue(property, out var node) ? node?.GetValue<string>() : null;
}
