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
}
