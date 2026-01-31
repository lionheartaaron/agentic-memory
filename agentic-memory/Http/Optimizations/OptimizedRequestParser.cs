using System.Buffers;
using System.Buffers.Text;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using HttpMethod = AgenticMemory.Http.Models.HttpMethod;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// High-performance HTTP request parser with minimal allocations.
/// Uses Span-based parsing and object pooling.
/// </summary>
public sealed class OptimizedRequestParser
{
    private static readonly byte[] CrLfCrLf = "\r\n\r\n"u8.ToArray();
    private static readonly byte Cr = (byte)'\r';
    private static readonly byte Lf = (byte)'\n';
    private static readonly byte Colon = (byte)':';
    private static readonly byte Space = (byte)' ';
    private static readonly byte QuestionMark = (byte)'?';
    private static readonly byte Ampersand = (byte)'&';
    private static readonly byte Equals = (byte)'=';

    private readonly int _maxHeaderSize;
    private readonly int _maxBodySize;

    // Pre-allocated buffers for common methods
    private static readonly byte[] GetBytes = "GET"u8.ToArray();
    private static readonly byte[] PostBytes = "POST"u8.ToArray();
    private static readonly byte[] PutBytes = "PUT"u8.ToArray();
    private static readonly byte[] DeleteBytes = "DELETE"u8.ToArray();

    public OptimizedRequestParser(int maxHeaderSize = 8192, int maxBodySize = 10485760)
    {
        _maxHeaderSize = maxHeaderSize;
        _maxBodySize = maxBodySize;
    }

    /// <summary>
    /// Parse HTTP request from stream with minimal allocations
    /// </summary>
    public async ValueTask<OptimizedRequest?> ParseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Rent buffer from shared pool
        var buffer = ArrayPool<byte>.Shared.Rent(_maxHeaderSize);
        try
        {
            int totalRead = 0;
            int headerEnd = -1;

            // Read headers
            while (totalRead < _maxHeaderSize)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(totalRead, _maxHeaderSize - totalRead), cancellationToken);
                if (read == 0)
                    return null;

                totalRead += read;

