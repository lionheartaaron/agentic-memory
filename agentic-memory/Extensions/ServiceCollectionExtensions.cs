using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Generation;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
using AgenticMemory.CodeIndex;
using AgenticMemory.CodeIndex.CSharp;
using AgenticMemory.CodeIndex.TypeScript;
using AgenticMemory.Configuration;
using AgenticMemory.Persistence;
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
    /// <param name="database">
    /// An already-open database. The host passes one so that opening the file — and therefore the
    /// schema migration that happens on open — is a step it can order and report on, rather than a
    /// side effect of the first service to be resolved.
    /// </param>
    public static IServiceCollection AddAgenticMemoryServices(
        this IServiceCollection services, AppSettings settings, SharedLiteDatabase? database = null)
    {
        services.AddConfiguration(settings);
        services.AddSingleton(database ?? new SharedLiteDatabase(
            settings.Storage.DatabasePath, settings.Maintenance.BackupPath));
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
        services.AddSingleton(settings.Retrieval);
        services.AddSingleton(settings.CodeIndex);

        // Slot definitions govern conflict resolution; a single shared registry keeps the store
        // path and the maintenance path in agreement.
        services.AddSingleton(new SlotRegistry());

        return services;
    }

    private static IServiceCollection AddMemoryRepository(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IMemoryEventLog>(sp =>
            new LiteDbMemoryEventLog(sp.GetRequiredService<SharedLiteDatabase>()));

        // One instance serves both surfaces. They are separate interfaces so that a service taking
        // only IMemoryRepository cannot reach across the user boundary even by accident.
        services.AddSingleton(sp => new LiteDbMemoryRepository(
            sp.GetRequiredService<SharedLiteDatabase>(),
            sp.GetRequiredService<IMemoryEventLog>(),
            sp.GetRequiredService<ILogger<LiteDbMemoryRepository>>()));

        services.AddSingleton<IMemoryRepository>(sp => sp.GetRequiredService<LiteDbMemoryRepository>());
        services.AddSingleton<IMemoryAdminStore>(sp => sp.GetRequiredService<LiteDbMemoryRepository>());
        services.AddSingleton<MemoryVectorCache>(_ => new MemoryVectorCache());
        services.AddSingleton<MemoryLexicalCache>(_ => new MemoryLexicalCache());

        return services;
    }

    private static IServiceCollection AddKeyValueStore(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IKeyValueStore>(sp =>
            new LiteDbKeyValueStore(sp.GetRequiredService<SharedLiteDatabase>()));
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
        services.AddSingleton<ISearchService>(sp => new MemorySearchEngine(
            sp.GetRequiredService<IMemoryRepository>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<RetrievalSettings>(),
            sp.GetRequiredService<MemoryVectorCache>(),
            sp.GetRequiredService<MemoryLexicalCache>(),
            sp.GetRequiredService<ILogger<MemorySearchEngine>>()));

        return services;
    }

    private static IServiceCollection AddMaintenanceServices(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton<IMemoryBackupService>(sp => new LiteDbBackupService(
            sp.GetRequiredService<SharedLiteDatabase>(),
            sp.GetRequiredService<MaintenanceSettings>(),
            sp.GetRequiredService<ILogger<LiteDbBackupService>>()));

        services.AddSingleton<IMaintenanceService>(sp => new MaintenanceService(
            sp.GetRequiredService<IMemoryRepository>(),
            sp.GetRequiredService<IMemoryAdminStore>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<MaintenanceSettings>(),
            sp.GetRequiredService<IMemoryBackupService>(),
            sp.GetRequiredService<ILogger<MaintenanceService>>()));

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
        services.AddSingleton<IConflictAwareStorage>(sp => new ConflictAwareStorageService(
            sp.GetRequiredService<IMemoryRepository>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<ConflictSettings>(),
            sp.GetRequiredService<SlotRegistry>(),
            sp.GetRequiredService<ILogger<ConflictAwareStorageService>>()));

        return services;
    }

    private static IServiceCollection AddMcpTools(this IServiceCollection services)
    {
        services.AddSingleton<MemoryTools>();
        services.AddSingleton<AgenticMemory.Tools.CodeIndexTools>();
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
                    providers.Add(new CSharpRoslynProvider(logger, settings));
                }

                if (settings.EnableTypeScriptV8)
                {
                    var tsPath = ResolveTypeScriptPath(sp, settings);
                    var logger = sp.GetRequiredService<ILogger<TypeScriptClearScriptProvider>>();
                    providers.Add(new TypeScriptClearScriptProvider(tsPath, logger, settings));
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
            new LiteDbCodeIndexRepository(sp.GetRequiredService<SharedLiteDatabase>()));

        services.AddSingleton<ActiveProjectService>();

        services.AddSingleton<WorkerStatusTracker>();

        services.AddSingleton<SummaryWorker>(sp => new SummaryWorker(
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IGenerativeModelService>(),
            sp.GetRequiredService<AppSettings>().Generation,
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<WorkerStatusTracker>(),
            sp.GetRequiredService<ILogger<SummaryWorker>>()));
        services.AddSingleton<ISummaryQueue>(sp => sp.GetRequiredService<SummaryWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<SummaryWorker>());

        // ReferenceIndexWorker — singleton + hosted + IReferenceQueue surface (same pattern as SummaryWorker)
        services.AddSingleton<ReferenceIndexWorker>();
        services.AddSingleton<IReferenceQueue>(sp => sp.GetRequiredService<ReferenceIndexWorker>());
        services.AddHostedService(sp => sp.GetRequiredService<ReferenceIndexWorker>());

        services.AddSingleton<FileIngestionService>(sp => new FileIngestionService(
            sp.GetRequiredService<CodeIndexService>(),
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<ISummaryQueue>(),
            sp.GetRequiredService<IReferenceQueue>(),
            sp.GetRequiredService<ILogger<FileIngestionService>>()));

        services.AddSingleton<WorkspaceDiscoveryService>();

        services.AddSingleton<StalenessScanner>(sp => new StalenessScanner(
            sp.GetRequiredService<ICodeIndexRepository>(),
            sp.GetRequiredService<IIngestionQueue>(),
            sp.GetRequiredService<ISummaryQueue>(),
            sp.GetRequiredService<IReferenceQueue>(),
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
