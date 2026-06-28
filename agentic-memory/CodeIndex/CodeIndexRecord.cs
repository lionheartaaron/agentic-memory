using LiteDB;

namespace AgenticMemory.CodeIndex;

public class CodeIndexRecord
{
    [BsonId]
    public string Id { get; set; } = "";

    public string ProjectId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Language { get; set; } = "";
    public string ProviderType { get; set; } = "";

    public string ExtractedContext { get; set; } = "";
    public string LlmSummary { get; set; } = "";
    public float[]? Embedding { get; set; }

    public List<SymbolRecord> Symbols { get; set; } = [];
    public string SymbolsText { get; set; } = "";

    public DateTime IndexedAt { get; set; }
    public DateTime FileModifiedAt { get; set; }
    public string ContentHash { get; set; } = "";

    public bool IsStale { get; set; }
    public string? IngestionError { get; set; }

    public string SubProjectId { get; set; } = "";
    // Pre-stored so LiteDB can run fast equality queries without string interpolation in predicates.
    public string SubProjectNamespace { get; set; } = "";

    // ── Symbol reference graph (populated by ReferenceIndexWorker) ────────────
    public List<string> DependsOnFileIds { get; set; } = [];  // FileIds whose symbols this file imports
    public List<string> UsedByFileIds    { get; set; } = [];  // FileIds that import this file's symbols
    public int FanIn  { get; set; }                           // unique inbound file count (denormalized)
    public int FanOut { get; set; }                           // unique outbound file count (denormalized)

    // ── Semantic extractions (populated during ingestion — Phase 4) ───────────
    public List<string> DomainTags    { get; set; } = [];  // e.g. ["aspnet-controller","react-hook"]
    public List<string> Imports       { get; set; } = [];  // raw using/import strings from the AST
    public List<string> TypeHierarchy { get; set; } = [];  // ["implements ISearchService","extends BaseRepo"]
    public string DiagnosticSummary   { get; set; } = "";  // compiler errors, condensed (UI only, not embedded)

    // ── P1 near-free rollups (test linkage; indexable scalars) ─────────────
    public bool IsTestFile { get; set; }                   // declared convention: references a known test framework
    public string? TestFramework { get; set; }             // "xunit" | "nunit" | "mstest" | "vitest" | "jest"
    public List<string> TestSubjectFileIds { get; set; } = []; // for a test file: production files it references

    public bool HasValidation { get; set; }                // any symbol carries a validation rule (P2, indexable)

    // ── P6: architectural orientation (indexable scalars) ─────────────────────
    public string? ArchitecturalRole { get; set; }         // controller/service/repository/component/page/… (from the file classifier)
    public bool IsEntrypoint { get; set; }                 // Program.cs / Main / app entry

    // For TypeScript files only: true when the file was indexed with full type resolution
    // (node_modules/typescript present), false when indexed in degraded/type-less mode (references and
    // type info are import-only), null for non-TS files. Drives the dashboard warning + auto-reindex.
    public bool? TypeScriptTypesResolved { get; set; }
}

/// <summary>
/// Mutable POCO mirror of SymbolInfo for LiteDB serialization.
/// LiteDB 5.x cannot round-trip positional records with init-only properties.
/// </summary>
public class SymbolRecord
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Type { get; set; }
    public string Accessibility { get; set; } = "";
    public int Line { get; set; }

    // ── P1 Tier 0: structured symbol shape (additive; default-empty on records predating the upgrade) ──
    public int EndLine { get; set; }

    public string? ContainingTypeFullName { get; set; }
    public string? ContainingNamespace { get; set; }
    public string? SymbolDocId { get; set; }                // GetDocumentationCommentId — stable graph key

    public List<ParameterRecord> Parameters { get; set; } = [];
    public string? ReturnTypeUnwrapped { get; set; }

    public List<string> Modifiers { get; set; } = [];
    public bool IsStatic { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsSealed { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsOverride { get; set; }
    public bool IsAsync { get; set; }

    public List<EnumMemberRecord> EnumMembers { get; set; } = [];
    public string? EnumUnderlyingType { get; set; }
    public bool IsFlags { get; set; }

    public List<AttributeRecord> Attributes { get; set; } = [];
    public string? ConstantValue { get; set; }
    public string? InitializerExpression { get; set; }

    // ── P1 near-free: type-level resource / concurrency contracts (TYPE symbols only) ──
    public bool ImplementsIDisposable { get; set; }
    public bool ImplementsIAsyncDisposable { get; set; }
    public bool IsBackgroundService { get; set; }
    public bool HasStaticMutableState { get; set; }

    // ── P2: intent & behavioral contracts ──
    public string? DocSummary { get; set; }
    public string? DocRemarks { get; set; }
    public Dictionary<string, string> ParamDocs { get; set; } = [];
    public string? ReturnsDoc { get; set; }
    public List<string> DocumentedExceptions { get; set; } = [];
    public bool IsDeprecated { get; set; }
    public string? DeprecationMessage { get; set; }
    public List<ValidationRuleRecord> ValidationRules { get; set; } = [];
    public string? NlDescription { get; set; }

    public bool IsAwaitable { get; set; }
    public bool IsAsyncEnumerable { get; set; }
    public bool UsesLock { get; set; }
    public bool BlocksOnAsync { get; set; }
    public bool UsesInterlocked { get; set; }

    // ── P4: type structure ──
    public List<TypeParameterRecord> TypeParameters { get; set; } = [];
    public List<string> BaseChain { get; set; } = [];
    public List<string> Interfaces { get; set; } = [];
    public string? OverriddenSymbolId { get; set; }

    // ── P5: behavioral (direct-throw only) ──
    public List<string> ThrownExceptions { get; set; } = [];
}
