using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgenticMemory.Brain.Embeddings;

/// <summary>
/// Downloads embedding model files with Spectre Console progress output.
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
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    public async Task<bool> EnsureModelsAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_settings.ModelsPath);

        var downloads = new List<(string Url, string DestPath, string Label)>();

        var modelPath = _settings.GetModelPath();
        if (!File.Exists(modelPath))
            downloads.Add((_settings.ModelUrlOnnx, modelPath, "ONNX model"));
        else
            _logger?.LogDebug("Embedding model present: {Path}", modelPath);

        var vocabPath = _settings.GetVocabPath();
        if (!File.Exists(vocabPath))
            downloads.Add((_settings.ModelVocabUrlTxt, vocabPath, "Vocabulary"));
        else
            _logger?.LogDebug("Vocabulary present: {Path}", vocabPath);

        if (downloads.Count == 0)
            return true;

        var allSucceeded = true;

        foreach (var (url, destPath, label) in downloads)
        {
            var succeeded = false;
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync($"↓  [grey]Downloading[/] [blue]{label}[/]...", async _ =>
                {
                    succeeded = await DownloadAsync(url, destPath, label, cancellationToken);
                });

            if (succeeded)
                AnsiConsole.MarkupLine($":check_mark_button:  [green]{label}[/] [grey]ready[/]");
            else
            {
                AnsiConsole.MarkupLine($":cross_mark:  [red]{label}[/] [grey]download failed[/]");
                allSucceeded = false;
            }
        }

        return allSucceeded;
    }

    private async Task<bool> DownloadAsync(string url, string destPath, string label, CancellationToken ct)
    {
        var tempPath = destPath + ".part";
        try
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var buffer = new byte[81920];
            long downloaded = 0;

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            {
                await using var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                }
            }

            File.Move(tempPath, destPath, overwrite: true);
            _logger?.LogDebug("Downloaded {Label}: {Bytes:N0} bytes", label, downloaded);
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogWarning("Download cancelled: {Label}", label);
            TryDelete(tempPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Download failed: {Label} from {Url}", label, url);
            TryDelete(tempPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
