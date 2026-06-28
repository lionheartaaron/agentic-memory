namespace AgenticMemory.CodeIndex;

/// <summary>
/// Opt-in interface for providers that can resolve references for multiple symbols
/// in a single compiler pass rather than one call per symbol.
/// Both CSharpRoslynProvider (via pre-built inverted index) and
/// TypeScriptClearScriptProvider (via the JS buildReferenceIndex) implement this.
/// CodeIndexService routes to this interface when available, avoiding the O(symbols)
/// per-symbol fallback loop.
/// </summary>
public interface IBatchReferenceProvider
{
    Task<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>> FindAllReferencesAsync(
        string filePath,
        IReadOnlyList<string> symbolNames,
        CancellationToken ct = default);
}
