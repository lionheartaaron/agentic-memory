namespace AgenticMemory.CodeIndex;

/// <summary>
/// Per §5 of code-understanding-methodology.md: every type declares (a) which real compiler API
/// it uses for symbol queries, and (b) an explicit, named list of domain patterns it hand-detects
/// on top of that API. A type missing (b) for a project with known framework conventions is leaving
/// value on the table without saying so.
/// </summary>
public record TypeCapabilities(
    /// <summary>
    /// The real compiler API being hosted. E.g. "Roslyn CSharpCompilation / SemanticModel"
    /// or "TypeScript LanguageService via Microsoft.ClearScript.V8".
    /// </summary>
    string CompilerApi,

    /// <summary>
    /// Explicit list of domain-pattern families this provider hand-detects. Never silently absent:
    /// if the target project has framework conventions worth surfacing, they must appear here.
    /// </summary>
    string[] DomainPatternFamilies);

public record SymbolInfo(
    string Name,
    string Kind,          // "class", "interface", "method", "property", "field", etc.
    string? Type,         // resolved type string from the semantic model
    string Accessibility, // "public", "internal", "private", "protected"
    int Line)
{
    // ── P1 Tier 0: structured symbol shape (all optional; absent => provider could not resolve) ──
    public int EndLine { get; init; }                       // last line of the declaration span

    // identity & linkage
    public string? ContainingTypeFullName { get; init; }
    public string? ContainingNamespace { get; init; }
    public string? SymbolDocId { get; init; }               // ISymbol.GetDocumentationCommentId — stable graph key

    // signature
    public List<ParameterRecord> Parameters { get; init; } = [];
    public string? ReturnTypeUnwrapped { get; init; }       // Task<T>/ValueTask<T>/IAsyncEnumerable<T> -> T

    // modifiers
    public List<string> Modifiers { get; init; } = [];
    public bool IsStatic { get; init; }
    public bool IsAbstract { get; init; }
    public bool IsSealed { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsOverride { get; init; }
    public bool IsAsync { get; init; }

    // enum
    public List<EnumMemberRecord> EnumMembers { get; init; } = [];
    public string? EnumUnderlyingType { get; init; }
    public bool IsFlags { get; init; }

    // attributes / constants
    public List<AttributeRecord> Attributes { get; init; } = [];
    public string? ConstantValue { get; init; }             // canonical string repr (LiteDB-safe)
    public string? InitializerExpression { get; init; }     // literals + short expressions only

    // ── P1 near-free: type-level resource / concurrency contracts (TYPE symbols only) ──
    public bool ImplementsIDisposable { get; init; }
    public bool ImplementsIAsyncDisposable { get; init; }
    public bool IsBackgroundService { get; init; }          // implements IHostedService (covers BackgroundService)
    public bool HasStaticMutableState { get; init; }        // static non-const non-readonly field present

    // ── P2: intent & behavioral contracts ──
    public string? DocSummary { get; init; }                // <summary> / JSDoc lead
    public string? DocRemarks { get; init; }
    public Dictionary<string, string> ParamDocs { get; init; } = [];
    public string? ReturnsDoc { get; init; }
    public List<string> DocumentedExceptions { get; init; } = [];
    public bool IsDeprecated { get; init; }
    public string? DeprecationMessage { get; init; }
    public List<ValidationRuleRecord> ValidationRules { get; init; } = [];
    public string? NlDescription { get; init; }             // deterministic one-line descriptor (no LLM)

    public bool IsAwaitable { get; init; }                  // returns Task/ValueTask/etc (even if not `async`)
    public bool IsAsyncEnumerable { get; init; }
    public bool UsesLock { get; init; }                     // lock {} in body
    public bool BlocksOnAsync { get; init; }                // .Result / .Wait() / .GetAwaiter().GetResult()
    public bool UsesInterlocked { get; init; }

    // ── P4: type structure ──
    public List<TypeParameterRecord> TypeParameters { get; init; } = [];
    public List<string> BaseChain { get; init; } = [];      // ordered base types (TYPE symbols), nearest first
    public List<string> Interfaces { get; init; } = [];     // all implemented interfaces (TYPE symbols)
    public string? OverriddenSymbolId { get; init; }        // DocId of the overridden member (override methods)

    // ── P5: behavioral (direct-throw only; transitive throws need the call graph) ──
    public List<string> ThrownExceptions { get; init; } = [];
}

public record ReferenceInfo(
    string FilePath,
    int Line,
    string Context)      // the expression/statement containing the reference
{
    // P2: usage-kind label — call / new / read / write / typeref / implements / override.
    public string Role { get; init; } = "ref";

    // P5: the symbol that CONTAINS this usage (caller attribution → symbol→symbol call graph).
    public string? EnclosingSymbolId { get; init; }
    public string? EnclosingName { get; init; }

    // Identity of the symbol this reference actually resolved to (GetDocumentationCommentId of the
    // OriginalDefinition). The reference index is bucketed by the textual NAME for fast lookup, but a
    // name can map to several declarations (overloads, same-named members on different types). Tagging
    // the resolved DocId lets a consumer attribute each site to the exact symbol it targets — without
    // it, overloads conflate and same-named symbols steal each other's references. Null for languages
    // (TypeScript) whose provider does not resolve a stable symbol id.
    public string? TargetDocId { get; init; }
}

public record DiagnosticInfo(
    CodeDiagnosticSeverity Severity,
    string Code,
    string Message,
    int Line);

public enum CodeDiagnosticSeverity { Hidden, Info, Warning, Error }