                // Look for end of headers
                headerEnd = FindSequence(buffer.AsSpan(0, totalRead), CrLfCrLf);
                if (headerEnd >= 0)
                    break;
            }

            if (headerEnd < 0)
                return null;

            var headerSpan = buffer.AsSpan(0, headerEnd);

            // Parse request line
            int requestLineEnd = headerSpan.IndexOf(Lf);
            if (requestLineEnd < 0)
                return null;

            var requestLine = headerSpan[..(requestLineEnd - 1)]; // Exclude \r
            var (method, path, queryParams) = ParseRequestLine(requestLine);

            // Parse headers into pooled dictionary
            var headers = DictionaryPool.Rent();
            ParseHeaders(headerSpan[(requestLineEnd + 1)..], headers);

            // Read body if Content-Length present
            byte[]? body = null;
            if (headers.TryGetValue("Content-Length", out var contentLengthStr) &&
                int.TryParse(contentLengthStr, out int contentLength) &&
                contentLength > 0)
            {
                if (contentLength > _maxBodySize)
                {
                    DictionaryPool.Return(headers);
                    throw new InvalidOperationException($"Body too large: {contentLength}");
                }

                body = new byte[contentLength];
                int bodyStart = headerEnd + 4;
                int alreadyRead = totalRead - bodyStart;

                if (alreadyRead > 0)
                {
                    int toCopy = Math.Min(alreadyRead, contentLength);
                    buffer.AsSpan(bodyStart, toCopy).CopyTo(body);
                }

                int remaining = contentLength - alreadyRead;
                if (remaining > 0)
                {
                    int bodyRead = alreadyRead;
                    while (bodyRead < contentLength)
                    {
                        int read = await stream.ReadAsync(body.AsMemory(bodyRead), cancellationToken);
                        if (read == 0)
                            throw new InvalidOperationException("Connection closed while reading body");
                        bodyRead += read;
                    }
                }
            }

            return new OptimizedRequest
            {
                Method = method,
                Path = path,
                QueryString = queryParams,
                Headers = headers,
                Body = body
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindSequence(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        return haystack.IndexOf(needle);
    }

    private static (HttpMethod method, string path, Dictionary<string, string> query) ParseRequestLine(ReadOnlySpan<byte> line)
    {
        // Parse method
        int methodEnd = line.IndexOf(Space);
        if (methodEnd < 0)
            throw new InvalidOperationException("Invalid request line");

        var methodSpan = line[..methodEnd];
        var method = ParseMethod(methodSpan);

        // Parse path and query
        int pathStart = methodEnd + 1;
        int pathEnd = line[pathStart..].IndexOf(Space);
        if (pathEnd < 0)
            pathEnd = line.Length - pathStart;
        else
            pathEnd += pathStart;

        var fullPath = line[pathStart..pathEnd];

        // Split path and query string
        int queryIndex = fullPath.IndexOf(QuestionMark);
        string path;
        var queryParams = DictionaryPool.Rent();

        if (queryIndex >= 0)
        {
            path = Encoding.UTF8.GetString(fullPath[..queryIndex]);
            ParseQueryString(fullPath[(queryIndex + 1)..], queryParams);
        }
        else
        {
            path = Encoding.UTF8.GetString(fullPath);
        }

        return (method, path, queryParams);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static HttpMethod ParseMethod(ReadOnlySpan<byte> method)
    {
        if (method.SequenceEqual(GetBytes))
            return HttpMethod.GET;
        if (method.SequenceEqual(PostBytes))
            return HttpMethod.POST;
        if (method.SequenceEqual(PutBytes))
            return HttpMethod.PUT;
        if (method.SequenceEqual(DeleteBytes))
            return HttpMethod.DELETE;

        return Encoding.UTF8.GetString(method).ToUpperInvariant() switch
        {
            "OPTIONS" => HttpMethod.OPTIONS,
            "HEAD" => HttpMethod.HEAD,
            "PATCH" => HttpMethod.PATCH,
            _ => throw new InvalidOperationException($"Unknown method: {Encoding.UTF8.GetString(method)}")
        };
    }

    private static void ParseQueryString(ReadOnlySpan<byte> query, Dictionary<string, string> dict)
    {
        while (query.Length > 0)
        {
            int ampIndex = query.IndexOf(Ampersand);
            ReadOnlySpan<byte> pair;

            if (ampIndex >= 0)
            {
                pair = query[..ampIndex];
                query = query[(ampIndex + 1)..];
            }
            else
            {
                pair = query;
                query = ReadOnlySpan<byte>.Empty;
            }

            int equalsIndex = pair.IndexOf(Equals);
            if (equalsIndex >= 0)
            {
                var key = Uri.UnescapeDataString(Encoding.UTF8.GetString(pair[..equalsIndex]));
                var value = Uri.UnescapeDataString(Encoding.UTF8.GetString(pair[(equalsIndex + 1)..]));
                dict[key] = value;
            }
        }
    }

    private static void ParseHeaders(ReadOnlySpan<byte> headerSection, Dictionary<string, string> headers)
    {
        while (headerSection.Length > 0)
        {
            int lineEnd = headerSection.IndexOf(Lf);
            if (lineEnd < 0)
                break;

            var line = headerSection[..(lineEnd > 0 && headerSection[lineEnd - 1] == Cr ? lineEnd - 1 : lineEnd)];
            headerSection = headerSection[(lineEnd + 1)..];

            if (line.Length == 0)
                break;

            int colonIndex = line.IndexOf(Colon);
            if (colonIndex > 0)
            {
                var name = Encoding.UTF8.GetString(line[..colonIndex]).Trim();
                var value = Encoding.UTF8.GetString(line[(colonIndex + 1)..]).Trim();
                headers[name] = value;
            }
        }
    }
}

/// <summary>
/// Optimized request that uses pooled dictionaries
/// </summary>
public sealed class OptimizedRequest : IDisposable
{
    public HttpMethod Method { get; init; }
    public string Path { get; init; } = string.Empty;
    public Dictionary<string, string> QueryString { get; init; } = null!;
    public Dictionary<string, string> Headers { get; init; } = null!;
    public byte[]? Body { get; init; }
    public Dictionary<string, string> Parameters { get; set; } = null!;

    private bool _disposed;

    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var value) ? value : null;

    public string? GetQueryParameter(string name) =>
        QueryString.TryGetValue(name, out var value) ? value : null;

    public string? GetParameter(string name) =>
        Parameters?.TryGetValue(name, out var value) == true ? value : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (QueryString is not null)
            DictionaryPool.Return(QueryString);
        if (Headers is not null)
            DictionaryPool.Return(Headers);
        if (Parameters is not null)
            DictionaryPool.Return(Parameters);
    }
}
