using System.Buffers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// High-performance HTTP response writer with minimal allocations.
/// Uses pre-computed headers and pooled buffers.
/// </summary>
public sealed class OptimizedResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly byte[] _serverHeader;

    public OptimizedResponseWriter(string serverName = "AgenticMemory/1.0")
    {
        _serverHeader = Encoding.ASCII.GetBytes($"Server: {serverName}\r\n");
    }

    /// <summary>
    /// Write response with minimal allocations using pre-computed headers
    /// </summary>
    public async ValueTask WriteAsync(NetworkStream stream, Response response, CancellationToken cancellationToken)
    {
        var buffer = MemoryStreamPool.Rent();
        try
        {
            // Serialize body first (if present) to know Content-Length
            byte[]? bodyBytes = null;
            if (response.Body is not null)
            {
                bodyBytes = SerializeBody(response.Body, response.ContentType);
            }

            // Write status line (pre-computed for common codes)
            buffer.Write(CommonHeaders.GetStatusLine(response.StatusCode));

            // Write server header
            buffer.Write(_serverHeader);

            // Write cached date header
            buffer.Write(DateHeaderCache.DateHeaderBytes);

            // Write content-type
            WriteContentType(buffer, response.ContentType);

            // Write content-length (pre-computed for small values)
            int contentLength = bodyBytes?.Length ?? 0;
            buffer.Write(CommonHeaders.GetContentLengthHeader(contentLength));

            // Write custom headers
            foreach (var header in response.Headers)
            {
                WriteHeader(buffer, header.Key, header.Value);
            }

            // Write header/body separator
            buffer.Write(CommonHeaders.CRLF);

            // Write body
            if (bodyBytes is not null)
            {
                buffer.Write(bodyBytes);
            }

            // Send entire response in one write
            await stream.WriteAsync(buffer.GetMemory(), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        finally
        {
            MemoryStreamPool.Return(buffer);
        }
    }

    /// <summary>
    /// Write a simple response with pre-computed status and JSON body
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WriteJsonAsync<T>(NetworkStream stream, int statusCode, T body, CancellationToken cancellationToken)
    {
        var response = new Response
        {
            StatusCode = statusCode,
            ReasonPhrase = Response.GetReasonPhrase(statusCode),
            Body = body,
            ContentType = "application/json"
        };
        return WriteAsync(stream, response, cancellationToken);
    }

    /// <summary>
    /// Write a 200 OK response with no body (fastest path)
    /// </summary>
    public async ValueTask WriteOkAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Pre-computed minimal OK response
        ReadOnlyMemory<byte> response = BuildMinimalResponse(200, null);
        await stream.WriteAsync(response, cancellationToken);
    }

    /// <summary>
    /// Write a 404 Not Found response (pre-computed)
    /// </summary>
    public async ValueTask WriteNotFoundAsync(NetworkStream stream, string message, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { error = message }, JsonOptions);
        var response = BuildResponse(404, "application/json", body);
        await stream.WriteAsync(response, cancellationToken);
    }

    private static void WriteContentType(RecyclableMemoryStream buffer, string contentType)
    {
        // Match against common content types (with or without charset)
        if (contentType.StartsWith("application/json"))
        {
            buffer.Write(CommonHeaders.ContentTypeJson);
        }
        else if (contentType.StartsWith("text/html"))
        {
            buffer.Write(CommonHeaders.ContentTypeHtml);
        }
        else if (contentType.StartsWith("text/css"))
        {
            buffer.Write(CommonHeaders.ContentTypeCss);
        }
        else if (contentType.StartsWith("text/plain"))
        {
            buffer.Write(CommonHeaders.ContentTypePlain);
        }
        else
        {
            // For other content types, write as-is (ensure UTF-8 if text)
            WriteHeader(buffer, "Content-Type", contentType);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHeader(RecyclableMemoryStream buffer, string name, string value)
    {
        // Header names are ASCII per HTTP spec, but values may contain UTF-8
        buffer.Write(Encoding.ASCII.GetBytes(name));
        buffer.Write(CommonHeaders.HeaderSeparator);
        buffer.Write(Encoding.UTF8.GetBytes(value));
        buffer.Write(CommonHeaders.CRLF);
    }

    private static byte[] SerializeBody(object data, string contentType)
    {
        if (data is string str)
        {
            return Encoding.UTF8.GetBytes(str);
        }

        if (data is byte[] bytes)
        {
            return bytes;
        }

        // JSON serialization
        return JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
    }

    private ReadOnlyMemory<byte> BuildMinimalResponse(int statusCode, byte[]? body)
    {
        using var ms = new MemoryStream(256);

        ms.Write(CommonHeaders.GetStatusLine(statusCode));
        ms.Write(_serverHeader);
        ms.Write(DateHeaderCache.DateHeaderBytes);
        ms.Write(CommonHeaders.ContentTypeJson);
        ms.Write(CommonHeaders.GetContentLengthHeader(body?.Length ?? 0));
        ms.Write(CommonHeaders.CRLF);

        if (body is not null)
            ms.Write(body);

        return ms.ToArray();
    }

    private ReadOnlyMemory<byte> BuildResponse(int statusCode, string contentType, byte[]? body)
    {
        using var ms = new MemoryStream(256 + (body?.Length ?? 0));

        ms.Write(CommonHeaders.GetStatusLine(statusCode));
        ms.Write(_serverHeader);
        ms.Write(DateHeaderCache.DateHeaderBytes);

        if (contentType == "application/json")
            ms.Write(CommonHeaders.ContentTypeJson);
        else
            ms.Write(Encoding.ASCII.GetBytes($"Content-Type: {contentType}\r\n"));

        ms.Write(CommonHeaders.GetContentLengthHeader(body?.Length ?? 0));
        ms.Write(CommonHeaders.CRLF);

        if (body is not null)
            ms.Write(body);

        return ms.ToArray();
    }
}
