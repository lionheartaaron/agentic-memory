using System.Threading;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// Caches the HTTP Date header value, updating it once per second.
/// Eliminates DateTime.UtcNow.ToString("R") allocation on every response.
/// </summary>
public static class DateHeaderCache
{
    private static string _cachedDate = DateTime.UtcNow.ToString("R");
    private static byte[] _cachedDateBytes = System.Text.Encoding.ASCII.GetBytes(_cachedDate);
    private static long _lastUpdate = Environment.TickCount64;
    private static readonly object _lock = new();

    /// <summary>
    /// Pre-computed header line: "Date: {RFC1123 date}\r\n"
    /// </summary>
    private static byte[] _cachedDateHeader = CreateDateHeader();

    /// <summary>
    /// Get the cached date string (RFC1123 format)
    /// </summary>
    public static string DateString
    {
        get
        {
            RefreshIfNeeded();
            return _cachedDate;
        }
    }

    /// <summary>
    /// Get the cached date as bytes (ASCII)
    /// </summary>
    public static ReadOnlySpan<byte> DateBytes
    {
        get
        {
            RefreshIfNeeded();
            return _cachedDateBytes;
        }
    }

    /// <summary>
    /// Get pre-formatted "Date: {date}\r\n" header bytes
    /// </summary>
    public static ReadOnlySpan<byte> DateHeaderBytes
    {
        get
        {
            RefreshIfNeeded();
            return _cachedDateHeader;
        }
    }

    private static void RefreshIfNeeded()
    {
        long now = Environment.TickCount64;
        if (now - Volatile.Read(ref _lastUpdate) >= 1000)
        {
            lock (_lock)
            {
                if (now - _lastUpdate >= 1000)
                {
                    _cachedDate = DateTime.UtcNow.ToString("R");
                    _cachedDateBytes = System.Text.Encoding.ASCII.GetBytes(_cachedDate);
                    _cachedDateHeader = CreateDateHeader();
                    Volatile.Write(ref _lastUpdate, now);
                }
            }
        }
    }

    private static byte[] CreateDateHeader()
    {
        return System.Text.Encoding.ASCII.GetBytes($"Date: {_cachedDate}\r\n");
    }
}

/// <summary>
/// Pre-computed common HTTP header bytes to avoid runtime allocations
/// </summary>
public static class CommonHeaders
{
    // Status lines
    public static readonly byte[] Http11_200_OK = "HTTP/1.1 200 OK\r\n"u8.ToArray();
    public static readonly byte[] Http11_201_Created = "HTTP/1.1 201 Created\r\n"u8.ToArray();
    public static readonly byte[] Http11_204_NoContent = "HTTP/1.1 204 No Content\r\n"u8.ToArray();
    public static readonly byte[] Http11_400_BadRequest = "HTTP/1.1 400 Bad Request\r\n"u8.ToArray();
    public static readonly byte[] Http11_404_NotFound = "HTTP/1.1 404 Not Found\r\n"u8.ToArray();
    public static readonly byte[] Http11_405_MethodNotAllowed = "HTTP/1.1 405 Method Not Allowed\r\n"u8.ToArray();
    public static readonly byte[] Http11_500_InternalServerError = "HTTP/1.1 500 Internal Server Error\r\n"u8.ToArray();
    public static readonly byte[] Http11_304_NotModified = "HTTP/1.1 304 Not Modified\r\n"u8.ToArray();

    // Common headers - all text types specify UTF-8 charset for Unicode support
    public static readonly byte[] ContentTypeJson = "Content-Type: application/json; charset=utf-8\r\n"u8.ToArray();
    public static readonly byte[] ContentTypeHtml = "Content-Type: text/html; charset=utf-8\r\n"u8.ToArray();
    public static readonly byte[] ContentTypePlain = "Content-Type: text/plain; charset=utf-8\r\n"u8.ToArray();
    public static readonly byte[] ContentTypeCss = "Content-Type: text/css; charset=utf-8\r\n"u8.ToArray();
    public static readonly byte[] ConnectionClose = "Connection: close\r\n"u8.ToArray();
    public static readonly byte[] ConnectionKeepAlive = "Connection: keep-alive\r\n"u8.ToArray();
    public static readonly byte[] CacheControlNoCache = "Cache-Control: no-cache\r\n"u8.ToArray();

    // Header parts
    public static readonly byte[] ContentLengthPrefix = "Content-Length: "u8.ToArray();
    public static readonly byte[] ServerPrefix = "Server: "u8.ToArray();
    public static readonly byte[] CRLF = "\r\n"u8.ToArray();
    public static readonly byte[] HeaderSeparator = ": "u8.ToArray();

    // Content-Length values (pre-computed for common small sizes)
    private static readonly byte[][] ContentLengthValues = CreateContentLengthValues();

    private static byte[][] CreateContentLengthValues()
    {
        // Pre-compute Content-Length headers for values 0-1023
        var values = new byte[1024][];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {i}\r\n");
        }
        return values;
    }

    /// <summary>
    /// Get pre-computed Content-Length header if available, otherwise compute it
    /// </summary>
    public static ReadOnlySpan<byte> GetContentLengthHeader(int length)
    {
        if (length >= 0 && length < ContentLengthValues.Length)
        {
            return ContentLengthValues[length];
        }

        // Fall back to dynamic generation for large values
        return System.Text.Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n");
    }

    /// <summary>
    /// Get status line bytes for common status codes
    /// </summary>
    public static ReadOnlySpan<byte> GetStatusLine(int statusCode) => statusCode switch
    {
        200 => Http11_200_OK,
        201 => Http11_201_Created,
        204 => Http11_204_NoContent,
        304 => Http11_304_NotModified,
        400 => Http11_400_BadRequest,
        404 => Http11_404_NotFound,
        405 => Http11_405_MethodNotAllowed,
        500 => Http11_500_InternalServerError,
        _ => System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {statusCode} {GetReasonPhrase(statusCode)}\r\n")
    };

    private static string GetReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        304 => "Not Modified",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        500 => "Internal Server Error",
        _ => "Unknown"
    };
}
