using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Maintenance;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using AgenticMemory.Tools;

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
        services.AddEmbeddingService();
        services.AddSearchService();
        services.AddMaintenanceServices(settings);
        services.AddConflictAwareStorage();
        services.AddMcpTools();

        return services;
    }

    private static IServiceCollection AddConfiguration(this IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton(settings.Storage);
        services.AddSingleton(settings.Conflict);
        services.AddSingleton(settings.Embeddings);
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

        Console.WriteLine();
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine("?  Downloading Embedding Models                                ?");
        Console.WriteLine("????????????????????????????????????????????????????????????????");
        Console.WriteLine();

        var downloaderLogger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelDownloader>();
        var downloader = new ModelDownloader(settings, downloaderLogger);
        var downloadSuccess = downloader.EnsureModelsAsync().GetAwaiter().GetResult();

        if (!downloadSuccess)
        {
            logger.LogWarning("Model download failed or was cancelled. Continuing without semantic search.");
            return false;
        }

        Console.WriteLine();
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
}
