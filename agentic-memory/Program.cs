using System.Text;
using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using AgenticMemory.Http;
using AgenticMemory.Http.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory;

internal class Program
{
    static async Task Main(string[] args)
    {
        // Enable UTF-8 output for console
        Console.OutputEncoding = Encoding.UTF8;

        // Load configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        var settings = new AppSettings();
        configuration.Bind(settings);

        // Setup logging from configuration
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConfiguration(configuration.GetSection("Logging"))
                .AddConsole();
        });

        var logger = loggerFactory.CreateLogger<Program>();

        // Parse command line arguments (override config)
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--port" || args[i] == "-p") && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var p))
                    settings.Server.Port = p;
            }
            else if ((args[i] == "--bind" || args[i] == "-b") && i + 1 < args.Length)
            {
                settings.Server.BindAddress = args[i + 1];
            }
        }


        // Configure server
        var serverOptions = new ServerOptions
        {
            Port = settings.Server.Port,
            BindAddress = settings.Server.BindAddress,
            MaxConcurrentConnections = settings.Server.MaxConcurrentConnections,
            ConnectionTimeout = TimeSpan.FromSeconds(settings.Server.ConnectionTimeoutSeconds),
            RequestTimeout = TimeSpan.FromSeconds(settings.Server.RequestTimeoutSeconds),
            ShutdownTimeout = TimeSpan.FromSeconds(settings.Server.ShutdownTimeoutSeconds),
            EnableKeepAlive = settings.Server.EnableKeepAlive,
            ServerName = settings.Server.ServerName,
            MaxRequestSize = settings.Server.MaxRequestSizeBytes,
            MaxHeaderSize = settings.Server.MaxHeaderSizeBytes
        };

        // Create storage services
        logger.LogInformation("Initializing LiteDB storage at {Path}", settings.Storage.DatabasePath);
        
        // Ensure data directory exists
        var dataDir = Path.GetDirectoryName(settings.Storage.DatabasePath);
        if (!string.IsNullOrEmpty(dataDir) && !Directory.Exists(dataDir))
        {
            Directory.CreateDirectory(dataDir);
        }
        
        IMemoryRepository repository = new LiteDbMemoryRepository(settings.Storage.DatabasePath);

        // Create embedding service (Phase 2)
        IEmbeddingService? embeddingService = null;
        
        if (settings.Embeddings.Enabled)
        {
            // Auto-download models if configured and needed
            if (settings.Embeddings.AutoDownload)
            {
                var modelPath = settings.Embeddings.GetModelPath();
                var vocabPath = settings.Embeddings.GetVocabPath();

                if (!File.Exists(modelPath) || !File.Exists(vocabPath))
                {
                    Console.WriteLine();
                    Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
                    Console.WriteLine("║  Downloading Embedding Models                                ║");
                    Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
                    Console.WriteLine();

                    var downloader = new ModelDownloader(settings.Embeddings, loggerFactory.CreateLogger<ModelDownloader>());
                    var downloadSuccess = await downloader.EnsureModelsAsync();

                    if (!downloadSuccess)
                    {
                        logger.LogWarning("Model download failed or was cancelled. Continuing without semantic search.");
                    }

                    Console.WriteLine();
                }
            }

            // Initialize embedding service
            logger.LogInformation("Initializing embedding service from {Path}", settings.Embeddings.ModelsPath);
            try
            {
                var localEmbeddingService = new LocalEmbeddingService(
                    settings.Embeddings,
                    loggerFactory.CreateLogger<LocalEmbeddingService>());
                    
                if (localEmbeddingService.IsAvailable)
                {
                    embeddingService = localEmbeddingService;
                    logger.LogInformation("Embedding service initialized with {Dimensions}-dimensional vectors", embeddingService.Dimensions);
                }
                else
                {
                    logger.LogWarning("Embedding service not available. Set Embeddings.AutoDownload to true in appsettings.json to enable automatic download.");
                    localEmbeddingService.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialize embedding service. Continuing without semantic search.");
            }
        }
        else
        {
            logger.LogInformation("Embedding service disabled in configuration.");
        }

        // Create search service with optional embedding support
        ISearchService searchService = new MemorySearchEngine(
            repository, 
            embeddingService, 
            loggerFactory.CreateLogger<MemorySearchEngine>());
        var searchEngine = searchService as MemorySearchEngine;

        // Create maintenance service (Phase 3)
        IMaintenanceService maintenanceService = new MaintenanceService(
            repository,
            embeddingService,
            loggerFactory.CreateLogger<MaintenanceService>());

        // Create conflict-aware storage service
        IConflictAwareStorage conflictStorage = new ConflictAwareStorageService(
            repository,
            searchService,
            embeddingService,
            settings.Conflict,
            loggerFactory.CreateLogger<ConflictAwareStorageService>());

        // Create and optionally start background maintenance tasks
        MaintenanceBackgroundService? backgroundMaintenance = null;
        if (settings.Maintenance.Enabled)
        {
            backgroundMaintenance = new MaintenanceBackgroundService(
                maintenanceService,
                settings.Maintenance,
                loggerFactory.CreateLogger<MaintenanceBackgroundService>());
        }

        // Register cleanup on process exit (synchronous to ensure completion)
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            logger.LogInformation("Disposing services...");
            if (backgroundMaintenance != null)
            {
                backgroundMaintenance.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            embeddingService?.Dispose();
            repository.Dispose();
        };

        // Create router with default routes and services (including embedding service for memory creation)
        var router = TcpMemoryServer.CreateDefaultRouter(loggerFactory, repository, searchService, maintenanceService, embeddingService, conflictStorage, settings.Storage);

        // Create and start server
        await using var server = new TcpMemoryServer(serverOptions, router, loggerFactory.CreateLogger<TcpMemoryServer>());

        // Handle shutdown signals
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        try
        {
            await server.StartAsync(cts.Token);


            const int boxWidth = 72;
            string Line(string text) => $"║  {text.PadRight(boxWidth - 4)}║";
            string Header(string text) => $"║{text.PadLeft((boxWidth + text.Length) / 2).PadRight(boxWidth - 2)}║";
            string Status(bool enabled, bool active) => enabled ? (active ? "Enabled ✓" : "Enabled (inactive)") : "Disabled";
            string Endpoint(string method, string path, string desc) => $"  {method,-6} {path,-34} {desc}";

            var listeningOn = $"http://{settings.Server.BindAddress}:{settings.Server.Port}";
            var embeddingsActive = embeddingService != null && searchEngine?.SemanticSearchAvailable == true;

            Console.WriteLine();
            Console.WriteLine($"╔{new string('═', boxWidth - 2)}╗");
            Console.WriteLine(Header("Agentic Memory TCP Server"));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line($"Listening on: {listeningOn}"));
            Console.WriteLine(Line($"Semantic Search: {Status(settings.Embeddings.Enabled, embeddingsActive)}"));
            Console.WriteLine(Line($"Conflict Resolution: Enabled ✓"));
            Console.WriteLine(Line($"Background Maintenance: {Status(settings.Maintenance.Enabled, true)}"));
            Console.WriteLine(Line($"MCP Protocol: Enabled ✓"));
            Console.WriteLine(Line("Press Ctrl+C to stop"));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("UI & Search:"));
            Console.WriteLine(Line(Endpoint("GET",    "/",                       "Search interface")));
            Console.WriteLine(Line(Endpoint("GET",    "/search?q=query",         "Search memories")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory/search",      "Search (POST)")));
            Console.WriteLine(Line(Endpoint("GET",    "/memory/{id}.html",       "Memory detail page")));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("Memory CRUD:"));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory",             "Create memory")));
            Console.WriteLine(Line(Endpoint("GET",    "/api/memory/{id}",        "Get memory")));
            Console.WriteLine(Line(Endpoint("PUT",    "/api/memory/{id}",        "Update memory")));
            Console.WriteLine(Line(Endpoint("DELETE", "/api/memory/{id}",        "Delete memory")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory/{id}/reinforce", "Reinforce memory")));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("Batch Operations:"));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory/batch",       "Batch create")));
            Console.WriteLine(Line(Endpoint("PUT",    "/api/memory/batch",       "Batch update")));
            Console.WriteLine(Line(Endpoint("DELETE", "/api/memory/batch",       "Batch delete")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory/search/batch", "Batch search")));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("Graph & Links:"));
            Console.WriteLine(Line(Endpoint("GET",    "/api/memory/{id}/links",  "Get linked memories")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/memory/{id}/link/{targetId}", "Create link")));
            Console.WriteLine(Line(Endpoint("DELETE", "/api/memory/{id}/link/{targetId}", "Delete link")));
            Console.WriteLine(Line(Endpoint("GET",    "/api/memory/{id}/graph",  "Get memory graph")));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("Admin & Maintenance:"));
            Console.WriteLine(Line(Endpoint("GET",    "/api/admin/health",       "Health check")));
            Console.WriteLine(Line(Endpoint("GET",    "/api/admin/stats",        "Server statistics")));
            Console.WriteLine(Line(Endpoint("GET",    "/api/admin/maintenance/status", "Maintenance status")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/admin/consolidate",  "Run consolidation")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/admin/prune",        "Run pruning")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/admin/reindex",      "Reindex embeddings")));
            Console.WriteLine(Line(Endpoint("POST",   "/api/admin/compact",      "Compact database")));
            Console.WriteLine($"╠{new string('═', boxWidth - 2)}╣");
            Console.WriteLine(Line("MCP Protocol:"));
            Console.WriteLine(Line(Endpoint("POST",   "/mcp",                    "JSON-RPC messages")));
            Console.WriteLine(Line(Endpoint("GET",    "/mcp",                    "SSE stream")));
            Console.WriteLine(Line(Endpoint("DELETE", "/mcp",                    "Terminate session")));
            Console.WriteLine($"╚{new string('═', boxWidth - 2)}╝");
            Console.WriteLine();

            // Start background maintenance after server is running
            backgroundMaintenance?.Start();

            // Wait for cancellation
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        finally
        {
            logger.LogInformation("Shutting down...");
            if (backgroundMaintenance != null)
            {
                await backgroundMaintenance.StopAsync();
            }
            await server.StopAsync();
        }
    }
}
