using AgenticMemory.Brain.Conflict;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Search;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemoryTests.Shared;

/// <summary>
/// Shared test fixture that handles common initialization for all test classes.
/// Provides repository, embedding service, search service, and conflict storage.
/// </summary>
public class TestFixture : IAsyncDisposable
{
    // Shared model download lock - ensures models are only downloaded once across all tests
    private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);
    private static bool _modelsDownloaded;

    public string TestDbPath { get; }
    public string TestModelsPath { get; }
    public IMemoryRepository Repository { get; private set; } = null!;
    public IEmbeddingService? EmbeddingService { get; private set; }
    public ISearchService SearchService { get; private set; } = null!;
    public IConflictAwareStorage ConflictStorage { get; private set; } = null!;
    public ILoggerFactory LoggerFactory { get; private set; } = null!;

    public TestFixture()
    {
        var testId = Guid.NewGuid().ToString("N")[..8];
        TestDbPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", $"test-{testId}.db");
        TestModelsPath = Path.Combine(Path.GetTempPath(), "agentic-memory-tests", "models");
    }

    public async Task InitializeAsync()
    {
        // Create directories
        var dbDir = Path.GetDirectoryName(TestDbPath);
        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        if (!Directory.Exists(TestModelsPath))
            Directory.CreateDirectory(TestModelsPath);

        // Setup logging
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(builder =>
        {
            builder.AddConsole().SetMinimumLevel(LogLevel.Warning);
        });

        // Download models if needed (thread-safe singleton pattern)
        await EnsureModelsDownloadedAsync();

        // Delete existing database if present (fresh start for each test)
        if (File.Exists(TestDbPath))
            File.Delete(TestDbPath);

        // Initialize repository
        Repository = new LiteDbMemoryRepository(TestDbPath);

        // Initialize embedding service
        var embeddingsSettings = new EmbeddingsSettings
        {
            Enabled = true,
            ModelsPath = TestModelsPath,
            AutoDownload = false
        };

        try
        {
            var localEmbedding = new LocalEmbeddingService(
                embeddingsSettings,
                LoggerFactory.CreateLogger<LocalEmbeddingService>());

            if (localEmbedding.IsAvailable)
            {
                EmbeddingService = localEmbedding;
            }
            else
            {
                localEmbedding.Dispose();
            }
        }
        catch
        {
            // Embedding service not available
        }

        // Initialize search service
        SearchService = new MemorySearchEngine(
            Repository,
            EmbeddingService,
            LoggerFactory.CreateLogger<MemorySearchEngine>());

        // Initialize conflict-aware storage
        var conflictSettings = new ConflictSettings();
        ConflictStorage = new ConflictAwareStorageService(
            Repository,
            SearchService,
            EmbeddingService,
            conflictSettings,
            LoggerFactory.CreateLogger<ConflictAwareStorageService>());
    }

    private async Task EnsureModelsDownloadedAsync()
    {
        if (_modelsDownloaded)
            return;

        await ModelDownloadLock.WaitAsync();
        try
        {
            if (_modelsDownloaded)
                return;

            var embeddingsSettings = new EmbeddingsSettings
            {
                Enabled = true,
                ModelsPath = TestModelsPath,
                AutoDownload = true
            };

            var modelPath = embeddingsSettings.GetModelPath();
            var vocabPath = embeddingsSettings.GetVocabPath();

            if (!File.Exists(modelPath) || !File.Exists(vocabPath))
            {
                var downloader = new ModelDownloader(
                    embeddingsSettings,
                    LoggerFactory.CreateLogger<ModelDownloader>());

                var success = await downloader.EnsureModelsAsync();
                downloader.Dispose();

                if (!success)
                {
                    throw new InvalidOperationException("Failed to download embedding models for tests");
                }
            }

            _modelsDownloaded = true;
        }
        finally
        {
            ModelDownloadLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Dispose services - catch mutex exceptions that can occur when
        // DisposeAsync is called from a different thread than InitializeAsync
        try { EmbeddingService?.Dispose(); }
        catch (ApplicationException) { }

        try { Repository?.Dispose(); }
        catch (ApplicationException) { }

        try { LoggerFactory?.Dispose(); }
        catch (ApplicationException) { }

        // Clean up test database
        await Task.Delay(100); // Give LiteDB time to release file
        try
        {
            if (File.Exists(TestDbPath))
                File.Delete(TestDbPath);

            var journalPath = TestDbPath + "-journal";
            if (File.Exists(journalPath))
                File.Delete(journalPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
