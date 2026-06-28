using LiteDB;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// A single framework-convention fact surfaced from real compiler data — the P1
/// "promote the extracted-then-discarded domain data" layer. Providers emit these as a flat,
/// discriminated list (one shape covers routes, DI edges, EF entities, and the TS cache/nav graph)
/// so the repository needs a single collection and LiteDB never has to query into nested arrays.
///
/// METHODOLOGY (§5): the C# producer resolves every field through the SemanticModel / symbol APIs,
/// never the string-slice hacks in CSharpDomainPatterns — those stay for the coarse tag layer only.
/// </summary>
public sealed class DomainFact
{
    public string Kind { get; set; } = "";        // http-endpoint | di-injection | ef-entity | tanstack-query | tanstack-mutation | navigation-edge | fetch-endpoint
    public int Line { get; set; }
    public string? Method { get; set; }            // HTTP verb / fetch method
    public string? Route { get; set; }             // route template / URL / navigation path
    public string? Name { get; set; }              // action name / parameter name / entity type / query key
    public string? TypeRef { get; set; }           // resolved return type / dependency type / table name / query|mutation fn
    public string? OwnerType { get; set; }         // controller type / consumer (injected-into) type
    public List<string> Items { get; set; } = [];  // parameters / invalidated query keys / handler types
}

/// <summary>
/// Persisted form of <see cref="DomainFact"/>. Stored in one collection keyed
/// {fileId}::{kind}::{ordinal}; indexed on FileId / ProjectId / Kind for delete-by-file on
/// re-ingest and project-scoped queries. Mutable POCO for LiteDB round-tripping.
/// </summary>
public sealed class DomainFactRecord
{
    [BsonId]
    public string Id { get; set; } = "";           // {fileId}::{kind}::{ordinal}
    public string FileId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string? SubProjectId { get; set; }

    public string Kind { get; set; } = "";
    public int Line { get; set; }
    public string? Method { get; set; }
    public string? Route { get; set; }
    public string? Name { get; set; }
    public string? TypeRef { get; set; }
    public string? OwnerType { get; set; }
    public List<string> Items { get; set; } = [];
}
