using System.Text.Json;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.CodeIndex;
using AgenticMemory.Tools;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// End-to-end validation of the MCP tool surface (<see cref="CodeIndexTools"/>) against the seeded
/// workspace — the exact contract an AI agent consumes over MCP. Each tool is driven through its real
/// resolution path (workspace → sub-project → file/symbol) and its JSON output is parsed and asserted,
/// so a regression that empties or corrupts a tool's result fails loudly. This is the "validate the
/// MCP endpoints return correct, non-empty results" gate for Release 1.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class McpCodeIndexToolsTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    // ── Harness ──────────────────────────────────────────────────────────────────

    private sealed class InMemoryKv : IKeyValueStore
    {
        private readonly Dictionary<string, string> _d = new();
        public string? Get(string key) => _d.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) => _d[key] = value;
        public void Delete(string key) => _d.Remove(key);
    }

    private CodeIndexTools BuildTools()
    {
        var kv = new InMemoryKv();
        var ws = new WorkspaceRecord(
            Fixture.WorkspaceId, "TestWorkspace", Fixture.WorkspaceRoot, "now",
            Fixture.SubProjects.Select(s => s with { WorkspaceId = Fixture.WorkspaceId }).ToList());
        kv.Set("workspaces", JsonSerializer.Serialize(new List<WorkspaceRecord> { ws }));

        var active = new ActiveProjectService(kv);
        active.SetActive(Fixture.WorkspaceId);
        return new CodeIndexTools(kv, Fixture.Repository, NullEmbeddingService.Instance, active);
    }

    private string BackendName => Fixture.SubProjects.First(s => s.Type == SubProjectType.CSharpProject).Name;
    private string WebName     => Fixture.SubProjects.First(s => s.Type == SubProjectType.TypeScript).Name;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    /// <summary>So a hung tool call is cancelled with the run rather than blocking it.</summary>
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── get_subproject_context ─────────────────────────────────────────────────────

    [Fact]
    public async Task SubprojectContext_lists_subprojects_with_manifest_and_language()
    {
        var json = await BuildTools().GetSubprojectContext(cancellationToken: Ct);
        var root = Parse(json);
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        Assert.True(root.GetArrayLength() >= 1);

        var backend = root.EnumerateArray().First(e => e.GetProperty("name").GetString() == BackendName);
        Assert.Equal("csharp", backend.GetProperty("language").GetString());
        Assert.False(string.IsNullOrWhiteSpace(backend.GetProperty("description").GetString()));

        var manifest = backend.GetProperty("manifest");
        Assert.Contains("net", manifest.GetProperty("framework").GetString());
        Assert.Equal(JsonValueKind.Array, manifest.GetProperty("key_dependencies").ValueKind);
    }

    [Fact]
    public async Task SubprojectContext_resolves_an_entry_point_for_the_web_app()
    {
        RequireTypeScript();
        var root = Parse(await BuildTools().GetSubprojectContext(cancellationToken: Ct));
        var web = root.EnumerateArray().First(e => e.GetProperty("name").GetString() == WebName);
        // The TS seed has App.tsx (a convention entry point) — it must be surfaced.
        var entry = web.TryGetProperty("entry_point", out var ep) ? ep.GetString() : null;
        Assert.NotNull(entry);
        Assert.EndsWith("App.tsx", entry!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubprojectContext_errors_clearly_when_no_workspace_registered()
    {
        var kv = new InMemoryKv(); // no "workspaces" key
        var tools = new CodeIndexTools(kv, Fixture.Repository, NullEmbeddingService.Instance, new ActiveProjectService(kv));
        var result = await tools.GetSubprojectContext(cancellationToken: Ct);
        Assert.Contains("No workspace", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubprojectContext_errors_clearly_when_multiple_workspaces_and_none_active()
    {
        var kv = new InMemoryKv();
        var a = new WorkspaceRecord("a", "Alpha", Fixture.WorkspaceRoot, "now", []);
        var b = new WorkspaceRecord("b", "Beta",  Fixture.WorkspaceRoot, "now", []);
        kv.Set("workspaces", JsonSerializer.Serialize(new List<WorkspaceRecord> { a, b }));
        var tools = new CodeIndexTools(kv, Fixture.Repository, NullEmbeddingService.Instance, new ActiveProjectService(kv));
        var result = await tools.GetSubprojectContext(cancellationToken: Ct);
        Assert.Contains("Multiple workspaces", result, StringComparison.OrdinalIgnoreCase);
    }

    // ── get_file_context ───────────────────────────────────────────────────────────

    [Fact]
    public async Task FileContext_returns_symbols_exports_and_line_count()
    {
        var root = Parse(await BuildTools().GetFileContext("Domain.cs", BackendName, cancellationToken: Ct));

        Assert.EndsWith("Domain.cs", root.GetProperty("path").GetString()!.Replace('\\', '/'));
        Assert.Equal("csharp", root.GetProperty("language").GetString());
        Assert.True(root.GetProperty("line_count").GetInt32() > 0);

        var symbolNames = root.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("Calculator", symbolNames);

        var calc = root.GetProperty("symbols").EnumerateArray()
            .First(s => s.GetProperty("name").GetString() == "Calculator");
        Assert.Equal("class", calc.GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(calc.GetProperty("signature").GetString()));

        var exports = root.GetProperty("exports").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Calculator", exports);
    }

    [Fact]
    public async Task FileContext_resolves_by_bare_filename_without_a_subproject()
    {
        var root = Parse(await BuildTools().GetFileContext("Domain.cs", cancellationToken: Ct));
        Assert.EndsWith("Domain.cs", root.GetProperty("path").GetString()!.Replace('\\', '/'));
    }

    [Fact]
    public async Task FileContext_reports_a_missing_file_clearly()
    {
        var result = await BuildTools().GetFileContext("NoSuchFile.cs", BackendName, cancellationToken: Ct);
        Assert.Contains("No indexed file", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FileContext_surfaces_typescript_class_and_interface_members()
    {
        RequireTypeScript();
        var root = Parse(await BuildTools().GetFileContext("models.ts", WebName, cancellationToken: Ct));
        var names = root.GetProperty("symbols").EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("User", names);
        Assert.Contains("Repository", names);
    }

    // ── get_symbol_context ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SymbolContext_returns_definition_callers_and_reference_count()
    {
        var root = Parse(await BuildTools().GetSymbolContext("Calculator", BackendName, cancellationToken: Ct));

        Assert.Equal("Calculator", root.GetProperty("symbol").GetString());
        Assert.Equal("class", root.GetProperty("kind").GetString());
        Assert.EndsWith("Domain.cs", root.GetProperty("definition").GetProperty("file").GetString()!.Replace('\\', '/'));
        Assert.True(root.GetProperty("definition").GetProperty("line").GetInt32() > 0);
        Assert.True(root.GetProperty("references_count").GetInt32() > 0);
        Assert.False(root.GetProperty("is_orphan").GetBoolean());
    }

    [Fact]
    public async Task SymbolContext_lists_callers_of_a_method()
    {
        var root = Parse(await BuildTools().GetSymbolContext("Add", BackendName, cancellationToken: Ct));
        var callers = root.GetProperty("callers").EnumerateArray()
            .Select(c => c.GetProperty("symbol").GetString()).ToList();
        // Consumer.Run calls calc.Add(...) — it must appear as a caller.
        Assert.Contains("Run", callers);
    }

    [Fact]
    public async Task SymbolContext_resolves_implementations_from_type_relations()
    {
        var root = Parse(await BuildTools().GetSymbolContext("Animal", BackendName, cancellationToken: Ct));
        var impls = root.GetProperty("implementations").EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()).ToList();
        Assert.Contains("Dog", impls); // class Dog : Animal
    }

    [Fact]
    public async Task SymbolContext_prefers_the_exact_name_over_near_names()
    {
        // "Order" must resolve to the Order class, never OrderService / GetOrder.
        var root = Parse(await BuildTools().GetSymbolContext("Order", BackendName, cancellationToken: Ct));
        Assert.Equal("Order", root.GetProperty("symbol").GetString());
    }

    [Fact]
    public async Task SymbolContext_reports_an_unknown_symbol_clearly()
    {
        var result = await BuildTools().GetSymbolContext("NotARealSymbol", BackendName, cancellationToken: Ct);
        Assert.Contains("No symbol named", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SymbolContext_resolves_a_typescript_interface()
    {
        RequireTypeScript();
        var root = Parse(await BuildTools().GetSymbolContext("User", WebName, cancellationToken: Ct));
        Assert.Equal("User", root.GetProperty("symbol").GetString());
        Assert.Equal("interface", root.GetProperty("kind").GetString());
    }

    // ── get_symbol_sourcecode ──────────────────────────────────────────────────────

    [Fact]
    public async Task SymbolSourcecode_returns_the_full_definition_span()
    {
        var root = Parse(await BuildTools().GetSymbolSourcecode("Calculator", BackendName, cancellationToken: Ct));

        Assert.EndsWith("Domain.cs", root.GetProperty("file").GetString()!.Replace('\\', '/'));
        var start = root.GetProperty("line_start").GetInt32();
        var end   = root.GetProperty("line_end").GetInt32();
        Assert.True(start > 0 && end >= start);

        var source = root.GetProperty("source").GetString()!;
        Assert.Contains("class Calculator", source);
        Assert.Contains("Add", source); // body captured, not just the signature line
    }

    [Fact]
    public async Task SymbolSourcecode_reports_an_unknown_symbol_clearly()
    {
        var result = await BuildTools().GetSymbolSourcecode("NotARealSymbol", BackendName, cancellationToken: Ct);
        Assert.Contains("No symbol named", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SymbolSourcecode_returns_a_typescript_function_body()
    {
        RequireTypeScript();
        var root = Parse(await BuildTools().GetSymbolSourcecode("makeUser", WebName, cancellationToken: Ct));
        var source = root.GetProperty("source").GetString()!;
        Assert.Contains("makeUser", source);
    }

    // ── search_code ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchCode_finds_a_file_by_keyword()
    {
        // Embeddings are disabled in the fixture → exercises the lexical lane.
        var root = Parse(await BuildTools().SearchCode("Calculator", BackendName, cancellationToken: Ct));
        Assert.Equal(JsonValueKind.Array, root.ValueKind);
        var paths = root.EnumerateArray()
            .Select(h => h.GetProperty("path").GetString()!.Replace('\\', '/')).ToList();
        Assert.Contains(paths, p => p.EndsWith("Domain.cs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCode_rejects_an_unknown_subproject()
    {
        var result = await BuildTools().SearchCode("anything", "no-such-subproject", cancellationToken: Ct);
        Assert.Contains("No sub-project", result, StringComparison.OrdinalIgnoreCase);
    }
}
