using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Configuration;
using AgenticMemory.Http.Configuration;
using AgenticMemory.Http.Handlers;
using AgenticMemory.Http.Mcp;
using AgenticMemory.Http.Middleware;
using AgenticMemory.Http.Models;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Http;

/// <summary>
/// Ultra-fast, minimal TCP server for AI agent memory queries
/// </summary>
public class TcpMemoryServer : IAsyncDisposable
{
    private readonly ServerOptions _options;
    private readonly Router _router;
    private readonly RequestParser _requestParser;
    private readonly ResponseWriter _responseWriter;
    private readonly ILogger<TcpMemoryServer> _logger;
    private readonly ConcurrentDictionary<Guid, Task> _activeConnections = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoopTask;

    public TcpMemoryServer(
        ServerOptions options,
        Router router,
        ILogger<TcpMemoryServer> logger)
    {
        _options = options;
        _router = router;
        _logger = logger;
        _requestParser = new RequestParser(_options.MaxHeaderSize, _options.MaxRequestSize);
        _responseWriter = new ResponseWriter(_options.ServerName);
    }

    /// <summary>
    /// Start the TCP server and accept loop
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
            throw new InvalidOperationException("Server is already running");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var address = IPAddress.Parse(_options.BindAddress);
        _listener = new TcpListener(address, _options.Port);
        _listener.Start();

        _logger.LogInformation("Agentic Memory TCP Server started on {Address}:{Port}", _options.BindAddress, _options.Port);

