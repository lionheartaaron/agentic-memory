namespace AgenticMemory.Http.Models;

/// <summary>
/// Represents an HTTP response to be written to TCP stream
/// </summary>
public class Response
{
    private static readonly Dictionary<int, string> ReasonPhrases = new()
    {
        [200] = "OK",
        [201] = "Created",
        [204] = "No Content",
        [400] = "Bad Request",
        [404] = "Not Found",
        [405] = "Method Not Allowed",
        [500] = "Internal Server Error"
    };

    // Content type constants with UTF-8 charset for Unicode support
    public const string ContentTypeJsonUtf8 = "application/json; charset=utf-8";
    public const string ContentTypeHtmlUtf8 = "text/html; charset=utf-8";
    public const string ContentTypePlainUtf8 = "text/plain; charset=utf-8";
    public const string ContentTypeCssUtf8 = "text/css; charset=utf-8";

    /// <summary>
    /// HTTP status code (200, 404, 500, etc.)
    /// </summary>
    public int StatusCode { get; init; }

    /// <summary>
    /// Status reason (OK, Not Found, etc.)
    /// </summary>
    public string ReasonPhrase { get; init; } = "OK";

    /// <summary>
    /// Response headers
    /// </summary>
    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>
    /// Response body (will be serialized)
    /// </summary>
    public object? Body { get; init; }

    /// <summary>
    /// Content-Type header (default: application/json; charset=utf-8)
    /// </summary>
    public string ContentType { get; init; } = ContentTypeJsonUtf8;

    /// <summary>
    /// Create a 200 OK response
    /// </summary>
    public static Response Ok(object? body = null, string contentType = ContentTypeJsonUtf8) => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        Body = body,
        ContentType = contentType
    };

    /// <summary>
    /// Create a 201 Created response with Location header
    /// </summary>
    public static Response Created(string location, object? body = null) => new()
    {
        StatusCode = 201,
        ReasonPhrase = "Created",
        Body = body,
        ContentType = ContentTypeJsonUtf8,
        Headers = { ["Location"] = location }
    };

    /// <summary>
    /// Create a 204 No Content response
    /// </summary>
    public static Response NoContent() => new()
    {
        StatusCode = 204,
        ReasonPhrase = "No Content"
    };

    /// <summary>
    /// Create a 400 Bad Request response
    /// </summary>
    public static Response BadRequest(string message) => new()
    {
        StatusCode = 400,
        ReasonPhrase = "Bad Request",
        Body = new { error = message },
        ContentType = ContentTypeJsonUtf8
    };

    /// <summary>
    /// Create a 404 Not Found response
    /// </summary>
    public static Response NotFound(string message = "Not Found") => new()
    {
        StatusCode = 404,
        ReasonPhrase = "Not Found",
        Body = new { error = message },
        ContentType = ContentTypeJsonUtf8
    };

    /// <summary>
    /// Create a 405 Method Not Allowed response
    /// </summary>
    public static Response MethodNotAllowed(string message = "Method Not Allowed") => new()
    {
        StatusCode = 405,
        ReasonPhrase = "Method Not Allowed",
        Body = new { error = message },
        ContentType = ContentTypeJsonUtf8
    };

    /// <summary>
    /// Create a 500 Internal Server Error response
    /// </summary>
    public static Response InternalServerError(string message) => new()
    {
        StatusCode = 500,
        ReasonPhrase = "Internal Server Error",
        Body = new { error = message },
        ContentType = ContentTypeJsonUtf8
    };

    /// <summary>
    /// Create an HTML response (UTF-8 encoded)
    /// </summary>
    public static Response Html(string html) => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        Body = html,
        ContentType = ContentTypeHtmlUtf8
    };

    /// <summary>
    /// Create a plain text response (UTF-8 encoded)
    /// </summary>
    public static Response Text(string text) => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        Body = text,
        ContentType = ContentTypePlainUtf8
    };

    /// <summary>
    /// Create a CSS stylesheet response (UTF-8 encoded, cacheable)
    /// </summary>
    public static Response Css(string css) => new()
    {
        StatusCode = 200,
        ReasonPhrase = "OK",
        Body = css,
        ContentType = ContentTypeCssUtf8
    };

    /// <summary>
    /// Get reason phrase for status code
    /// </summary>
    public static string GetReasonPhrase(int statusCode) =>
        ReasonPhrases.TryGetValue(statusCode, out var phrase) ? phrase : "Unknown";
}
