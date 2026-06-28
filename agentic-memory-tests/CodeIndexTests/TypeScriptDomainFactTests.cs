namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// TypeScript domain facts promoted from the bridge's getFileInfo hints: HTTP fetch endpoints,
/// TanStack query/mutation cache graph, and navigation edges. Mirrors <see cref="CSharpDomainFactTests"/>.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypeScriptDomainFactTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    private Task<IReadOnlyList<AgenticMemory.CodeIndex.DomainFactRecord>> WebFactsAsync(string? kind = null) =>
        Fixture.Repository.GetDomainFactsByProjectAsync(
            Fixture.WorkspaceId, kind, Fixture.WebSubProjectId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Fetch_endpoints_capture_verb_and_literal_url()
    {
        RequireTypeScript();
        var fetch = await WebFactsAsync("fetch-endpoint");
        Assert.Contains(fetch, f => f.Method == "GET"  && f.Route == "/api/orders");
        Assert.Contains(fetch, f => f.Method == "POST" && f.Route == "/api/orders");
        Assert.Contains(fetch, f => f.Method == "GET"  && f.Route == "/api/users");
    }

    [Fact]
    public async Task Tanstack_query_is_promoted_with_its_key()
    {
        RequireTypeScript();
        var queries = await WebFactsAsync("tanstack-query");
        Assert.Contains(queries, f => (f.Name ?? "").Contains("orders"));
    }

    [Fact]
    public async Task Tanstack_mutation_captures_invalidations_and_navigation()
    {
        RequireTypeScript();
        var mutations = await WebFactsAsync("tanstack-mutation");
        Assert.Contains(mutations, f => f.Items.Contains("orders"));   // invalidateQueries(['orders'])
        Assert.Contains(mutations, f => f.Route == "/orders");          // navigate('/orders') on success
    }

    [Fact]
    public async Task Navigation_edges_are_extracted_from_navigate_and_link()
    {
        RequireTypeScript();
        var nav = await WebFactsAsync("navigation-edge");
        Assert.Contains(nav, f => f.Route == "/orders");      // navigate('/orders')
        Assert.Contains(nav, f => f.Route == "/orders/new");  // <Link to="/orders/new">
    }

    [Fact]
    public async Task Facts_are_scoped_to_the_web_sub_project()
    {
        RequireTypeScript();
        var all = await WebFactsAsync();
        Assert.NotEmpty(all);
        Assert.All(all, f => Assert.Equal(Fixture.WebSubProjectId, f.SubProjectId));
    }
}
