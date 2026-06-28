using AgenticMemory.CodeIndex;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Search-quality contracts for <see cref="ICodeIndexRepository.SearchSymbolsAsync"/> — the exact
/// query path the MCP <c>get_symbol_context</c> tool drives. An AI caller supplies a known symbol
/// name and must get THAT symbol back, never a more-popular near-name. These tests pin the ranking
/// invariants (exact &gt; prefix &gt; substring, fan-in as a tie-breaker), case-insensitivity,
/// kind/visibility filters and sub-project scoping, so a regression in relevance fails loudly.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class SymbolSearchQualityTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    private Task<IReadOnlyList<SymbolReferenceRecord>> SearchAsync(
        string query, string? subProjectId = null,
        bool publicOnly = false, string[]? kinds = null, int minFanIn = 0) =>
        Fixture.Repository.SearchSymbolsAsync(
            query, Fixture.WorkspaceId, subProjectId, publicOnly, kinds, minFanIn);

    private static int TierOf(string name, string query)
    {
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return 2;
    }

    // ── Exactness wins ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exact_name_match_ranks_first()
    {
        // "Order" is an exact class name; "OrderService", "GetOrder", "GetOrderHandler", the "Orders"
        // property all merely contain it. The exact type must come back first.
        var results = await SearchAsync("Order", Fixture.BackendSubProjectId);
        Assert.NotEmpty(results);
        Assert.Equal("Order", results[0].SymbolName);
    }

    [Fact]
    public async Task Exact_match_outranks_a_more_referenced_substring_match()
    {
        RequireTypeScript();
        // The adversarial case: in web/, `makeUser` is referenced cross-file (consumer.ts) while the
        // `User` interface is only referenced same-file — so a pure popularity sort would float
        // makeUser to the top. Exactness must still put `User` first.
        var results = await SearchAsync("User", Fixture.WebSubProjectId);
        Assert.NotEmpty(results);
        Assert.Equal("User", results[0].SymbolName);

        // Sanity: the substring rivals really are in the candidate set (so the test is meaningful).
        Assert.Contains(results, r => r.SymbolName == "makeUser");
        Assert.Contains(results, r => r.SymbolName == "loadUser");
    }

    [Fact]
    public async Task Prefix_matches_precede_pure_substring_matches()
    {
        // "Order": prefix "OrderService" must come before substring "GetOrder".
        var results = (await SearchAsync("Order", Fixture.BackendSubProjectId))
            .Select(r => r.SymbolName).ToList();

        var orderService = results.IndexOf("OrderService");
        var getOrder     = results.IndexOf("GetOrder");
        Assert.True(orderService >= 0 && getOrder >= 0,
            $"Expected both OrderService and GetOrder in results: {string.Join(", ", results)}");
        Assert.True(orderService < getOrder,
            $"Prefix 'OrderService' (#{orderService}) should outrank substring 'GetOrder' (#{getOrder}).");
    }

    [Theory]
    [InlineData("Order")]
    [InlineData("Calc")]
    [InlineData("Service")]
    public async Task Match_tiers_are_monotonic_non_decreasing(string query)
    {
        // Across the whole result list the match tier must never improve as you go down — i.e. every
        // exact precedes every prefix precedes every substring. This is the core ranking guarantee.
        var results = await SearchAsync(query, Fixture.BackendSubProjectId);
        var tiers = results.Select(r => TierOf(r.SymbolName, query)).ToList();
        for (var i = 1; i < tiers.Count; i++)
            Assert.True(tiers[i] >= tiers[i - 1],
                $"Tier regressed at #{i} for query '{query}': {string.Join(",", tiers)}");
    }

    [Fact]
    public async Task Within_a_tier_higher_fan_in_ranks_first()
    {
        // With an empty query every symbol is tier 0, so ordering is pure fan-in — the most
        // depended-on symbol of the whole backend must lead.
        var results = await SearchAsync("", Fixture.BackendSubProjectId);
        Assert.NotEmpty(results);
        var maxFanIn = results.Max(r => r.UsedBy.Count);
        Assert.Equal(maxFanIn, results[0].UsedBy.Count);
    }

    // ── Case-insensitivity ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("calculator")]
    [InlineData("CALCULATOR")]
    [InlineData("Calculator")]
    public async Task Search_is_case_insensitive_and_returns_the_exact_symbol_first(string query)
    {
        var results = await SearchAsync(query, Fixture.BackendSubProjectId);
        Assert.NotEmpty(results);
        Assert.Equal("Calculator", results[0].SymbolName);
    }

    // ── Filters ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Kind_filter_restricts_to_the_requested_kinds()
    {
        var results = await SearchAsync("", Fixture.BackendSubProjectId, kinds: ["class"]);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("class", r.SymbolKind));
        Assert.Contains(results, r => r.SymbolName == "Calculator");
    }

    [Fact]
    public async Task Public_only_filter_excludes_non_public_symbols()
    {
        var results = await SearchAsync("", Fixture.BackendSubProjectId, publicOnly: true);
        Assert.NotEmpty(results);
        Assert.All(results, r =>
            Assert.True(r.Accessibility is "public" or "exported" or "internal",
                $"{r.SymbolName} has accessibility '{r.Accessibility}'"));
    }

    [Fact]
    public async Task Min_fan_in_filter_drops_low_reference_symbols()
    {
        var results = await SearchAsync("", Fixture.BackendSubProjectId, minFanIn: 1);
        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.UsedBy.Count >= 1));
    }

    // ── Scoping ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sub_project_scoping_isolates_results_to_one_sub_project()
    {
        // Calculator lives in the C# backend; it must not surface when scoped to the web sub-project.
        var backend = await SearchAsync("Calculator", Fixture.BackendSubProjectId);
        Assert.Contains(backend, r => r.SymbolName == "Calculator");

        var web = await SearchAsync("Calculator", Fixture.WebSubProjectId);
        Assert.DoesNotContain(web, r => r.SymbolName == "Calculator");
    }

    [Fact]
    public async Task Workspace_wide_search_spans_both_sub_projects()
    {
        RequireTypeScript();
        // No sub-project scope → both the C# Calculator and the TS User must be reachable.
        var calc = await SearchAsync("Calculator");
        Assert.Contains(calc, r => r.SymbolName == "Calculator");

        var user = await SearchAsync("User");
        Assert.Contains(user, r => r.SymbolName == "User");
    }

    [Fact]
    public async Task Unknown_symbol_returns_no_results()
    {
        var results = await SearchAsync("ThisSymbolDoesNotExistAnywhere", Fixture.BackendSubProjectId);
        Assert.Empty(results);
    }
}
