using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Generation;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.CodeIndex;
using AgenticMemory.CodeIndex.CSharp;
using AgenticMemory.CodeIndex.TypeScript;
using AgenticMemory.Configuration;
using AgenticMemory.Tools;
using Spectre.Console;

namespace AgenticMemory.Extensions;

/// <summary>
/// Extension methods for configuring application services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds all application services to the dependency injection container.
    /// </summary>
    public static IServiceCollection AddAgenticMemoryServices(this IServiceCollection services, AppSettings settings)
    {
        services.AddConfiguration(settings);
        services.AddMemoryRepository(settings);
        services.AddKeyValueStore(settings);
        services.AddEmbeddingService();
        services.AddGenerativeModelService();
        services.AddSearchService();
        services.AddMaintenanceServices(settings);
        services.AddConflictAwareStorage();
        services.AddMcpTools();
        services.AddCodeIndexServices(settings.CodeIndex);

        return services;
    }

    private static IServiceCollection AddConfiguration(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(settings.Storage);
        services.AddSingleton(settings.Conflict);
        services.AddSingleton(settings.Embeddings);
        services.AddSingleton(settings.Generation);
        services.AddSingleton(settings.Maintenance);

        return services;
    }

    private static IServiceCollection AddMemoryRepository(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IMemoryRepository>(sp =>
        {
            var storageSettings = sp.GetRequiredService<AppSettings>().Storage;
            return new LiteDbMemoryRepository(storageSettings.DatabasePath);
        });

        return services;
    }

    private static IServiceCollection AddKeyValueStore(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IKeyValueStore>(new LiteDbKeyValueStore(settings.Storage.DatabasePath));
        return services;
    }

    private static IServiceCollection AddEmbeddingService(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var embeddingsSettings = sp.GetRequiredService<EmbeddingsSettings>();
            var logger = sp.GetRequiredService<ILogger<LocalEmbeddingService>>();

            if (!embeddingsSettings.Enabled)
            {
                logger.LogInformation("Embedding service disabled in configuration.");
                return NullEmbeddingService.Instance;
            }

            if (embeddingsSettings.AutoDownload)
            {
                if (!TryDownloadModels(sp, embeddingsSettings, logger))
                {
                    return NullEmbeddingService.Instance;
                }
            }

            return TryCreateEmbeddingService(embeddingsSettings, logger) ?? NullEmbeddingService.Instance;
        });

        return services;
    }

    private static bool TryDownloadModels(IServiceProvider sp, EmbeddingsSettings settings, ILogger logger)
    {
        var modelPath = settings.GetModelPath();
        var vocabPath = settings.GetVocabPath();

        if (File.Exists(modelPath) && File.Exists(vocabPath))
        {
            return true;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule(":brain: [bold blue]Downloading Embedding Model[/]").RuleStyle("blue dim").LeftJustified());
        AnsiConsole.MarkupLine("  [grey]all-MiniLM-L6-v2 · sentence-transformers · ONNX[/]");
        AnsiConsole.WriteLine();

        var downloaderLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelDownloader>();
        var downloader = new ModelDownloader(settings, downloaderLogger);
        var downloadSuccess = downloader.EnsureModelsAsync().GetAwaiter().GetResult();

        if (!downloadSuccess)
        {
            AnsiConsole.MarkupLine(":cross_mark:  [red]Embedding model download failed. Continuing without semantic search.[/]");
            logger.LogWarning("Model download failed or was cancelled. Continuing without semantic search.");
            return false;
        }

        AnsiConsole.WriteLine();
        return true;
    }

    private static IEmbeddingService? TryCreateEmbeddingService(EmbeddingsSettings settings, ILogger<LocalEmbeddingService> logger)
    {
        try
        {
            var localEmbeddingService = new LocalEmbeddingService(settings, logger);

            if (localEmbeddingService.IsAvailable)
            {
                logger.LogInformation("Embedding service initialized with {Dimensions}-dimensional vectors", localEmbeddingService.Dimensions);
                return localEmbeddingService;
            }

            logger.LogWarning("Embedding service not available. Set Embeddings.AutoDownload to true in appsettings.json to enable automatic download.");
            localEmbeddingService.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize embedding service. Continuing without semantic search.");
            return null;
        }
    }

    private static IServiceCollection AddGenerativeModelService(this IServiceCollection services)
    {
        services.AddSingleton<IGenerativeModelService>(sp =>
        {
            var genSettings = sp.GetRequiredService<GenerationSettings>();
            var logger = sp.GetRequiredService<ILogger<GenerativeModelService>>();

            if (!genSettings.Enabled)
            {
                logger.LogInformation("Generative model service disabled in configuration.");
                return NullGenerativeModelService.Instance;
            }

            if (genSettings.AutoDownload)
            {
                if (!TryDownloadGenerativeModel(sp, genSettings, logger))
                    return NullGenerativeModelService.Instance;
            }

            return TryCreateGenerativeModelService(genSettings, logger)
                ?? (IGenerativeModelService)NullGenerativeModelService.Instance;
        });

        return services;
    }

    private static bool TryDownloadGenerativeModel(IServiceProvider sp, GenerationSettings settings, ILogger logger)
    {
        var allPresent = settings.Files.All(f =>
        {
            var path = Path.Combine(settings.ModelsPath, f.FileName);
            if (!File.Exists(path)) return false;
            if (f.ExpectedBytes is null) return true;
            var actual = new FileInfo(path).Length;
            return Math.Abs(actual - f.ExpectedBytes.Value) < Math.Max(1024, f.ExpectedBytes.Value / 100);
        });

        if (allPresent) return true;

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule(":robot: [bold blue]Downloading Generative Model[/]").RuleStyle("blue dim").LeftJustified());
        AnsiConsole.MarkupLine("  [grey]Phi-4-mini-instruct · Microsoft · ONNX int4 CPU · ~4.93 GB[/]");
        AnsiConsole.WriteLine();

        var downloaderLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<GenerativeModelDownloader>();
        using var downloader = new GenerativeModelDownloader(settings, downloaderLogger);
        var success = downloader.EnsureModelFilesAsync().GetAwaiter().GetResult();

        if (!success)
        {
            AnsiConsole.MarkupLine(":cross_mark:  [red]Generative model download failed. Continuing without local generation.[/]");
            logger.LogWarning("Generative model download failed. Continuing without local generation.");
            return false;
        }

        AnsiConsole.WriteLine();
        return true;
    }

    private static IGenerativeModelService? TryCreateGenerativeModelService(
        GenerationSettings settings, ILogger<GenerativeModelService> logger)
    {
        try
        {
            var svc = new GenerativeModelService(settings, logger);
            if (svc.IsAvailable)
            {
                logger.LogInformation("Generative model service ready");
                return svc;
            }

            svc.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to initialize generative model service. Continuing without local generation.");
            return null;
        }
    }

    private static IServiceCollection AddSearchService(this IServiceCollection services)
    {
        services.AddSingleton<ISearchService>(sp =>
        {
            var repository = sp.GetRequiredService<IMemoryRepository>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var logger = sp.GetRequiredService<ILogger<MemorySearchEngine>>();
            return new MemorySearchEngine(repository, embeddingService, logger);
        });

        return services;
    }

    private static IServiceCollection AddMaintenanceServices(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IMaintenanceService>(sp =>
        {
            var repository = sp.GetRequiredService<IMemoryRepository>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var logger = sp.GetRequiredService<ILogger<MaintenanceService>>();
            return new MaintenanceService(repository, embeddingService, logger);
        });

        if (settings.Maintenance.Enabled)
        {
            services.AddHostedService(sp =>
            {
                var maintenanceService = sp.GetRequiredService<IMaintenanceService>();
                var maintenanceSettings = sp.GetRequiredService<MaintenanceSettings>();
                var logger = sp.GetRequiredService<ILogger<MaintenanceBackgroundService>>();
                return new MaintenanceBackgroundService(maintenanceService, maintenanceSettings, logger);
            });
        }

        return services;
    }

    private static IServiceCollection AddConflictAwareStorage(this IServiceCollection services)
    {
        services.AddSingleton<IConflictAwareStorage>(sp =>
        {
            var repository = sp.GetRequiredService<IMemoryRepository>();
            var searchService = sp.GetRequiredService<ISearchService>();
            var embeddingService = sp.GetRequiredService<IEmbeddingService>();
            var conflictSettings = sp.GetRequiredService<ConflictSettings>();
            var logger = sp.GetRequiredService<ILogger<ConflictAwareStorageService>>();
            return new ConflictAwareStorageService(repository, searchService, embeddingService, conflictSettings, logger);
        });

        return services;
    }

    private static IServiceCollection AddMcpTools(this IServiceCollection services)
    {
        services.AddSingleton<MemoryTools>();
        return services;
    }

    private static IServiceCollection AddCodeIndexServices(
        this IServiceCollection services, CodeIndexSettings settings)
    {
        // Always register CodeIndexService so /api/file/context and /api/file/summary can inject
        // it even when CodeIndex.Enabled = false. When disabled, no providers are added and every
        // request falls through to the regex-based CodeContextExtractor fallback.
        services.AddSingleton<CodeIndexService>(sp =>
        {
            var providers = new List<ICodeIntelligenceProvider>();

            if (settings.Enabled)
            {
                if (settings.EnableCSharpRoslyn)
                {
                    var logger = sp.GetRequiredService<ILogger<CSharpRoslynProvider>>();
                    providers.Add(new CSharpRoslynProvider(logger));
                }

                if (settings.EnableTypeScriptV8)
                {
                    var tsPath = ResolveTypeScriptPath(sp, settings);
                    var logger = sp.GetRequiredService<ILogger<TypeScriptClearScriptProvider>>();
                    providers.Add(new TypeScriptClearScriptProvider(tsPath, logger));
                }
            }

            var svcLogger = sp.GetRequiredService<ILogger<CodeIndexService>>();
            var service = new CodeIndexService(providers, svcLogger);

            // Pre-register any configured project roots (whole-program index, per §3.3)
            if (settings.Enabled && settings.ProjectRoots.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var root in settings.ProjectRoots)
                    {
                        try { await service.RegisterProjectAsync(root); }
                        catch (Exception ex)
                        {
                            svcLogger.LogWarning(ex, "Startup project registration failed for {Root}", root);
                        }
                    }
                });
            }

            return service;
        });

        // ── Code Index Brain services ────────────────────────────────────────

        services.AddSingleton<ICodeIndexRepository>(sp =>
            new LiteDbCodeIndexRepository(sp.GetRequiredService<AppSettings>().Storage.DatabasePath));

        services.AddSingleton<ActiveProjectService>();

        services.AddSingleton<WorkerStatusTracker>();

        services.AddSingleton<SummaryWorker>(sp => new SummaryWorker(
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IGenerativeModelService>(),
            sp.GetRequiredService<AppSettings>().Generation,
            sp.GetRequiredService<WorkerStatusTracker>(),
            sp.GetRequiredService<ILogger<SummaryWorker>>()));
        services.AddSingleton<ISummaryQueue>(sp => sp.GetRequiredService<SummaryWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SummaryWorker>());

        services.AddSingleton<FileIngestionService>(sp => new FileIngestionService(
            sp.GetRequiredService<CodeIndexService>(),
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<ISummaryQueue>(),
            sp.GetRequiredService<ILogger<FileIngestionService>>()));

        services.AddSingleton<StalenessScanner>(sp => new StalenessScanner(
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IIngestionQueue>(),
            settings,
            sp.GetRequiredService<ILogger<StalenessScanner>>()));

        // FileIngestionWorker is the IIngestionQueue — register as singleton and surface both roles
        services.AddSingleton<FileIngestionWorker>();
        services.AddSingleton<IIngestionQueue>(sp => sp.GetRequiredService<FileIngestionWorker>());

        if (settings.EnableFileWatcher)
        {
            services.AddHostedService(sp => sp.GetRequiredService<FileIngestionWorker>());

            services.AddSingleton<ProjectFileWatcher>(sp => new ProjectFileWatcher(
                sp.GetRequiredService<ActiveProjectService>(),
                sp.GetRequiredService<StalenessScanner>(),
                sp.GetRequiredService<IIngestionQueue>(),
                sp.GetRequiredService<ICodeIndexRepository>(),
                sp.GetRequiredService<WorkerStatusTracker>(),
                sp.GetRequiredService<IKeyValueStore>(),
                settings,
                sp.GetRequiredService<ILogger<ProjectFileWatcher>>()));
            services.AddHostedService(sp => sp.GetRequiredService<ProjectFileWatcher>());
        }

        return services;
    }

    private static string? ResolveTypeScriptPath(IServiceProvider sp, CodeIndexSettings settings)
    {
        // Explicit path wins — user supplied their own typescript.js
        if (!string.IsNullOrEmpty(settings.TypeScriptCompilerPath) &&
            File.Exists(settings.TypeScriptCompilerPath))
            return settings.TypeScriptCompilerPath;

        var destPath = Path.Combine(settings.TypeScriptModelsPath, "typescript.js");

        if (File.Exists(destPath))
            return destPath;

        if (!settings.AutoDownloadTypeScript)
        {
            var logger = sp.GetRequiredService<ILogger<CodeIndexService>>();
            logger.LogWarning(
                "TypeScript provider enabled but typescript.js not found. " +
                "Set CodeIndex.AutoDownloadTypeScript=true or provide CodeIndex.TypeScriptCompilerPath.");
            return null;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule(":scroll: [bold blue]Downloading TypeScript Compiler[/]").RuleStyle("blue dim").LeftJustified());
        AnsiConsole.MarkupLine($"  [grey]typescript.js {settings.TypeScriptVersion} · unpkg.com · ~10 MB[/]");
        AnsiConsole.WriteLine();

        var dlLogger = sp.GetRequiredService<ILoggerFactory>()
            .CreateLogger<TypeScriptCompilerDownloader>();
        using var downloader = new TypeScriptCompilerDownloader(dlLogger);
        var ok = downloader.EnsureAsync(destPath, settings.TypeScriptVersion)
            .GetAwaiter().GetResult();

        AnsiConsole.WriteLine();
        return ok ? destPath : null;
    }
}