        // Start accept loop
        _acceptLoopTask = AcceptLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gracefully shutdown server
    /// </summary>
    public async Task StopAsync()
    {
        if (_cts is null || _listener is null)
            return;

        _logger.LogInformation("Stopping Agentic Memory TCP Server...");

        // Signal cancellation
        await _cts.CancelAsync();

        // Stop accepting new connections
        _listener.Stop();

        // Wait for accept loop to complete
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
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Wait for active connections to complete
        if (_activeConnections.Count > 0)
        {
            _logger.LogInformation("Waiting for {Count} active connections to complete...", _activeConnections.Count);

            var connectionTasks = _activeConnections.Values.ToArray();
            try
            {
                await Task.WhenAll(connectionTasks).WaitAsync(_options.ShutdownTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("{Count} connections did not complete within shutdown timeout", _activeConnections.Count);
            }
            catch (Exception)
            {
                // Ignore exceptions during shutdown
            }
        }

        _logger.LogInformation("Agentic Memory TCP Server stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(cancellationToken);

                // Check connection limit
                if (_activeConnections.Count >= _options.MaxConcurrentConnections)
                {
                    _logger.LogWarning("Max connections reached ({Max}), rejecting connection", _options.MaxConcurrentConnections);
                    client.Dispose();
                    continue;
                }

                // Spawn worker task for connection
                var connectionId = Guid.NewGuid();
                var connectionTask = HandleConnectionAsync(client, connectionId, cancellationToken);
                _activeConnections.TryAdd(connectionId, connectionTask);

                // Clean up completed connection task
                _ = connectionTask.ContinueWith(_ => _activeConnections.TryRemove(connectionId, out Task? _), TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break; // Listener was stopped
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting connection");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, Guid connectionId, CancellationToken cancellationToken)
    {
        using var context = new ConnectionContext(client);

        try
        {
            _logger.LogDebug("Connection {ConnectionId} from {RemoteEndPoint}", connectionId, context.RemoteEndPoint);

            // Set timeouts
            client.ReceiveTimeout = (int)_options.ConnectionTimeout.TotalMilliseconds;
            client.SendTimeout = (int)_options.ConnectionTimeout.TotalMilliseconds;

            // Request/response loop (for keep-alive support)
            do
            {
                using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCts.CancelAfter(_options.RequestTimeout);

                // Parse request
                var request = await _requestParser.ParseAsync(context.Stream, requestCts.Token);

                if (request is null)
                {
                    // Connection closed or timeout
                    break;
                }

                // Check for keep-alive
                context.KeepAlive = _options.EnableKeepAlive &&
                    request.GetHeader("Connection")?.Equals("keep-alive", StringComparison.OrdinalIgnoreCase) == true;

                // Route and execute
                var response = await _router.RouteAsync(request, requestCts.Token);

                // Add keep-alive header if appropriate
                if (context.KeepAlive)
                {
                    response.Headers["Connection"] = "keep-alive";
                }

                // Write response
                await _responseWriter.WriteAsync(context.Stream, response, requestCts.Token);

            } while (context.KeepAlive && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Connection {ConnectionId} cancelled", connectionId);
        }
        catch (IOException ex)
        {
            _logger.LogDebug("Connection {ConnectionId} IO error: {Message}", connectionId, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            // Parse error - try to send 400 response
            _logger.LogWarning("Connection {ConnectionId} parse error: {Message}", connectionId, ex.Message);
            try
            {
                var errorResponse = Response.BadRequest(ex.Message);
                await _responseWriter.WriteAsync(context.Stream, errorResponse, cancellationToken);
            }
            catch
            {
                // Ignore errors sending error response
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection {ConnectionId} unexpected error", connectionId);
            try
            {
                var errorResponse = Response.InternalServerError("Internal server error");
                await _responseWriter.WriteAsync(context.Stream, errorResponse, cancellationToken);
            }
            catch
            {
                // Ignore errors sending error response
            }
        }
        finally
        {
            _logger.LogDebug("Connection {ConnectionId} closed", connectionId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Number of active connections
    /// </summary>
    public int ActiveConnections => _activeConnections.Count;

    /// <summary>
    /// Create a pre-configured router with default routes
    /// </summary>
    public static Router CreateDefaultRouter(
        ILoggerFactory loggerFactory,
        IMemoryRepository? repository = null,
        ISearchService? searchService = null,
        IMaintenanceService? maintenanceService = null,
        IEmbeddingService? embeddingService = null,
        IConflictAwareStorage? conflictStorage = null,
        StorageSettings? storageSettings = null)
    {
        var searchHandler = new SearchHandler(searchService);
        var memoryHandler = new MemoryHandler(repository, embeddingService);
        var staticFileHandler = new StaticFileHandler(repository);
        var adminHandler = new AdminHandler(repository, maintenanceService);
        var cssHandler = new CssHandler();
        var mcpHandler = new McpHandler(repository, searchService, conflictStorage, storageSettings, loggerFactory.CreateLogger<McpHandler>());
        var batchHandler = new BatchHandler(repository, searchService, embeddingService);
        var graphHandler = new GraphHandler(repository);

        var router = new Router()
            // Middleware
            .UseMiddleware(new ErrorHandlingMiddleware(loggerFactory.CreateLogger<ErrorHandlingMiddleware>()))
            .UseMiddleware(new LoggingMiddleware(loggerFactory.CreateLogger<LoggingMiddleware>()))
            .UseMiddleware(new TimingMiddleware())

            // Static routes
            .MapGet("/", staticFileHandler)
            .MapGet("/index.html", staticFileHandler)

            // CSS routes (with caching)
            .MapGet("/css/main.css", cssHandler)
            .MapGet("/css/memory.css", cssHandler)
            .MapGet("/css/search.css", cssHandler)

            // Search routes
            .MapGet("/search", searchHandler)
            .MapPost("/api/memory/search", searchHandler)

            // Batch operations
            .MapPost("/api/memory/batch", batchHandler)
            .MapPut("/api/memory/batch", batchHandler)
            .MapDelete("/api/memory/batch", batchHandler)
            .MapPost("/api/memory/search/batch", batchHandler)

            // Memory CRUD routes
            .MapGet("/api/memory/{id}", memoryHandler)
            .MapPost("/api/memory", memoryHandler)
            .MapPut("/api/memory/{id}", memoryHandler)
            .MapDelete("/api/memory/{id}", memoryHandler)

            // Memory graph routes
            .MapGet("/api/memory/{id}/links", graphHandler)
            .MapPost("/api/memory/{id}/link/{targetId}", graphHandler)
            .MapDelete("/api/memory/{id}/link/{targetId}", graphHandler)
            .MapGet("/api/memory/{id}/graph", graphHandler)

            // Memory HTML routes
            .MapGet("/memory/{id}.html", staticFileHandler)

            // Admin routes
            .MapGet("/api/admin/stats", adminHandler)
            .MapPost("/api/admin/consolidate", adminHandler)
            .MapPost("/api/admin/prune", adminHandler)
            .MapPost("/api/admin/reindex", adminHandler)
            .MapPost("/api/admin/compact", adminHandler)
            .MapGet("/api/admin/maintenance/status", adminHandler)
            .MapGet("/api/admin/health", adminHandler)

            // MCP (Model Context Protocol) endpoint
            .MapPost("/mcp", mcpHandler);

        return router;
    }
}
