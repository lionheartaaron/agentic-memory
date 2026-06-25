using AgenticMemory.Helpers;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Orchestrates all registered ICodeIntelligenceProvider instances. Routes file requests to the
/// matching provider; falls back to the legacy CodeContextExtractor when no provider handles the
/// file type (covers Python, generic text, etc.).
/// </summary>
public sealed class CodeIndexService : IDisposable
{
    private readonly IReadOnlyList<ICodeIntelligenceProvider> _providers;
    private readonly ILogger<CodeIndexService> _logger;

    public CodeIndexService(IEnumerable<ICodeIntelligenceProvider> providers, ILogger<CodeIndexService> logger)
    {
        _providers = providers.ToList();
        _logger = logger;
    }

    public ICodeIntelligenceProvider? GetProvider(string filePath)
        => _providers.FirstOrDefault(p => p.CanHandle(filePath));

    /// <summary>
    /// Extracts a structured, LLM-ready context summary using the real compiler for the file's
    /// language when a provider is registered. Falls back to the regex-based CodeContextExtractor
    /// when no provider matches (Phase 1 behaviour preserved as the fallback).
    /// </summary>
    public async Task<string> ExtractContextAsync(string filePath, CancellationToken ct = default)
    {
        var provider = GetProvider(filePath);
        if (provider is null)
        {
            _logger.LogInformation("No provider for {File} — using static extractor", Path.GetFileName(filePath));
            return CodeContextExtractor.ExtractContext(filePath);
        }

        _logger.LogInformation("Extracting context for {File} via {Provider}", Path.GetFileName(filePath), provider.ProviderType);
        try
        {
            var result = await provider.ExtractContextAsync(filePath, ct);
            if (!string.IsNullOrEmpty(result))
            {
                var firstLine = result.Split('\n', 2)[0].Trim();
                _logger.LogInformation("Context ready ({Chars} chars) — {FirstLine}", result.Length, firstLine);
                return result;
            }

            _logger.LogInformation("Provider {Provider} returned empty for {File} — falling back to static extractor", provider.ProviderType, Path.GetFileName(filePath));
            return CodeContextExtractor.ExtractContext(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider {Provider} failed for {File} — falling back to static extractor", provider.ProviderType, Path.GetFileName(filePath));
            return CodeContextExtractor.ExtractContext(filePath);
        }
    }

    /// <summary>
    /// Registers a project root with all providers. Safe to call before any file queries;
    /// providers that cannot handle the project type skip registration silently.
    /// </summary>
    public async Task RegisterProjectAsync(string projectRoot, CancellationToken ct = default)
    {
        _logger.LogInformation("Registering project {Root} with {Count} provider(s)", projectRoot, _providers.Count);
        foreach (var provider in _providers)
        {
            try
            {
                await provider.RegisterProjectAsync(projectRoot, ct);
                _logger.LogInformation("  {Provider} registered {Root}", provider.ProviderType, projectRoot);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Type} failed to register project {Root}", provider.ProviderType, projectRoot);
            }
        }
    }

    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string filePath, CancellationToken ct = default)
    {
        var provider = GetProvider(filePath);
        if (provider is null) return [];
        return await provider.GetSymbolsAsync(filePath, ct);
    }

    public async Task<IReadOnlyList<ReferenceInfo>> FindReferencesAsync(string filePath, string symbolName, CancellationToken ct = default)
    {
        var provider = GetProvider(filePath);
        if (provider is null) return [];
        return await provider.FindReferencesAsync(filePath, symbolName, ct);
    }

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default)
    {
        var provider = GetProvider(filePath);
        if (provider is null) return [];
        return await provider.GetDiagnosticsAsync(filePath, ct);
    }

    public IReadOnlyList<ICodeIntelligenceProvider> Providers => _providers;

    public void Dispose()
    {
        foreach (var p in _providers)
        {
            try { p.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch { /* best effort */ }
        }
    }
}
