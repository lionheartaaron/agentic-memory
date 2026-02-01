using System.Text.Json;

namespace AgenticMemoryTests.McpTests;

/// <summary>
/// Tests for MCP resources functionality (resources/list, resources/read).
/// </summary>
public class ResourceTests : McpTestBase
{
    #region Resources/List Tests

    [Fact]
    public async Task ResourcesList_ReturnsAvailableResources()
    {
        var request = CreateMcpRequest("resources/list");
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var resources = doc.RootElement.GetProperty("resources");
        var uris = resources.EnumerateArray().Select(r => r.GetProperty("uri").GetString()).ToList();

        Assert.Contains("memory://recent", uris);
        Assert.Contains("memory://stats", uris);
    }

    #endregion

    #region Resources/Read Tests

    [Fact]
    public async Task ResourcesRead_Stats_ReturnsStatistics()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://stats"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        Assert.Single(contents.EnumerateArray());

        var content = contents.EnumerateArray().First();
        Assert.Equal("memory://stats", content.GetProperty("uri").GetString());
        Assert.Equal("application/json", content.GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task ResourcesRead_Recent_ReturnsRecentMemories()
    {
        await StoreTestMemoryAsync("Recent 1", "First recent memory");
        await StoreTestMemoryAsync("Recent 2", "Second recent memory");

        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://recent"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        var content = contents.EnumerateArray().First();
        var text = content.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains("Recent", text);
    }

    [Fact]
    public async Task ResourcesRead_MemoryById_ReturnsMemory()
    {
        var id = await StoreTestMemoryAsync("Resource Test", "Testing resource access", "Full content");

        var @params = new Dictionary<string, object?>
        {
            ["uri"] = $"memory://{id}"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.Null(response.Error);

        var resultJson = JsonSerializer.Serialize(response.Result, JsonOptions);
        using var doc = JsonDocument.Parse(resultJson);

        var contents = doc.RootElement.GetProperty("contents");
        var content = contents.EnumerateArray().First();
        var text = content.GetProperty("text").GetString();

        Assert.NotNull(text);
        Assert.Contains("Resource Test", text);
    }

    #endregion

    #region Resources/Read Error Tests

    [Fact]
    public async Task ResourcesRead_InvalidUri_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "http://invalid"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("Invalid URI scheme", response.Error.Message);
    }

    [Fact]
    public async Task ResourcesRead_InvalidPath_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = "memory://invalid-path"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("Invalid resource path", response.Error.Message);
    }

    [Fact]
    public async Task ResourcesRead_NonexistentMemory_ReturnsError()
    {
        var @params = new Dictionary<string, object?>
        {
            ["uri"] = $"memory://{Guid.NewGuid()}"
        };

        var request = CreateMcpRequest("resources/read", @params);
        var response = await SendMcpRequestAsync(request);

        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error.Message);
    }

    #endregion
}
