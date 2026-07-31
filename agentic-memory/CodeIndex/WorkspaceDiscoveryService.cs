using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticMemory.CodeIndex;

public sealed class WorkspaceDiscoveryService
{
    private readonly ILogger<WorkspaceDiscoveryService> _logger;
    private readonly ExcludedFolderMatcher _excluded;

    public WorkspaceDiscoveryService(
        ILogger<WorkspaceDiscoveryService> logger,
        Configuration.CodeIndexSettings? settings = null)
    {
        _logger = logger;
        _excluded = new ExcludedFolderMatcher(
            (settings ?? new Configuration.CodeIndexSettings()).ExcludePatterns);
    }

    /// <summary>
    /// Derives a stable GUID from the manifest path so re-runs produce the same IDs.
    /// SHA256 of the lowercased, fully-qualified path → first 16 bytes → GUID.
    /// </summary>
    public static string DeriveId(string manifestPath) =>
        new Guid(SHA256.HashData(
            Encoding.UTF8.GetBytes(
                Path.GetFullPath(manifestPath).ToLowerInvariant()))[..16])
        .ToString();

    public async Task<List<SubProjectRecord>> DiscoverAsync(
        string workspaceRoot, CancellationToken ct = default)
    {
        var results = new List<SubProjectRecord>();

        // Pass 1: C# — every .csproj is an independent sub-project
        foreach (var manifest in FindFiles(workspaceRoot, "*.csproj"))
        {
            ct.ThrowIfCancellationRequested();
            var root = Path.GetDirectoryName(manifest)!;
            results.Add(Make(manifest, root, SubProjectType.CSharpProject, "csharp"));
            _logger.LogInformation("Discovered CSharpProject: {Name}",
                Path.GetFileNameWithoutExtension(manifest));
        }

        // Pass 2: Node / TypeScript
        foreach (var manifest in FindFiles(workspaceRoot, "package.json"))
        {
            ct.ThrowIfCancellationRequested();
            var root = Path.GetDirectoryName(manifest)!;
            var (type, lang, name) = await ParsePackageJson(manifest, ct);
            results.Add(Make(manifest, root, type, lang, name));
            _logger.LogInformation("Discovered {Type}: {Name}", type, name);
        }

        if (results.Count == 0)
        {
            _logger.LogWarning("No manifests found at {Root} — creating Unknown fallback", workspaceRoot);
            results.Add(Make(workspaceRoot, workspaceRoot, SubProjectType.Unknown, "unknown"));
        }

        return results;
    }

    /// <summary>
    /// Merges a fresh discovery run into the existing sub-project list.
    /// Existing sub-projects (matched by stable ID) are kept unchanged.
    /// New sub-projects are added; removed ones are returned separately.
    /// </summary>
    public async Task<(List<SubProjectRecord> Merged, List<SubProjectRecord> Removed)> DiscoverAndMergeAsync(
        string workspaceRoot, List<SubProjectRecord> existing, CancellationToken ct = default)
    {
        var candidates = await DiscoverAsync(workspaceRoot, ct);
        var existingById = existing.ToDictionary(s => s.Id);

        var merged = new List<SubProjectRecord>();
        foreach (var candidate in candidates)
        {
            // Keep the existing record if the manifest hasn't changed
            merged.Add(existingById.TryGetValue(candidate.Id, out var kept) ? kept : candidate);
        }

        var candidateIds = candidates.Select(c => c.Id).ToHashSet();
        var removed = existing.Where(s => !candidateIds.Contains(s.Id)).ToList();

        return (merged, removed);
    }

    private static SubProjectRecord Make(
        string manifestPath, string rootPath,
        SubProjectType type, string language, string? nameOverride = null)
    {
        var id = DeriveId(manifestPath);
        var name = nameOverride
            ?? Path.GetFileNameWithoutExtension(
                manifestPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFileName(rootPath)
                    : manifestPath);

        return new SubProjectRecord(
            Id:           id,
            WorkspaceId:  "",
            Name:         name,
            RootPath:     rootPath,
            Type:         type,
            ManifestPath: manifestPath,
            Language:     language,
            Namespace:    $"sub:{id}");
    }

    private IEnumerable<string> FindFiles(string root, string pattern)
    {
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories); }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var f in files)
            if (!_excluded.IsExcluded(f, root))
                yield return f;
    }

    private static readonly string[] TypeScriptIndicators =
        ["typescript", "vite", "@types/react", "ts-node", "next", "@typescript-eslint/"];

    private static async Task<(SubProjectType type, string language, string name)>
        ParsePackageJson(string manifestPath, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var section in new[] { "dependencies", "devDependencies" })
                if (root.TryGetProperty(section, out var d))
                    foreach (var prop in d.EnumerateObject())
                        deps.Add(prop.Name);

            var isTs = TypeScriptIndicators.Any(ind =>
                deps.Any(dep => dep.StartsWith(ind, StringComparison.OrdinalIgnoreCase)));

            return isTs
                ? (SubProjectType.TypeScript, "typescript", name)
                : (SubProjectType.Node, "javascript", name);
        }
        catch
        {
            return (SubProjectType.Node, "javascript",
                Path.GetFileName(Path.GetDirectoryName(manifestPath)) ?? "");
        }
    }
}
