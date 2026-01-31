using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AgenticMemory.Http.Configuration;
using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http.Optimizations;

/// <summary>
/// High-performance TCP server using optimized components.
/// Targets 50,000+ RPS with sub-2ms latency.
/// </summary>
public sealed class OptimizedTcpMemoryServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly OptimizedRouter _router;
    private readonly OptimizedRequestParser _requestParser;
    private readonly OptimizedResponseWriter _responseWriter;
    private readonly ILogger<OptimizedTcpMemoryServer> _logger;
    private readonly ConnectionMetrics _metrics = new();
    private readonly ConcurrentDictionary<long, Task> _activeConnections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;
    private long _connectionIdCounter;

    public OptimizedTcpMemoryServer(
        ServerOptions options,
        OptimizedRouter router,
        ILogger<OptimizedTcpMemoryServer> logger)
    {
        _options = options;
        _router = router;
        _logger = logger;
        _requestParser = new OptimizedRequestParser(_options.MaxHeaderSize, _options.MaxRequestSize);
        _responseWriter = new OptimizedResponseWriter(_options.ServerName);
    }

    public ConnectionMetrics Metrics => _metrics;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Server is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var address = IPAddress.Parse(_options.BindAddress);
        _listener = new TcpListener(address, _options.Port);

        // Apply socket optimizations
        SocketOptimizations.ConfigureListener(_listener);

        _listener.Start(_options.MaxConcurrentConnections);

        // Freeze router for optimal performance
        _router.Freeze();

        _logger.LogInformation("Optimized TCP Server started on {Address}:{Port}", _options.BindAddress, _options.Port);

        // Start accept loop
        _acceptLoopTask = AcceptLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is null || _listener is null)
            return;

        _logger.LogInformation("Stopping Optimized TCP Server...");

        await _cts.CancelAsync();
        _listener.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.WaitAsync(_options.ShutdownTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Accept loop did not complete within shutdown timeout");
            }
            catch (OperationCanceledException) { }
        }

        if (_activeConnections.Count > 0)
        {
            _logger.LogInformation("Waiting for {Count} active connections...", _activeConnections.Count);
            try
            {
                await Task.WhenAll(_activeConnections.Values).WaitAsync(_options.ShutdownTimeout);
            }
            catch { }
        }

        _logger.LogInformation("Optimized TCP Server stopped. Total requests: {Requests}", _metrics.TotalRequests);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);

                if (_activeConnections.Count >= _options.MaxConcurrentConnections)
                {
                    _logger.LogWarning("Max connections reached, rejecting");
                    client.Dispose();
                    continue;
                }

                // Configure client socket
                SocketOptimizations.ConfigureClient(client);

                // Use incrementing counter instead of Guid for connection ID
                var connectionId = Interlocked.Increment(ref _connectionIdCounter);
                var connectionTask = HandleConnectionAsync(client, connectionId, cancellationToken);
                _activeConnections.TryAdd(connectionId, connectionTask);

                _ = connectionTask.ContinueWith(_ => _activeConnections.TryRemove(connectionId, out _), TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
                _metrics.ErrorOccurred();
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, long connectionId, CancellationToken cancellationToken)
    {
        _metrics.ConnectionOpened();

        try
        {
            using (client)
            {
                var stream = client.GetStream();
                stream.ReadTimeout = (int)_options.ConnectionTimeout.TotalMilliseconds;
                stream.WriteTimeout = (int)_options.ConnectionTimeout.TotalMilliseconds;

                do
                {
                    using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCts.CancelAfter(_options.RequestTimeout);

                    // Parse request using optimized parser
                    using var request = await _requestParser.ParseAsync(stream, requestCts.Token);
                    if (request is null)
                        break;

                    // Route and execute
                    var response = await _router.RouteAsync(request, requestCts.Token);

                    // Check keep-alive
                    bool keepAlive = _options.EnableKeepAlive &&
                        request.GetHeader("Connection")?.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) == true;

                    if (keepAlive)
                    {
                        response.Headers["Connection"] = "keep-alive";
                    }

                    // Write response using optimized writer
                    await _responseWriter.WriteAsync(stream, response, requestCts.Token);

                    _metrics.RequestCompleted(0);

                    if (!keepAlive)
                        break;

                } while (!cancellationToken.IsCancellationRequested);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (IOException)
        {
            // Connection closed
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Connection {Id} error", connectionId);
            _metrics.ErrorOccurred();
        }
        finally
        {
            _metrics.ConnectionClosed();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
    }
}
