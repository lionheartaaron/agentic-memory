using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http;

/// <summary>
/// Serialize and write HTTP-like responses to TCP stream
/// </summary>
public class ResponseWriter
{
    private static readonly byte[] Crlf = "\r\n"u8.ToArray();
    private static readonly byte[] HeaderSeparator = ": "u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _serverName;

    public ResponseWriter(string serverName = "AgenticMemory/1.0")
    {
        _serverName = serverName;
    }

    /// <summary>
    /// Write complete HTTP response to stream
    /// </summary>
    public async Task WriteAsync(NetworkStream stream, Response response, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();

        // Serialize body first to know Content-Length
        byte[]? bodyBytes = null;
        if (response.Body is not null)
        {
            bodyBytes = SerializeBody(response.Body, response.ContentType);
        }

        // Write status line
        WriteStatusLine(ms, response.StatusCode, response.ReasonPhrase);

        // Write standard headers
        WriteHeader(ms, "Server", _serverName);
        WriteHeader(ms, "Date", DateTime.UtcNow.ToString("R"));
        WriteHeader(ms, "Content-Type", response.ContentType);

        if (bodyBytes is not null)
        {
            WriteHeader(ms, "Content-Length", bodyBytes.Length.ToString());
        }
        else
        {
            WriteHeader(ms, "Content-Length", "0");
        }

        // Write custom headers
        foreach (var header in response.Headers)
        {
            WriteHeader(ms, header.Key, header.Value);
        }

        // Write header/body separator
        ms.Write(Crlf);

        // Write body
        if (bodyBytes is not null)
        {
            ms.Write(bodyBytes);
        }

        // Send entire response
        ms.Position = 0;
        await ms.CopyToAsync(stream, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void WriteStatusLine(MemoryStream stream, int statusCode, string reasonPhrase)
    {
        var line = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        stream.Write(bytes);
    }

    private static void WriteHeader(MemoryStream stream, string name, string value)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);

        stream.Write(nameBytes);
        stream.Write(HeaderSeparator);
        stream.Write(valueBytes);
        stream.Write(Crlf);
    }

    private static byte[] SerializeBody(object data, string contentType)
    {
        // Handle text-based content types (HTML, CSS, plain text, etc.)
        if (contentType.StartsWith("text/"))
        {
            return Encoding.UTF8.GetBytes(data.ToString() ?? string.Empty);
        }

        // Default to JSON
        return JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
    }
}
