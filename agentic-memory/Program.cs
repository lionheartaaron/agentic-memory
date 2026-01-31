using System.Text;
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

        // Setup logging
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder
                .AddConsole()
                .SetMinimumLevel(LogLevel.Information);
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
            ServerName = settings.Server.ServerName
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
        ISearchService searchService = new MemorySearchEngine(repository, embeddingService);
        var searchEngine = searchService as MemorySearchEngine;

        // Create maintenance service (Phase 3)
        IMaintenanceService maintenanceService = new MaintenanceService(
            repository,
            embeddingService,
            loggerFactory.CreateLogger<MaintenanceService>());

        // Create and optionally start background maintenance tasks
        MaintenanceBackgroundService? backgroundMaintenance = null;
        if (settings.Maintenance.Enabled)
        {
            backgroundMaintenance = new MaintenanceBackgroundService(
                maintenanceService,
                settings.Maintenance,
                loggerFactory.CreateLogger<MaintenanceBackgroundService>());
        }

        // Register cleanup on process exit
        AppDomain.CurrentDomain.ProcessExit += async (_, _) =>
        {
            logger.LogInformation("Disposing services...");
            if (backgroundMaintenance != null)
            {
                await backgroundMaintenance.DisposeAsync();
            }
            embeddingService?.Dispose();
            repository.Dispose();
        };

        // Create router with default routes and services
        var router = TcpMemoryServer.CreateDefaultRouter(loggerFactory, repository, searchService, maintenanceService);

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

            // Start background maintenance after server is running
            backgroundMaintenance?.Start();

            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║            Agentic Memory TCP Server                         ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  Listening on: http://{settings.Server.BindAddress}:{settings.Server.Port,-28}║");
            Console.WriteLine($"║  Semantic Search: {(searchEngine?.SemanticSearchAvailable == true ? "Enabled ✓" : "Disabled"),-42}║");
            Console.WriteLine($"║  Background Maintenance: {(backgroundMaintenance?.IsRunning == true ? "Enabled ✓" : "Disabled"),-35}║");
            Console.WriteLine("║  Press Ctrl+C to stop                                        ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════════════╣");
            Console.WriteLine("║  Endpoints:                                                  ║");
            Console.WriteLine("║    GET  /                    - Search interface              ║");
            Console.WriteLine("║    GET  /css/*.css           - Stylesheets (cached)          ║");
            Console.WriteLine("║    GET  /search?q=query      - Search memories               ║");
            Console.WriteLine("║    POST /api/memory/search   - Search API                    ║");
            Console.WriteLine("║    GET  /api/memory/{id}     - Get memory node               ║");
            Console.WriteLine("║    POST /api/memory          - Create memory node            ║");
            Console.WriteLine("║    PUT  /api/memory/{id}     - Update memory node            ║");
            Console.WriteLine("║    DELETE /api/memory/{id}   - Delete memory node            ║");
            Console.WriteLine("║    GET  /memory/{id}.html    - Memory node HTML              ║");
            Console.WriteLine("║    GET  /api/admin/stats     - Server statistics             ║");
            Console.WriteLine("║    POST /api/admin/prune     - Manual pruning                ║");
            Console.WriteLine("║    POST /api/admin/consolidate - Consolidation trigger       ║");
            Console.WriteLine("║    POST /api/admin/reindex   - Reindex memories              ║");
            Console.WriteLine("║    POST /api/admin/compact   - Compact database              ║");
            Console.WriteLine("║    GET  /api/admin/maintenance/status - Maintenance status   ║");
            Console.WriteLine("║    GET  /api/admin/health    - Health check                  ║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
            Console.WriteLine();

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
