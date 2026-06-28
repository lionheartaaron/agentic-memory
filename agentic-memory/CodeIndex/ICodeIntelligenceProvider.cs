namespace AgenticMemory.CodeIndex;

public record SemanticMetadata(
    List<string> DomainTags,
    List<string> Imports,
    List<string> TypeHierarchy,
    string DiagnosticSummary)
{
    public static SemanticMetadata Empty => new([], [], [], "");
}

/// <summary>
/// Per §1 of code-understanding-methodology.md: every provider (a) routes symbol/type/reference
/// queries through the real compiler API for its language, and (b) declares an explicit list of
/// domain patterns it hand-detects on top of that API. A type that cannot satisfy both points is
/// either reimplementing a compiler or silently omitting domain signal.
/// </summary>
public interface ICodeIntelligenceProvider : IAsyncDisposable
{
    /// <summary>Short identifier, e.g. "dotnet-csharp" or "typescript-react-native-expo".</summary>
    string ProviderType { get; }

    /// <summary>Names the compiler API being called and the domain-pattern families this provider hand-detects.</summary>
    TypeCapabilities Capabilities { get; }

    /// <summary>Returns true if this provider should handle the given file.</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Registers a project root for whole-program analysis. Per §3.3 (adapted for each language):
    /// all source files under the root must be enumerated and compiled as one program, not parsed
    /// independently, so that cross-file reference resolution (barrel re-exports, alias chains) works.
    /// </summary>
    Task RegisterProjectAsync(string projectRoot, CancellationToken ct = default);

    /// <summary>
    /// Extracts a structured, LLM-ready context summary for the file. Uses the language's real
    /// compiler for type/symbol data; supplements with hand-rolled domain-pattern detection per §3.4.
    /// </summary>
    Task<string> ExtractContextAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Returns all declared symbols in the file. Every result routes through the compiler's semantic
    /// model — no hand-written type inference.
    /// </summary>
    Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Finds all references to a named symbol across the whole registered project. Requires a whole-
    /// program compilation (registered via RegisterProjectAsync) — single-file parsing cannot resolve
    /// references through barrel re-exports or type aliases.
    /// </summary>
    Task<IReadOnlyList<ReferenceInfo>> FindReferencesAsync(string filePath, string symbolName, CancellationToken ct = default);

    /// <summary>Returns semantic (not syntax) diagnostics for the file from the real compiler.</summary>
    Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Extracts structured semantic metadata: domain tags, import namespaces, type hierarchy entries,
    /// and a condensed diagnostic summary. Providers that cannot supply a field return empty defaults.
    /// </summary>
    Task<SemanticMetadata> ExtractSemanticMetadataAsync(string filePath, CancellationToken ct = default)
        => Task.FromResult(SemanticMetadata.Empty);

    /// <summary>
    /// Promotes the framework-convention data the provider already detects (HTTP routes, DI edges,
    /// EF entities, TanStack query/mutation cache graph, navigation/fetch endpoints) into a flat,
    /// queryable list. Producers MUST resolve fields through the real compiler API (§5 — no syntactic
    /// guesses persisted). Providers with no domain layer return an empty list.
    /// </summary>
    Task<IReadOnlyList<DomainFact>> ExtractDomainFactsAsync(string filePath, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DomainFact>>([]);
}
