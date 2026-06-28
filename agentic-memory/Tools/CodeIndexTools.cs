using System.ComponentModel;
using System.Text;
using System.Text.Json;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.CodeIndex;
using ModelContextProtocol.Server;

namespace AgenticMemory.Tools;

[McpServerToolType]
public class CodeIndexTools
{
    private readonly IKeyValueStore _kv;
    private readonly ICodeIndexRepository _repo;
    private readonly IEmbeddingService _embedding;

    public CodeIndexTools(IKeyValueStore kv, ICodeIndexRepository repo, IEmbeddingService embedding)
    {
        _kv       = kv;
        _repo     = repo;
        _embedding = embedding;
    }

    [McpServerTool(Name = "get_project_info")]
    [Description(
        "Returns all registered workspaces and their auto-discovered sub-projects (language, type, " +
        "provider status, sub-project ID). Call this at the start of a session to orient yourself " +
        "and to get sub-project IDs for scoped code searches.")]
    public string GetProjectInfo()
    {
        var workspaces = LoadWorkspaces();
        if (workspaces.Count == 0)
            return "No workspaces registered. POST /api/workspaces to register one.";

        var sb = new StringBuilder();
        foreach (var ws in workspaces)
        {
            sb.AppendLine($"Workspace: {ws.Name}  (id: {ws.Id})");
            sb.AppendLine($"  Root: {ws.RootPath}");
            if (ws.SubProjects.Count == 0)
            {
                sb.AppendLine("  No sub-projects discovered yet. Activate workspace to trigger discovery.");
                continue;
            }
            foreach (var sp in ws.SubProjects)
                sb.AppendLine(
                    $"  [{sp.Language.ToUpperInvariant()}] {sp.Name}" +
                    $"  id:{sp.Id}  type:{sp.Type}  ns:{sp.Namespace}");
        }
        return sb.ToString();
    }

    [McpServerTool(Name = "search_code")]
    [Description(
        "Search the code index by semantic meaning and/or keyword. " +
        "Returns file names, relative paths, symbols, and LLM summaries ranked by relevance. " +
        "Use sub_project_id (from get_project_info) to scope to one language. " +
        "Omit it to search across the entire workspace.")]
    public async Task<string> SearchCode(
        [Description("What to search for — concepts, class names, route paths, domain terms")]
        string query,
        [Description("Workspace ID to search within (from get_project_info). Omit for all workspaces.")]
        string? project_id = null,
        [Description("Sub-project ID to scope results to one language. Omit to search everything.")]
        string? sub_project_id = null,
        [Description("Maximum number of results (1–50)")]
        int top_n = 10,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CodeIndexRecord> results;

        if (_embedding.IsAvailable)
        {
            var vector = await _embedding.GetEmbeddingAsync(query, cancellationToken);
            var scored = await _repo.SearchByEmbeddingAsync(
                vector, project_id, sub_project_id, top_n, cancellationToken);
            results = scored.Select(x => x.Record).ToList();
        }
        else
        {
            results = await _repo.SearchLexicalAsync(
                query, project_id, sub_project_id, cancellationToken);
            results = results.Take(top_n).ToList();
        }

        if (results.Count == 0)
            return "No matching files found.";

        return string.Join("\n\n", results.Select(r =>
            $"**{r.FileName}** ({r.Language})\n" +
            $"Path: {r.RelativePath}\n" +
            $"Symbols: {string.Join(", ", r.Symbols.Take(8).Select(s => s.Name))}\n" +
            (string.IsNullOrEmpty(r.LlmSummary) ? "" : $"Summary: {r.LlmSummary}")));
    }

    private List<WorkspaceRecord> LoadWorkspaces()
    {
        var json = _kv.Get("workspaces");
        return string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<WorkspaceRecord>>(json) ?? [];
    }
}
