using System.Net;
using System.Net.Http.Headers;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace AgenticMemory.Brain.Generation;

public class GenerativeModelDownloader : IDisposable
{
    private readonly GenerationSettings _settings;
    private readonly ILogger<GenerativeModelDownloader>? _logger;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public GenerativeModelDownloader(GenerationSettings settings, ILogger<GenerativeModelDownloader>? logger = null)
    {
        _settings = settings;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.Timeout = TimeSpan.FromHours(2);
    }

    public async Task<bool> EnsureModelFilesAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_settings.ModelsPath);

        var plan = _settings.Files
            .Select(f =>
            {
                var dest = Path.Combine(_settings.ModelsPath, f.FileName);
                return (File: f, Dest: dest, Skip: !ShouldDownload(f, dest));
            })
            .ToList();

        if (plan.All(p => p.Skip))
        {
            _logger?.LogDebug("All generative model files already present");
            return true;
        }

        var allSucceeded = true;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .HideCompleted(false)
            .Columns(
                new TaskDescriptionColumn { Alignment = Justify.Left },
                new ProgressBarColumn(),
                new PercentageColumn(),
                new DownloadedColumn(),
                new TransferSpeedColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn()
            )
            .StartAsync(async ctx =>
            {
                foreach (var (file, dest, skip) in plan)
                {
                    if (skip)
                    {
                        var doneTask = ctx.AddTask(
                            $"✅  {Markup.Escape(file.FileName)}",
                            maxValue: file.ExpectedBytes ?? 1);
                        doneTask.Increment(doneTask.MaxValue);
                        continue;
                    }

                    if (!_settings.AutoDownload)
                    {
                        _logger?.LogError("{File} missing and AutoDownload is disabled", file.FileName);
                        allSucceeded = false;
                        continue;
                    }

                    var tempPath = dest + ".part";
                    var resumeFrom = GetResumeOffset(tempPath, file.ExpectedBytes);

                    var task = ctx.AddTask(
                        $"{Markup.Escape("[↓]")} {Markup.Escape(file.FileName)}",
                        maxValue: file.ExpectedBytes > 0 ? file.ExpectedBytes.Value : 1);

                    if (resumeFrom > 0)
                        task.Increment(resumeFrom);

                    var succeeded = await DownloadFileAsync(file, dest, task, resumeFrom, cancellationToken);
                    if (succeeded)
                        task.Description = $"✅  {Markup.Escape(file.FileName)}";
                    else
                        allSucceeded = false;
                }
            });

        return allSucceeded;
    }

    private static long GetResumeOffset(string tempPath, long? expectedBytes)
    {
        if (!File.Exists(tempPath)) return 0;
        var partSize = new FileInfo(tempPath).Length;
        // Discard a corrupt or oversized partial file
        if (expectedBytes.HasValue && partSize >= expectedBytes.Value)
        {
            TryDeletePartial(tempPath);
            return 0;
        }
        return partSize;
    }

    private static bool ShouldDownload(ModelFileSpec file, string destPath)
    {
        if (!File.Exists(destPath)) return true;
        if (file.ExpectedBytes is null) return false;
        var actual = new FileInfo(destPath).Length;
        return Math.Abs(actual - file.ExpectedBytes.Value) >= Math.Max(1024, file.ExpectedBytes.Value / 100);
    }

    private async Task<bool> DownloadFileAsync(
        ModelFileSpec file,
        string destPath,
        ProgressTask progressTask,
        long resumeFrom,
        CancellationToken ct)
    {
        var url = $"{_settings.RepoBaseUrl.TrimEnd('/')}/{file.FileName}";
        var tempPath = destPath + ".part";

        _logger?.LogDebug("Downloading {File} (offset {Offset:N0})", file.FileName, resumeFrom);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (resumeFrom > 0)
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);

            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            // Server declined the range request — start over
            if (resumeFrom > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                resumeFrom = 0;
                progressTask.Value = 0;
                TryDeletePartial(tempPath);
            }

            response.EnsureSuccessStatusCode();

            // Sync maxValue with the actual total size now that we have the real Content-Length
            var contentLength = response.Content.Headers.ContentLength;
            if (contentLength.HasValue)
            {
                var totalBytes = resumeFrom + contentLength.Value;
                if (Math.Abs(totalBytes - progressTask.MaxValue) > 1024)
                    progressTask.MaxValue = totalBytes;
            }

            var buffer = new byte[81920];
            var fileMode = resumeFrom > 0 ? FileMode.Append : FileMode.Create;

            await using var content = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(
                tempPath, fileMode, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                progressTask.Increment(read);
            }

            // Snap to 100% in case of minor size drift
            var remaining = progressTask.MaxValue - progressTask.Value;
            if (remaining > 0) progressTask.Increment(remaining);

            File.Move(tempPath, destPath, overwrite: true);
            _logger?.LogDebug("Finished {File}", file.FileName);
            return true;
        }
        catch (OperationCanceledException)
        {
            // Leave the .part file so the next run can resume
            _logger?.LogDebug("Download paused: {File}", file.FileName);
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to download {File}", file.FileName);
            TryDeletePartial(tempPath);
            return false;
        }
    }

    private static void TryDeletePartial(string path)
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
