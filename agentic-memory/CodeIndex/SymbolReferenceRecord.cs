using LiteDB;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// One document per symbol definition. All usage sites are embedded.
/// ID = "{defFileId}::{symbolName}" — guarantees idempotent upserts.
/// </summary>
public class SymbolReferenceRecord
{
    [BsonId]
    public string Id { get; set; } = "";               // "{defFileId}::{symbolName}"

    public string SymbolName     { get; set; } = "";
    public string SymbolKind     { get; set; } = "";   // "class","interface","method","property", …
    public string Accessibility  { get; set; } = "";   // "public","internal","protected","private"

    public string  DefinedInFileId       { get; set; } = "";
    public string  DefinedInRelativePath { get; set; } = "";
    public int     DefinedAtLine         { get; set; }

    public string  ProjectId    { get; set; } = "";
    public string? SubProjectId { get; set; }

    public List<SymbolUsageSite> UsedBy { get; set; } = [];

    // ── P1 near-free rollups over UsedBy (denormalized, indexable) ────────────
    public int ExternalUseCount { get; set; }              // UsedBy count excluding the defining file
    public bool IsOrphan { get; set; }                     // ExternalUseCount == 0 — dead-code candidate
    public List<string> TestedByFileIds { get; set; } = []; // subset of UsedBy whose file IsTestFile

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A single location where a symbol is referenced by another file.
/// LiteDB mutable POCO — no init-only properties.
/// </summary>
public class SymbolUsageSite
{
    public string FileId       { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public int    Line         { get; set; }
    public string Context      { get; set; } = "";    // surrounding expression, ≤ 120 chars
    public string Role         { get; set; } = "ref"; // P2: call/new/read/write/typeref/implements/override
    public string? EnclosingSymbolId { get; set; }    // P5: the symbol that contains this usage (caller)
    public string? EnclosingName     { get; set; }
}
