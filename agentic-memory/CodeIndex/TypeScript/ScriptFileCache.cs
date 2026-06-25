using System.Collections.Concurrent;

namespace AgenticMemory.CodeIndex.TypeScript;

/// <summary>
/// Per-file version stamps for the TypeScript LanguageServiceHost.
///
/// TypeScript's incremental compilation model calls getScriptVersion() to detect which files
/// have changed since the last analysis pass. This cache tracks the version for each file so
/// only the changed file triggers re-analysis, not a full project rebuild.
/// </summary>
internal sealed class ScriptFileCache
{
    private readonly ConcurrentDictionary<string, FileEntry> _entries
        = new(StringComparer.OrdinalIgnoreCase);

    internal record FileEntry(string Version, string Content, long LastModifiedUtc);

    /// <summary>Returns the version string for the file, computing it on first access.</summary>
    internal string GetVersion(string fileName)
    {
        if (!_entries.TryGetValue(fileName, out var entry))
        {
            entry = LoadEntry(fileName);
            _entries[fileName] = entry;
        }
        return entry.Version;
    }

    /// <summary>Returns the file content, re-reading from disk only when the version has changed.</summary>
    internal string? GetContent(string fileName)
    {
        if (!File.Exists(fileName)) return null;

        var lastModified = File.GetLastWriteTimeUtc(fileName).Ticks;
        if (_entries.TryGetValue(fileName, out var existing) && existing.LastModifiedUtc == lastModified)
            return existing.Content;

        var entry = LoadEntry(fileName);
        _entries[fileName] = entry;
        return entry.Content;
    }

    /// <summary>Invalidates the cached entry for a file (e.g. after an in-memory edit).</summary>
    internal void Invalidate(string fileName) => _entries.TryRemove(fileName, out _);

    /// <summary>Removes entries for files that no longer exist.</summary>
    internal void PruneDeleted()
    {
        foreach (var key in _entries.Keys.Where(k => !File.Exists(k)).ToList())
            _entries.TryRemove(key, out _);
    }

    private static FileEntry LoadEntry(string fileName)
    {
        if (!File.Exists(fileName))
            return new FileEntry("0", string.Empty, 0L);

        var lastModified = File.GetLastWriteTimeUtc(fileName).Ticks;
        var content = File.ReadAllText(fileName);
        var version = ((uint)content.GetHashCode()).ToString("X8");
        return new FileEntry(version, content, lastModified);
    }
}
