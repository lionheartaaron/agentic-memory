namespace AgenticMemory.CodeIndex;

/// <summary>
/// Single implementation of the "is this path inside an ignored folder?" check, driven by
/// CodeIndexSettings.ExcludePatterns. Every scan path — staleness scan, live file watcher,
/// workspace discovery, manifest extraction, and the Roslyn / TypeScript whole-program
/// enumerations — must route through this so the configured list is honored everywhere.
/// Indexing never relies on .gitignore.
/// </summary>
public sealed class ExcludedFolderMatcher
{
    private readonly HashSet<string> _folders;

    /// <summary>
    /// Accepts plain folder names ("node_modules") and tolerates glob-decorated entries
    /// ("**/node_modules/**") by reducing them to the folder name.
    /// </summary>
    public ExcludedFolderMatcher(IEnumerable<string> patterns)
    {
        _folders = new HashSet<string>(
            patterns
                .Select(p => p.Replace("**/", "").Replace("/**", "").Replace("**", "").Trim('/', '\\'))
                .Where(p => p.Length > 0),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when any directory segment of <paramref name="path"/> matches an excluded folder.
    /// When <paramref name="root"/> is supplied only segments below the root are checked, so a
    /// workspace that itself lives inside e.g. a "build" directory is not excluded wholesale.
    /// The final (file-name) segment is never matched.
    /// </summary>
    public bool IsExcluded(string path, string? root = null)
    {
        var candidate = path;
        if (root is not null)
        {
            var rel = Path.GetRelativePath(root, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
                candidate = rel;
        }

        var segments = candidate.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 1; i++)
            if (_folders.Contains(segments[i]))
                return true;
        return false;
    }
}
