using System.Text.Json;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Models;
using AgenticMemoryTests.Shared;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemoryTests.Frontend;

/// <summary>
/// Base class for Frontend HTTP handler tests. Provides common infrastructure for
/// testing HTTP endpoints including search, memory CRUD operations, and response handling.
/// </summary>
public abstract class FrontendTestBase : IAsyncLifetime
{
    protected TestFixture Fixture { get; private set; } = null!;
    protected SearchHandler SearchHandler { get; private set; } = null!;
    protected IMemoryRepository Repository => Fixture.Repository;

    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public virtual async ValueTask InitializeAsync()
    {
        Fixture = new TestFixture();
        await Fixture.InitializeAsync();
        SearchHandler = new SearchHandler(Fixture.SearchService);
    }

    public async ValueTask DisposeAsync()
    {
        await Fixture.DisposeAsync();
    }

    #region Helper Methods

    protected async Task<Guid> StoreMemoryAsync(string title, string summary, string content, List<string> tags)
    {
        var memory = new MemoryNodeEntity
        {
            Title = title,
            Summary = summary,
            Content = content,
            Tags = tags
        };

        await Fixture.ConflictStorage.StoreAsync(memory, TestContext.Current.CancellationToken);
        return memory.Id;
    }

    protected static Request CreateGetSearchRequest(string query, int topN, bool acceptHtml = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (acceptHtml)
        {
            headers["Accept"] = "text/html";
        }

        return new Request
        {
            Method = HttpMethod.GET,
            Path = $"/search?q={Uri.EscapeDataString(query)}&top_n={topN}",
            QueryString = new Dictionary<string, string>
            {
                ["q"] = query,
                ["top_n"] = topN.ToString()
            },
            Headers = headers
        };
    }

    protected static Request CreatePostSearchRequest(SearchRequest searchRequest)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(searchRequest, JsonOptions);

        return new Request
        {
            Method = HttpMethod.POST,
            Path = "/search",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json"
            },
            Body = body
        };
    }

    protected static T? DeserializeResponse<T>(object? body)
    {
        if (body is null) return default;
        var json = JsonSerializer.Serialize(body, JsonOptions);
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    #endregion
}
