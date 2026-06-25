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
    int Line);

public record ReferenceInfo(
    string FilePath,
    int Line,
    string Context);     // the expression/statement containing the reference

public record DiagnosticInfo(
    CodeDiagnosticSeverity Severity,
    string Code,
    string Message,
    int Line);

public enum CodeDiagnosticSeverity { Hidden, Info, Warning, Error }
