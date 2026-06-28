namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Repository query surface: sub-project scoping, symbol search, manifests, test-file detection,
/// and the aggregates the overview/hotspot/entrypoint endpoints are built on.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class RepositoryQueryTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Files_are_partitioned_by_sub_project()
    {
        var ct = TestContext.Current.CancellationToken;
        var backend = await fixture.Repository.GetBySubProjectAsync(fixture.BackendSubProjectId, ct);
        var web     = await fixture.Repository.GetBySubProjectAsync(fixture.WebSubProjectId, ct);

        Assert.NotEmpty(backend);
        Assert.All(backend, r => Assert.Equal("csharp", r.Language));
        Assert.All(backend, r => Assert.Equal(fixture.BackendSubProjectId, r.SubProjectId));

        Assert.NotEmpty(web);
        Assert.All(web, r => Assert.Equal(fixture.WebSubProjectId, r.SubProjectId));
    }

    [Fact]
    public async Task Symbol_search_can_be_scoped_to_a_sub_project()
    {
        var ct = TestContext.Current.CancellationToken;
        var hits = await fixture.Repository.SearchSymbolsAsync(
            "Calculator", fixture.WorkspaceId, fixture.BackendSubProjectId, ct: ct);
        Assert.Contains(hits, h => h.SymbolName == "Calculator");
        Assert.All(hits, h => Assert.Equal(fixture.BackendSubProjectId, h.SubProjectId));
    }

    [Fact]
    public async Task Public_only_symbol_search_excludes_private_members()
    {
        var ct = TestContext.Current.CancellationToken;
        var publicHits = await fixture.Repository.SearchSymbolsAsync(
            "", fixture.WorkspaceId, fixture.BackendSubProjectId, publicOnly: true, ct: ct);
        Assert.DoesNotContain(publicHits, h => h.SymbolName == "Secret");
        Assert.Contains(publicHits, h => h.SymbolName == "Calculator");
    }

    [Fact]
    public async Task Manifests_for_all_three_kinds_are_captured()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifests = await fixture.Repository.GetProjectManifestsAsync(fixture.WorkspaceId, ct);
        var kinds = manifests.Select(m => m.ManifestType).ToHashSet();
        Assert.Contains("csproj", kinds);
        Assert.Contains("package.json", kinds);
        Assert.Contains("tsconfig.json", kinds);

        var csproj = manifests.First(m => m.ManifestType == "csproj");
        Assert.Contains("net10.0", csproj.TargetFrameworks);

        var pkg = manifests.First(m => m.ManifestType == "package.json");
        Assert.Contains(pkg.Packages, p => p.Name == "react");
        Assert.Contains(pkg.Packages, p => p.Name == "typescript" && p.IsDev);
    }

    [Fact]
    public async Task Test_file_is_detected_with_framework()
    {
        var tests = await fixture.GetRecordAsync("CalculatorTests.cs");
        Assert.True(tests.IsTestFile);
        Assert.Equal("xunit", tests.TestFramework);
    }

    [Fact]
    public async Task Hot_symbols_surface_the_most_referenced_definitions()
    {
        var ct = TestContext.Current.CancellationToken;
        var hot = await fixture.Repository.GetHotSymbolsAsync(fixture.WorkspaceId, 20, ct);
        Assert.Contains(hot, h => h.SymbolName == "Calculator");
    }

    [Fact]
    public async Task Project_stats_reflect_indexed_files()
    {
        var ct = TestContext.Current.CancellationToken;
        var (indexed, _, errored) = await fixture.Repository.GetProjectStatsAsync(fixture.WorkspaceId, ct);
        Assert.True(indexed > 0);
        Assert.Equal(0, errored);
    }

    [Fact]
    public async Task Overview_style_counts_aggregate_correctly()
    {
        var ct = TestContext.Current.CancellationToken;
        var files     = await fixture.Repository.GetByProjectAsync(fixture.WorkspaceId, ct);
        var endpoints = await fixture.Repository.GetDomainFactsByProjectAsync(fixture.WorkspaceId, "http-endpoint", null, ct);

        Assert.True(files.Count >= 6, $"expected the backend + web files to be indexed, got {files.Count}");
        Assert.Equal(1, files.Count(f => f.IsTestFile));
        Assert.Equal(3, endpoints.Count);
    }
}
