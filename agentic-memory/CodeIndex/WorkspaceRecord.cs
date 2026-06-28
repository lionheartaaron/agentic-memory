namespace AgenticMemory.CodeIndex;

public enum SubProjectType
{
    CSharpProject,
    TypeScript,
    Node,
    Python,
    Unknown
}

public sealed record WorkspaceRecord(
    string Id,
    string Name,
    string RootPath,
    string CreatedAt,
    List<SubProjectRecord> SubProjects);

public sealed record SubProjectRecord(
    string Id,
    string WorkspaceId,
    string Name,
    string RootPath,
    SubProjectType Type,
    string ManifestPath,
    string Language,
    string Namespace);
