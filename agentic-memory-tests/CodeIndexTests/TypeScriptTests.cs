namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// TypeScript provider coverage (in-process TypeScript compiler via ClearScript/V8). Gated on the
/// provider actually initialising in the test host — if typescript.js / the V8 native bits aren't
/// available the whole class skips rather than failing.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypeScriptTests(CodeIndexFixture fixture)
{
    private void RequireTypeScript() =>
        Assert.SkipUnless(fixture.TypeScriptAvailable, "TypeScript/V8 provider not available in this environment.");

    [Fact]
    public async Task Exported_symbols_are_extracted()
    {
        RequireTypeScript();
        var utils = await fixture.GetRecordAsync("utils.ts");
        Assert.Equal("typescript", utils.Language);
        Assert.Contains(utils.Symbols, s => s.Name == "formatName");
    }

    [Fact]
    public async Task Component_file_is_indexed()
    {
        RequireTypeScript();
        var app = await fixture.GetRecordAsync("App.tsx");
        Assert.NotEmpty(app.Symbols);
        Assert.Contains(app.Symbols, s => s.Name == "App");
    }

    [Fact]
    public async Task Cross_file_references_resolve()
    {
        RequireTypeScript();
        // App.tsx imports formatName from utils.ts → utils.ts is depended on.
        var utils = await fixture.GetRecordAsync("utils.ts");
        Assert.True(utils.FanIn > 0, "App.tsx should depend on utils.ts");
    }

    [Fact]
    public async Task Web_sub_project_files_are_typescript()
    {
        RequireTypeScript();
        var ct = TestContext.Current.CancellationToken;
        var web = await fixture.Repository.GetBySubProjectAsync(fixture.WebSubProjectId, ct);
        Assert.NotEmpty(web);
        Assert.Contains(web, r => r.RelativePath.Replace('\\', '/').EndsWith("useOrders.ts"));
    }

    [Fact]
    public async Task Tanstack_query_domain_facts_are_promoted()
    {
        RequireTypeScript();
        var ct = TestContext.Current.CancellationToken;
        var facts = await fixture.Repository.GetDomainFactsByProjectAsync(
            fixture.WorkspaceId, null, fixture.WebSubProjectId, ct);
        // The bridge's getFileInfo promotes TanStack query/mutation, navigation and fetch hints.
        Assert.Contains(facts, f =>
            f.Kind is "tanstack-query" or "tanstack-mutation" or "fetch-endpoint" or "navigation-edge");
    }

    [Fact]
    public async Task Symbol_kinds_are_classified()
    {
        RequireTypeScript();
        var models = await fixture.GetRecordAsync("models.ts");
        Assert.Contains(models.Symbols, s => s.Name == "User" && s.Kind == "Interface");
        Assert.Contains(models.Symbols, s => s.Name == "Identifier" && s.Kind == "TypeAlias");
        Assert.Contains(models.Symbols, s => s.Name == "Repository" && s.Kind == "Class");
        Assert.Contains(models.Symbols, s => s.Name == "makeUser" && s.Kind == "Function");
    }

    [Fact]
    public async Task Generic_class_exposes_type_parameters()
    {
        RequireTypeScript();
        var repo = await fixture.GetSymbolAsync("models.ts", "Repository");
        Assert.Contains(repo.TypeParameters, t => t.Name == "T");
    }

    [Fact]
    public async Task Cross_file_reference_records_role_and_caller()
    {
        RequireTypeScript();
        var formatName = await fixture.GetSymbolRefAsync("utils.ts", "formatName");
        Assert.NotNull(formatName);
        Assert.False(formatName!.IsOrphan);
        Assert.Contains(formatName.UsedBy, u => u.Role == "call");
        Assert.Contains(formatName.UsedBy, u => u.EnclosingName == "App");
    }

    [Fact]
    public async Task Fetch_endpoints_are_extracted_from_literal_urls()
    {
        RequireTypeScript();
        var ct = TestContext.Current.CancellationToken;
        var fetch = await fixture.Repository.GetDomainFactsByProjectAsync(
            fixture.WorkspaceId, "fetch-endpoint", fixture.WebSubProjectId, ct);
        Assert.Contains(fetch, f => f.Route == "/api/orders");
    }
}
