using System.Buffers;
using System.Net.Sockets;
using System.Text;
using AgenticMemory.Http.Models;

namespace AgenticMemory.Http;

/// <summary>
/// Parse HTTP-like requests from raw TCP stream
/// </summary>
public class RequestParser
{
    private readonly ArrayPool<byte> _bufferPool = ArrayPool<byte>.Shared;
    private readonly int _maxHeaderSize;
    private readonly int _maxBodySize;

    public RequestParser(int maxHeaderSize = 8192, int maxBodySize = 10485760)
    {
        _maxHeaderSize = maxHeaderSize;
        _maxBodySize = maxBodySize;
    }

    /// <summary>
    /// Parse complete HTTP request from stream
    /// </summary>
    public async Task<Request?> ParseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = _bufferPool.Rent(_maxHeaderSize);
        try
        {
            // Read until we find the end of headers (double CRLF)
            int totalRead = 0;
            int headerEnd = -1;

            while (totalRead < _maxHeaderSize)
            {
                if (!stream.DataAvailable && totalRead == 0)
                {
                    // Wait for data with timeout
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    try
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(totalRead, _maxHeaderSize - totalRead), timeoutCts.Token);
                        if (read == 0)
                            return null; // Connection closed

                        totalRead += read;
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                }
                else if (stream.DataAvailable)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(totalRead, _maxHeaderSize - totalRead), cancellationToken);
                    if (read == 0)
                        return null;

                    totalRead += read;
                }

                // Check for end of headers
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, totalRead));
                if (headerEnd >= 0)
                    break;

                if (!stream.DataAvailable && totalRead > 0)
                    break; // No more data and no header end found yet - might be incomplete
            }

            if (headerEnd < 0)
                headerEnd = totalRead; // Treat entire buffer as headers if no body

            // Parse the request line and headers
            var headerSpan = buffer.AsSpan(0, headerEnd);
            var (method, path, queryString) = ParseRequestLine(headerSpan);
            var headers = ParseHeaders(headerSpan);

            // Read body if Content-Length is present
            byte[]? body = null;
            if (headers.TryGetValue("Content-Length", out var contentLengthStr) &&
                int.TryParse(contentLengthStr, out var contentLength) &&
                contentLength > 0)
            {
                if (contentLength > _maxBodySize)
                    throw new InvalidOperationException($"Body too large: {contentLength} bytes");

                body = await ReadBodyAsync(stream, buffer, totalRead, headerEnd + 4, contentLength, cancellationToken);
            }

            return new Request
            {
                Method = Request.ParseMethod(method),
                Path = path,
                QueryString = queryString,
                Headers = headers,
                Body = body
            };
        }
        finally
        {
            _bufferPool.Return(buffer);
        }
    }

    private static int FindHeaderEnd(ReadOnlySpan<byte> buffer)
    {
        // Look for \r\n\r\n
        for (int i = 0; i < buffer.Length - 3; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }
        return -1;
    }

    private static (string method, string path, Dictionary<string, string> queryString) ParseRequestLine(ReadOnlySpan<byte> buffer)
    {
        // Find first line
        int lineEnd = buffer.IndexOf((byte)'\r');
        if (lineEnd < 0)
            lineEnd = buffer.Length;

        var line = Encoding.UTF8.GetString(buffer[..lineEnd]);
        var parts = line.Split(' ', 3);

        if (parts.Length < 2)
            throw new InvalidOperationException($"Invalid request line: {line}");

        var method = parts[0];
        var fullPath = parts[1];

        // Parse path and query string
        var queryIndex = fullPath.IndexOf('?');
        string path;
        var queryString = new Dictionary<string, string>();

        if (queryIndex >= 0)
        {
            path = fullPath[..queryIndex];
            var queryPart = fullPath[(queryIndex + 1)..];
            foreach (var pair in queryPart.Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2)
                {
                    queryString[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
                }
                else if (kv.Length == 1 && !string.IsNullOrEmpty(kv[0]))
                {
                    queryString[Uri.UnescapeDataString(kv[0])] = string.Empty;
                }
            }
        }
        else
        {
            path = fullPath;
        }

        return (method, path, queryString);
    }

    private static Dictionary<string, string> ParseHeaders(ReadOnlySpan<byte> buffer)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = Encoding.UTF8.GetString(buffer);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Skip first line (request line)
        for (int i = 1; i < lines.Length; i++)
        {
            var colonIndex = lines[i].IndexOf(':');
            if (colonIndex > 0)
            {
                var name = lines[i][..colonIndex].Trim();
                var value = lines[i][(colonIndex + 1)..].Trim();
                headers[name] = value;
            }
        }

        return headers;
    }

    private async Task<byte[]> ReadBodyAsync(NetworkStream stream, byte[] headerBuffer, int totalHeaderRead, int bodyStart, int contentLength, CancellationToken cancellationToken)
    {
        var body = new byte[contentLength];
        int bodyRead = 0;

        // Copy any body data already in header buffer
        int alreadyInBuffer = totalHeaderRead - bodyStart;
        if (alreadyInBuffer > 0)
        {
            int toCopy = Math.Min(alreadyInBuffer, contentLength);
            Array.Copy(headerBuffer, bodyStart, body, 0, toCopy);
            bodyRead = toCopy;
        }

        // Read remaining body
        while (bodyRead < contentLength)
        {
            int read = await stream.ReadAsync(body.AsMemory(bodyRead, contentLength - bodyRead), cancellationToken);
            if (read == 0)
                throw new InvalidOperationException("Connection closed while reading body");

            bodyRead += read;
        }

        return body;
    }
}
