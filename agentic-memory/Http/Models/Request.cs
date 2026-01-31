using System.Buffers;
using System.Text.Json;

namespace AgenticMemory.Http.Models;

/// <summary>
/// HTTP method enumeration
/// </summary>
public enum HttpMethod
{
    GET,
    POST,
    PUT,
    DELETE,
    OPTIONS,
    HEAD,
    PATCH
}

/// <summary>
/// Represents an HTTP request parsed from raw TCP stream
/// </summary>
public class Request
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// HTTP method (GET, POST, PUT, DELETE)
    /// </summary>
    public HttpMethod Method { get; init; }

    /// <summary>
    /// Request path (/api/memory/search)
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Parsed query parameters
    /// </summary>
    public Dictionary<string, string> QueryString { get; init; } = [];

    /// <summary>
    /// Request headers (case-insensitive)
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raw request body
    /// </summary>
    public byte[]? Body { get; init; }

    /// <summary>
    /// Path parameters extracted by router
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = [];

    /// <summary>
    /// Content-Type header value
    /// </summary>
    public string? ContentType => GetHeader("Content-Type");

    /// <summary>
    /// Accept header value
    /// </summary>
    public string? Accept => GetHeader("Accept");

    /// <summary>
    /// Deserialize body as type T based on Content-Type
    /// </summary>
    public T? GetBodyAs<T>()
    {
        if (Body is null || Body.Length == 0)
            return default;

        // For now, only JSON is supported
        return JsonSerializer.Deserialize<T>(Body, JsonOptions);
    }

    /// <summary>
    /// Get query parameter by key
    /// </summary>
    public string? GetQueryParameter(string key)
    {
        return QueryString.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Get header value (case-insensitive)
    /// </summary>
    public string? GetHeader(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Get path parameter by key
    /// </summary>
    public string? GetParameter(string key)
    {
        return Parameters.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// Parse HTTP method from string
    /// </summary>
    public static HttpMethod ParseMethod(string method) => method.ToUpperInvariant() switch
    {
        "GET" => HttpMethod.GET,
        "POST" => HttpMethod.POST,
        "PUT" => HttpMethod.PUT,
        "DELETE" => HttpMethod.DELETE,
        "OPTIONS" => HttpMethod.OPTIONS,
        "HEAD" => HttpMethod.HEAD,
        "PATCH" => HttpMethod.PATCH,
        _ => throw new ArgumentException($"Unknown HTTP method: {method}")
    };
}
