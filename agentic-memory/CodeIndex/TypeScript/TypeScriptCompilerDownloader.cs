using Spectre.Console;

namespace AgenticMemory.CodeIndex.TypeScript;

/// <summary>
/// Downloads typescript.js from unpkg.com and caches it locally.
/// Follows the same AutoDownload pattern as ModelDownloader and GenerativeModelDownloader.
///
/// typescript.js is the isomorphic TypeScript compiler bundle — the same file the TypeScript
/// Playground runs in a browser. It requires no Node APIs, so it loads cleanly into V8 via
/// Microsoft.ClearScript without any shims.
/// </summary>
internal sealed class TypeScriptCompilerDownloader : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger<TypeScriptCompilerDownloader> _logger;

    internal TypeScriptCompilerDownloader(ILogger<TypeScriptCompilerDownloader> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("agentic-memory/1.0");
    }

    /// <summary>
    /// Ensures typescript.js is present at <paramref name="destPath"/>.
    /// Downloads from unpkg.com if not found. Returns true on success.
    /// </summary>
    internal async Task<bool> EnsureAsync(
        string destPath, string version, CancellationToken ct = default)
    {
        if (File.Exists(destPath))
        {
            _logger.LogDebug("typescript.js already present at {Path}", destPath);
            return true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        var url = $"https://unpkg.com/typescript@{version}/lib/typescript.js";
        _logger.LogInformation("Downloading typescript.js {Version} from {Url}", version, url);

        var succeeded = false;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync($"↓  [grey]Downloading[/] [blue]typescript.js {version}[/]...", async _ =>
            {
                succeeded = await DownloadAsync(url, destPath, ct);
            });

        if (succeeded)
            AnsiConsole.MarkupLine($":check_mark_button:  [green]typescript.js {version}[/] [grey]ready[/]");
        else
            AnsiConsole.MarkupLine($":cross_mark:  [red]typescript.js download failed[/] — TypeScript provider will be unavailable");

        return succeeded;
    }

    private async Task<bool> DownloadAsync(string url, string destPath, CancellationToken ct)
    {
        var tmpPath = destPath + ".tmp";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using (var dst = File.Create(tmpPath))
            {
                await src.CopyToAsync(dst, ct);
            }  // dst flushed and closed before the move

            File.Move(tmpPath, destPath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download typescript.js from {Url}", url);
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
