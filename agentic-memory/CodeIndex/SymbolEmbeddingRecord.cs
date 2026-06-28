using LiteDB;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// One SBERT vector per public/exported/internal symbol, over a deterministic NL descriptor
/// (P3). Enables semantic "find the method that does X" at SYMBOL granularity — the file-level
/// embedding cannot localize to a symbol.
///
/// Stored in its own collection. The Id carries an arity discriminator ({fileId}::{name}::{arity})
/// so overloaded methods / multiple constructors get distinct vectors instead of silently merging.
/// Search MUST prefilter by ProjectId/SubProjectId before the O(d) cosine scan (VectorMath is
/// unindexed) — the symbol set is many times the file set.
/// </summary>
public sealed class SymbolEmbeddingRecord
{
    [BsonId]
    public string Id { get; set; } = "";           // {fileId}::{name}::{arity}
    public string ProjectId { get; set; } = "";
    public string? SubProjectId { get; set; }
    public string FileId { get; set; } = "";
    public string RelativePath { get; set; } = "";

    public string SymbolName { get; set; } = "";
    public string? ContainingType { get; set; }
    public string Kind { get; set; } = "";
    public int Line { get; set; }
    public int EndLine { get; set; }

    public float[]? Vector { get; set; }
    public string EmbedTextHash { get; set; } = "";  // SHA256 of the embed text — incremental re-embed guard
    public string ModelId { get; set; } = "";
    public int Dim { get; set; }
}
