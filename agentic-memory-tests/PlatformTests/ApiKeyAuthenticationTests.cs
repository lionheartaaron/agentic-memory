using System.Text;
using AgenticMemory.Configuration;
using AgenticMemory.Middleware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AgenticMemoryTests.PlatformTests;

/// <summary>
/// The server binds a TCP port, so without a key anything that can reach that port can read and
/// write somebody's memories. These cover the guarantee in both directions: that a configured key
/// is actually required everywhere data lives, and that configuring one does not break the two
/// things that must keep working without it — the readiness probe a host process polls, and the
/// static dashboard a browser cannot attach a header to.
/// </summary>
public class ApiKeyAuthenticationTests
{
    private const string Key = "s3cret-key-value";

    /// <summary>Runs one request through a pipeline whose terminal handler returns 200.</summary>
    private static async Task<HttpContext> Send(
        ServerSettings settings,
        string path,
        (string Name, string Value)? header = null)
    {
        var builder = new ApplicationBuilder(new ServiceCollection().BuildServiceProvider());
        builder.UseApiKeyAuthentication(settings);
        builder.Run(context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        var pipeline = builder.Build();

        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        if (header is { } h) http.Request.Headers[h.Name] = h.Value;

        await pipeline(http);
        return http;
    }

    private static ServerSettings Protected() => new() { ApiKey = Key };
    private static ServerSettings Open()      => new();

    private static async Task<string> BodyOf(HttpContext context)
    {
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        return await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
    }

    // ── No key configured ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithNoKeyConfiguredEverythingIsReachable()
    {
        // The default. Requiring a key out of the box would mean a fresh checkout fails its first
        // request with a 401, so open has to be the default — loudly, which the startup banner does.
        var response = await Send(Open(), "/api/memory");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task AKeyThatIsOnlyWhitespaceCountsAsNoKey()
    {
        var response = await Send(new ServerSettings { ApiKey = "   " }, "/api/memory");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    // ── A key configured ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnApiRequestWithoutAKeyIsRejected()
    {
        var response = await Send(Protected(), "/api/memory");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
    }

    [Fact]
    public async Task TheKeyIsAcceptedInTheDedicatedHeader()
    {
        var response = await Send(Protected(), "/api/memory", ("X-API-Key", Key));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task TheKeyIsAlsoAcceptedAsABearerToken()
    {
        // Most HTTP and MCP clients can send Authorization without extra configuration; several
        // cannot send an arbitrary custom header at all.
        var response = await Send(Protected(), "/api/memory", ("Authorization", $"Bearer {Key}"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task TheBearerSchemeIsMatchedCaseInsensitively()
    {
        var response = await Send(Protected(), "/api/memory", ("Authorization", $"bearer {Key}"));

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("")]
    [InlineData("s3cret-key-valu")]   // one character short
    [InlineData("s3cret-key-values")] // one character long
    [InlineData("S3CRET-KEY-VALUE")]  // keys are case-sensitive
    public async Task AWrongKeyIsRejected(string candidate)
    {
        var response = await Send(Protected(), "/api/memory", ("X-API-Key", candidate));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
    }

    [Fact]
    public async Task ABearerHeaderWithoutTheSchemeIsNotEnough()
    {
        var response = await Send(Protected(), "/api/memory", ("Authorization", Key));

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
    }

    [Fact]
    public async Task TheHeaderNameIsConfigurable()
    {
        var settings = new ServerSettings { ApiKey = Key, ApiKeyHeader = "X-Companion-Token" };

        Assert.Equal(StatusCodes.Status200OK,
            (await Send(settings, "/api/memory", ("X-Companion-Token", Key))).Response.StatusCode);

        // The default name is not also accepted once it has been changed.
        Assert.Equal(StatusCodes.Status401Unauthorized,
            (await Send(settings, "/api/memory", ("X-API-Key", Key))).Response.StatusCode);
    }

    // ── What must stay reachable ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthStaysOpenSoAHostCanWaitForStartup()
    {
        // An Electron host polls this before it has been given anything. If it 401s, "not ready"
        // and "not authorised" become indistinguishable and the sidecar looks hung.
        var response = await Send(Protected(), "/api/admin/health");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    [Fact]
    public async Task AnEndpointThatMerelyStartsWithHealthIsStillProtected()
    {
        // Segment matching, not string prefix: /api/admin/healthy must not inherit the exemption.
        var response = await Send(Protected(), "/api/admin/healthy");

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/assets/index-abc123.js")]
    [InlineData("/favicon.ico")]
    public async Task StaticDashboardAssetsAreNotProtected(string path)
    {
        // A browser cannot attach a header to its own page load. The assets are identical for every
        // install; the data they go on to request is what carries the key.
        var response = await Send(Protected(), path);

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    // ── What must be protected ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/memory")]
    [InlineData("/api/memory/search")]
    [InlineData("/api/admin/stats")]
    [InlineData("/api/admin/database")]
    [InlineData("/api/admin/memories")]
    [InlineData("/api/kv/workspaces")]
    [InlineData("/api/generate")]
    [InlineData("/mcp")]
    [InlineData("/mcp/sse")]
    public async Task EveryDataPathIsProtected(string path)
    {
        // MCP especially: it is the surface an agent uses, and it can read and write everything.
        var response = await Send(Protected(), path);

        Assert.Equal(StatusCodes.Status401Unauthorized, response.Response.StatusCode);
    }

    [Fact]
    public async Task ThePathMatchRespectsSegmentBoundaries()
    {
        // "/apidocs" is not under "/api" and must not be swept up by a naive prefix test.
        var response = await Send(Protected(), "/apidocs");

        Assert.Equal(StatusCodes.Status200OK, response.Response.StatusCode);
    }

    // ── What the rejection says ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRejectionExplainsHowToAuthenticateWithoutLeakingTheKey()
    {
        var response = await Send(Protected(), "/api/memory");
        var body     = await BodyOf(response);

        Assert.Contains("X-API-Key", body);
        Assert.Contains("Bearer", body);

        // Never echo, confirm or hint at the secret itself.
        Assert.DoesNotContain(Key, body);
    }

    [Fact]
    public async Task TheRejectionNamesTheConfiguredHeaderNotTheDefault()
    {
        var settings = new ServerSettings { ApiKey = Key, ApiKeyHeader = "X-Companion-Token" };

        var body = await BodyOf(await Send(settings, "/api/memory"));

        Assert.Contains("X-Companion-Token", body);
    }

    [Fact]
    public async Task TheRejectionAdvertisesTheBearerScheme()
    {
        var response = await Send(Protected(), "/api/memory");

        Assert.Equal("Bearer", response.Response.Headers.WWWAuthenticate.ToString());
    }

    // ── The settings themselves ───────────────────────────────────────────────────────────────

    [Fact]
    public void AuthenticationIsOffUntilAKeyIsSet()
    {
        Assert.False(new ServerSettings().RequiresAuthentication);
        Assert.True(new ServerSettings { ApiKey = "x" }.RequiresAuthentication);
    }

    [Fact]
    public void TheShippedConfigurationDoesNotSetAKey()
    {
        // A key committed to the repository is a key everyone has. It must arrive from the host's
        // own configuration or from the environment variable, never from the bundled file.
        var settings = new ServerSettings();

        Assert.Equal("", settings.ApiKey);
        Assert.Equal("X-API-Key", settings.ApiKeyHeader);
    }
}
