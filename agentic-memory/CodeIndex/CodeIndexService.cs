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

    /// <summary>
    /// Registers a sub-project with only the providers relevant to its language/type.
    /// Roslyn receives the .csproj path; TypeScript provider receives the package.json directory.
    /// </summary>
    public async Task RegisterSubProjectAsync(SubProjectRecord sub, CancellationToken ct = default)
    {
        var targetProviders = sub.Type switch
        {
            SubProjectType.CSharpProject
                => _providers.Where(p => p.ProviderType.StartsWith("dotnet")),
            SubProjectType.TypeScript or SubProjectType.Node
                => _providers.Where(p => p.ProviderType.StartsWith("typescript")),
            SubProjectType.Python
                => _providers.Where(p => p.ProviderType.StartsWith("python")),
            _ => _providers
        };

        _logger.LogInformation("Registering sub-project {Name} ({Type})", sub.Name, sub.Type);

        foreach (var provider in targetProviders)
        {
            try
            {
                // Roslyn: pass the .csproj path for project-scoped whole-program analysis.
                // TypeScript: pass the directory containing package.json.
                var target = provider.ProviderType.StartsWith("dotnet")
                    ? sub.ManifestPath
                    : sub.RootPath;

                await provider.RegisterProjectAsync(target, ct);
                _logger.LogInformation("  {Provider} → {Target}", provider.ProviderType, target);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider {Type} failed on sub-project {Name}",
                    provider.ProviderType, sub.Name);
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

    /// <summary>
    /// Finds references for all named symbols in a single compiler pass.
    /// Any provider implementing IBatchReferenceProvider (Roslyn via pre-built inverted
    /// index, TypeScript via JS buildReferenceIndex) takes the fast O(symbols) path.
    /// Falls back to per-symbol FindReferencesAsync for providers without batch support.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>> FindAllReferencesAsync(
        string filePath, IReadOnlyList<string> symbolNames, CancellationToken ct = default)
    {
        if (GetProvider(filePath) is IBatchReferenceProvider batch)
            return await batch.FindAllReferencesAsync(filePath, symbolNames, ct);

        // Fallback: delegate to per-symbol FindReferencesAsync
        var result = new Dictionary<string, IReadOnlyList<ReferenceInfo>>(StringComparer.Ordinal);
        foreach (var name in symbolNames)
        {
            ct.ThrowIfCancellationRequested();
            var refs = await FindReferencesAsync(filePath, name, ct);
            if (refs.Count > 0) result[name] = refs;
        }
        return result;
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
