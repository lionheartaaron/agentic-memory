namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// The promoted domain-fact layer (routes / DI / EF / MediatR / type relations / config / sinks),
/// persisted per file and queried by project. Covers the minimal-API endpoint extraction added recently.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class CSharpDomainFactTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Minimal_api_endpoints_are_extracted_with_verb_and_route()
    {
        var endpoints = await fixture.BackendFactsAsync("http-endpoint");
        Assert.True(endpoints.Count >= 3, $"expected >=3 endpoints, got {endpoints.Count}");
        Assert.Contains(endpoints, e => e.Method == "GET"    && e.Route == "/api/orders");
        Assert.Contains(endpoints, e => e.Method == "POST"   && e.Route == "/api/orders");
        Assert.Contains(endpoints, e => e.Method == "DELETE" && e.Route == "/api/orders/{id}");
    }

    [Fact]
    public async Task Constructor_injection_edges_are_captured()
    {
        var di = await fixture.BackendFactsAsync("di-injection");
        Assert.Contains(di, f => f.OwnerType == "OrderService" && f.TypeRef == "IClock");
    }

    [Fact]
    public async Task Ef_dbset_entities_are_captured()
    {
        var ef = await fixture.BackendFactsAsync("ef-entity");
        Assert.Contains(ef, f => f.Name == "Order" && f.OwnerType == "AppDbContext");
    }

    [Fact]
    public async Task Mediatr_messages_and_handlers_are_paired()
    {
        var messages = await fixture.BackendFactsAsync("mediatr-message");
        Assert.Contains(messages, f => f.Name == "GetOrder");

        var handlers = await fixture.BackendFactsAsync("mediatr-handler");
        Assert.Contains(handlers, f => f.Name == "GetOrderHandler");
    }

    [Fact]
    public async Task Type_relations_capture_extends_and_implements()
    {
        var rel = await fixture.BackendFactsAsync("type-relation");
        Assert.Contains(rel, f => f.OwnerType == "Dog" && f.Method == "extends"    && f.Name == "Animal");
        Assert.Contains(rel, f => f.OwnerType == "Dog" && f.Method == "implements" && f.Name == "IBark");
    }

    [Fact]
    public async Task Config_keys_are_captured()
    {
        var config = await fixture.BackendFactsAsync("config-key");
        Assert.Contains(config, f => f.Name == "My:Setting");
    }

    [Fact]
    public async Task Security_sinks_are_captured_by_symbol_identity()
    {
        var sinks = await fixture.BackendFactsAsync("security-sink");
        Assert.Contains(sinks, f => f.Name == "process");
    }

    [Fact]
    public async Task Domain_facts_are_scoped_to_their_sub_project()
    {
        var all = await fixture.BackendFactsAsync();
        Assert.NotEmpty(all);
        Assert.All(all, f => Assert.Equal(fixture.BackendSubProjectId, f.SubProjectId));
    }
}
