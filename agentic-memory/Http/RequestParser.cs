using System.Buffers;
using System.Net;
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
                // Use ReadAsync with cancellation token - it will block until data arrives
                // or cancellation is requested. Don't rely on DataAvailable which can be
                // false even when more data is coming (network latency between packets).
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
                    if (cancellationToken.IsCancellationRequested)
                        return null; // External cancellation
                    // Timeout - if we have data, try to parse it; otherwise return null
                    if (totalRead == 0)
                        return null;
                    break;
                }

                // Check for end of headers
                headerEnd = FindHeaderEnd(buffer.AsSpan(0, totalRead));
                if (headerEnd >= 0)
                    break;
            }

            if (headerEnd < 0)
                headerEnd = totalRead; // Treat entire buffer as headers if no body

            // Parse the request line and headers
            var headerSpan = buffer.AsSpan(0, headerEnd);
            var (method, path, queryString) = ParseRequestLine(headerSpan);
            var headers = ParseHeaders(headerSpan);

            // Read body based on Content-Length or Transfer-Encoding
            byte[]? body = null;
            
            if (headers.TryGetValue("Content-Length", out var contentLengthStr) &&
                int.TryParse(contentLengthStr, out var contentLength) &&
                contentLength > 0)
            {
                if (contentLength > _maxBodySize)
                    throw new InvalidOperationException($"Body too large: {contentLength} bytes");

                body = await ReadBodyAsync(stream, buffer, totalRead, headerEnd + 4, contentLength, cancellationToken);
            }
            else if (headers.TryGetValue("Transfer-Encoding", out var transferEncoding) &&
                     transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                // Handle chunked transfer encoding
                body = await ReadChunkedBodyAsync(stream, buffer, totalRead, headerEnd + 4, cancellationToken);
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
                    // Use WebUtility.UrlDecode to properly handle both + and %20 as space
                    queryString[WebUtility.UrlDecode(kv[0])] = WebUtility.UrlDecode(kv[1]);
                }
                else if (kv.Length == 1 && !string.IsNullOrEmpty(kv[0]))
                {
                    queryString[WebUtility.UrlDecode(kv[0])] = string.Empty;
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

    /// <summary>
    /// Read chunked transfer-encoded body per HTTP/1.1 spec (RFC 7230)
    /// Format: {chunk-size in hex}\r\n{chunk-data}\r\n ... 0\r\n\r\n
    /// </summary>
    private async Task<byte[]> ReadChunkedBodyAsync(NetworkStream stream, byte[] headerBuffer, int totalHeaderRead, int bodyStart, CancellationToken cancellationToken)
    {
        using var bodyStream = new MemoryStream();
        var readBuffer = new byte[8192];
        
        // First, handle any data already in the header buffer after the headers
        int leftoverStart = bodyStart;
        int leftoverLength = totalHeaderRead - bodyStart;
        byte[] leftoverData = leftoverLength > 0 
            ? headerBuffer[leftoverStart..totalHeaderRead] 
            : [];
        
        var chunkBuffer = new List<byte>(leftoverData);
        
        while (true)
        {
            // Ensure we have enough data to read chunk size line
            while (!ContainsCrlf(chunkBuffer))
            {
                int read = await stream.ReadAsync(readBuffer, cancellationToken);
                if (read == 0)
                    throw new InvalidOperationException("Connection closed while reading chunked body");
                chunkBuffer.AddRange(readBuffer.Take(read));
            }
            
            // Extract chunk size line
            int crlfIndex = FindCrlfIndex(chunkBuffer);
            var sizeLine = Encoding.ASCII.GetString(chunkBuffer.Take(crlfIndex).ToArray()).Trim();
            chunkBuffer.RemoveRange(0, crlfIndex + 2); // Remove size line + CRLF
            
            // Parse chunk size (hex, may have extensions after semicolon)
            var sizeStr = sizeLine.Split(';')[0].Trim();
            if (!int.TryParse(sizeStr, System.Globalization.NumberStyles.HexNumber, null, out var chunkSize))
                throw new InvalidOperationException($"Invalid chunk size: {sizeLine}");
            
            // Size 0 means end of chunks
            if (chunkSize == 0)
                break;
            
            if (bodyStream.Length + chunkSize > _maxBodySize)
                throw new InvalidOperationException($"Chunked body too large: exceeds {_maxBodySize} bytes");
            
            // Read chunk data
            while (chunkBuffer.Count < chunkSize + 2) // +2 for trailing CRLF
            {
                int read = await stream.ReadAsync(readBuffer, cancellationToken);
                if (read == 0)
                    throw new InvalidOperationException("Connection closed while reading chunk data");
                chunkBuffer.AddRange(readBuffer.Take(read));
            }
            
            // Write chunk data to body stream (excluding trailing CRLF)
            bodyStream.Write(chunkBuffer.Take(chunkSize).ToArray());
            chunkBuffer.RemoveRange(0, chunkSize + 2); // Remove chunk data + CRLF
        }
        
        // Skip trailing headers (if any) - read until final CRLF
        while (!ContainsCrlf(chunkBuffer))
        {
            if (!stream.DataAvailable)
                break; // No trailing headers
            int read = await stream.ReadAsync(readBuffer, cancellationToken);
            if (read == 0)
                break;
            chunkBuffer.AddRange(readBuffer.Take(read));
        }
        
        return bodyStream.ToArray();
    }
    
    private static bool ContainsCrlf(List<byte> buffer)
    {
        for (int i = 0; i < buffer.Count - 1; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                return true;
        }
        return false;
    }
    
    private static int FindCrlfIndex(List<byte> buffer)
    {
        for (int i = 0; i < buffer.Count - 1; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n')
                return i;
        }
        return -1;
    }
}
