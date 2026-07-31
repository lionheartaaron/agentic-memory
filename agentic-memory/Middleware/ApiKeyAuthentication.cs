using System.Security.Cryptography;
using System.Text;
using AgenticMemory.Configuration;

namespace AgenticMemory.Middleware;

/// <summary>
/// Requires a shared secret on every API and MCP request, when one is configured.
///
/// This is a single shared key, not a user identity system, and that is the right shape for what it
/// protects: one local process, serving one person, over loopback. What it defends against is the
/// fact that the server binds a TCP port — so on the default <c>0.0.0.0</c> address, every machine
/// on the network can read and write somebody's memories with an unauthenticated GET. A key turns
/// that from "anyone" into "anyone the host application told".
///
/// It is off by default. Requiring a key on a fresh checkout would mean a developer's first run
/// fails with a 401, so the default has to be open — but see
/// <see cref="ServerSettings.ApiKey"/> for when that stops being acceptable.
/// </summary>
public static class ApiKeyAuthentication
{
    /// <summary>
    /// Readiness only. Exempt so that a supervising process — an Electron host waiting for its
    /// sidecar — can poll for startup without being given the key, and so that a failing health
    /// check means the server is actually unhealthy rather than that the caller is unauthenticated.
    /// It discloses nothing but the fact that the process is running.
    /// </summary>
    public const string HealthPath = "/api/admin/health";

    private const string AuthorizationHeader = "Authorization";
    private const string BearerPrefix        = "Bearer ";

    public static IApplicationBuilder UseApiKeyAuthentication(
        this IApplicationBuilder app, ServerSettings settings)
    {
        if (!settings.RequiresAuthentication) return app;

        // Compared as bytes, so the encoding is fixed here rather than at every request.
        var expected = Encoding.UTF8.GetBytes(settings.ApiKey.Trim());
        var header   = string.IsNullOrWhiteSpace(settings.ApiKeyHeader)
            ? "X-API-Key"
            : settings.ApiKeyHeader.Trim();

        return app.Use(async (context, next) =>
        {
            if (!IsProtected(context.Request.Path) || IsAuthorised(context.Request, header, expected))
            {
                await next();
                return;
            }

            // 401 with a hint at *which* header, because the header name is configurable and a
            // caller that guessed wrong otherwise has nothing to go on. The key itself is never
            // echoed, confirmed, or compared against in the response.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            await context.Response.WriteAsJsonAsync(new
            {
                error = $"This server requires an API key. Send it as '{header}: <key>' " +
                        "or 'Authorization: Bearer <key>'.",
            });
        });
    }

    /// <summary>
    /// Everything that reads or writes data. The dashboard's own static assets are not covered:
    /// a browser cannot attach a custom header to its initial page load, and the HTML and
    /// JavaScript are the same bytes for every install. The data those assets go on to request is
    /// what needs protecting, and that all arrives here as <c>/api/...</c>.
    /// </summary>
    private static bool IsProtected(PathString path)
    {
        if (path.StartsWithSegments(HealthPath, StringComparison.OrdinalIgnoreCase))
            return false;

        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAuthorised(HttpRequest request, string header, byte[] expected)
    {
        if (request.Headers.TryGetValue(header, out var direct) && Matches(direct, expected))
            return true;

        if (!request.Headers.TryGetValue(AuthorizationHeader, out var authorization))
            return false;

        foreach (var value in authorization)
        {
            if (value is null) continue;
            if (!value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Equal(value[BearerPrefix.Length..], expected)) return true;
        }

        return false;
    }

    private static bool Matches(IEnumerable<string?> values, byte[] expected)
    {
        foreach (var value in values)
            if (value is not null && Equal(value, expected)) return true;

        return false;
    }

    /// <summary>
    /// Fixed-time comparison. A naive string equality returns as soon as two bytes differ, which
    /// makes the time it takes a measure of how much of the key the caller got right — enough, over
    /// many requests, to recover it one byte at a time. Length is not hidden, and does not need to
    /// be: it is chosen by whoever set the key, not learned from it.
    /// </summary>
    private static bool Equal(string candidate, byte[] expected) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate.Trim()), expected);
}
