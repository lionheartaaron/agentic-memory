using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Brain.Embeddings;

/// <summary>
/// Downloads embedding models with console progress bar
/// </summary>
public class ModelDownloader : IDisposable
{
    private readonly EmbeddingsSettings _settings;
    private readonly ILogger<ModelDownloader>? _logger;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public ModelDownloader(EmbeddingsSettings settings, ILogger<ModelDownloader>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(10); // Long timeout for large files
    }

    /// <summary>
    /// Ensure all required model files are present, downloading if necessary
    /// </summary>
    /// <returns>True if all files are present (downloaded or already existed)</returns>
    public async Task<bool> EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        // Ensure models directory exists
        if (!Directory.Exists(_settings.ModelsPath))
        {
            Directory.CreateDirectory(_settings.ModelsPath);
            _logger?.LogInformation("Created models directory: {Path}", _settings.ModelsPath);
        }

        var modelPath = _settings.GetModelPath();
        var vocabPath = _settings.GetVocabPath();

        var success = true;

        // Download ONNX model if not present
        if (!File.Exists(modelPath))
        {
            _logger?.LogInformation("ONNX model not found. Downloading from {Url}", _settings.ModelUrlOnnx);
            success &= await DownloadFileWithProgressAsync(
                _settings.ModelUrlOnnx,
                modelPath,
                "ONNX Model",
                cancellationToken);
        }
        else
        {
            _logger?.LogInformation("ONNX model already exists: {Path}", modelPath);
        }

        // Download vocabulary if not present
        if (!File.Exists(vocabPath))
        {
            _logger?.LogInformation("Vocabulary file not found. Downloading from {Url}", _settings.ModelVocabUrlTxt);
            success &= await DownloadFileWithProgressAsync(
                _settings.ModelVocabUrlTxt,
                vocabPath,
                "Vocabulary",
                cancellationToken);
        }
        else
        {
            _logger?.LogInformation("Vocabulary file already exists: {Path}", vocabPath);
        }

        return success;
    }

    private async Task<bool> DownloadFileWithProgressAsync(
        string url,
        string destinationPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var canReportProgress = totalBytes > 0;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            long totalBytesRead = 0;
            int bytesRead;
            var lastProgressUpdate = DateTime.MinValue;
            var progressUpdateInterval = TimeSpan.FromMilliseconds(100);

            // Print initial progress line (ASCII compatible)
            Console.Write($"\r  Downloading {displayName}: ");
            if (canReportProgress)
            {
                Console.Write("[------------------------------] 0%");
            }
            else
            {
                Console.Write("0 KB");
            }

            while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalBytesRead += bytesRead;

                // Update progress bar (throttled)
                if (DateTime.UtcNow - lastProgressUpdate > progressUpdateInterval)
                {
                    lastProgressUpdate = DateTime.UtcNow;
                    PrintProgress(displayName, totalBytesRead, totalBytes, canReportProgress);
                }
            }

            // Final progress update
            PrintProgress(displayName, totalBytesRead, totalBytes, canReportProgress);
            Console.WriteLine(" ?");

            var fileSizeMb = totalBytesRead / (1024.0 * 1024.0);
            _logger?.LogInformation("Downloaded {Name}: {Size:F2} MB", displayName, fileSizeMb);

            return true;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine(" Cancelled");
            _logger?.LogWarning("Download cancelled: {Name}", displayName);
            
            // Clean up partial file
            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); } catch { }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine(" Failed");
            _logger?.LogError(ex, "Failed to download {Name} from {Url}", displayName, url);
            
            // Clean up partial file
            if (File.Exists(destinationPath))
            {
                try { File.Delete(destinationPath); } catch { }
            }
            
            return false;
        }
    }

    private static void PrintProgress(string displayName, long bytesRead, long totalBytes, bool canReportProgress)
    {
        Console.Write($"\r  Downloading {displayName}: ");

        if (canReportProgress && totalBytes > 0)
        {
            var percentage = (int)((bytesRead * 100) / totalBytes);
            var progressBarWidth = 30;
            var filledWidth = (int)((bytesRead * progressBarWidth) / totalBytes);
            var emptyWidth = progressBarWidth - filledWidth;

            // Use ASCII characters for compatibility: # for filled, - for empty
            var progressBar = new string('#', filledWidth) + new string('-', emptyWidth);
            var sizeMb = bytesRead / (1024.0 * 1024.0);
            var totalMb = totalBytes / (1024.0 * 1024.0);

            Console.Write($"[{progressBar}] {percentage,3}% ({sizeMb:F1}/{totalMb:F1} MB)");
        }
        else
        {
            var sizeKb = bytesRead / 1024.0;
            Console.Write($"{sizeKb:F0} KB downloaded...");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
