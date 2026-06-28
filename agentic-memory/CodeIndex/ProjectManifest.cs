using LiteDB;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// The project's own declarative manifest — the "what is this project, what does it depend on" layer
/// (P0 / §4.1). Parsed from .csproj / package.json / tsconfig.json (declarative config, legitimately
/// hand-parsed — not compiler output). Gives an agent the dependency/target/output picture without
/// reading build files, and underpins the reference-resolution soundness fix.
/// </summary>
public sealed class ProjectManifestRecord
{
    [BsonId]
    public string Id { get; set; } = "";           // {projectId}::{manifestPathLower}
    public string ProjectId { get; set; } = "";
    public string? SubProjectId { get; set; }

    public string ManifestPath { get; set; } = "";
    public string ManifestType { get; set; } = ""; // "csproj" | "package.json" | "tsconfig.json"

    public List<string> TargetFrameworks { get; set; } = [];
    public string? OutputKind { get; set; }         // "Exe" | "Library" (from <OutputType>)
    public string? LangVersion { get; set; }
    public string? Nullable { get; set; }           // "enable" | "disable" | "warnings" | "annotations"
    public bool ImplicitUsings { get; set; }

    public List<PackageDependency> Packages { get; set; } = [];
    public List<string> ProjectReferences { get; set; } = []; // relative paths to referenced projects
    public Dictionary<string, string> Scripts { get; set; } = []; // package.json scripts

    public DateTime IndexedAt { get; set; }
}

public sealed class PackageDependency
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool IsDev { get; set; }                 // devDependencies (package.json)
}
