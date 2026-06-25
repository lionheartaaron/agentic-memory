using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace AgenticMemory.CodeIndex.CSharp;

/// <summary>
/// Holds the whole-program Roslyn compilation for one C# project root.
///
/// Per §3.3 (C# variant): all .cs files under the root are enumerated up front so the engine
/// builds one CSharpCompilation spanning the entire tree. Cross-file alias resolution and
/// "find references" only work correctly against a whole-program view — per-file parsing is
/// insufficient by construction.
/// </summary>
internal sealed class ProjectIndex : IDisposable
{
    private readonly string _root;
    private CSharpCompilation? _compilation;
    private readonly ConcurrentDictionary<string, (SyntaxTree Tree, string Version)> _fileCache
        = new(StringComparer.OrdinalIgnoreCase);

    internal string Root => _root;
    internal CSharpCompilation? Compilation => _compilation;

    internal ProjectIndex(string root) => _root = root;

    internal async Task BuildAsync(CancellationToken ct = default)
    {
        var csPaths = Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInExcludedDir(p))
            .ToArray();

        var trees = new SyntaxTree[csPaths.Length];
        for (var i = 0; i < csPaths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src = await File.ReadAllTextAsync(csPaths[i], ct);
            var tree = ParseFile(csPaths[i], src);
            trees[i] = tree;
            _fileCache[csPaths[i]] = (tree, VersionOf(src));
        }

        _compilation = CSharpCompilation.Create(
            assemblyName: Path.GetFileName(_root),
            syntaxTrees: trees,
            references: BuildReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    /// <summary>
    /// Returns the SemanticModel for a file. The model is always built against the whole-program
    /// compilation, not an isolated per-file parse, so cross-file type resolution works.
    /// </summary>
    internal SemanticModel? GetSemanticModel(string filePath)
    {
        if (_compilation is null) return null;
        if (!_fileCache.TryGetValue(filePath, out var entry)) return null;

        // Always use the tree that is part of the compilation (whole-program requirement)
        var compilationTree = _compilation.SyntaxTrees
            .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (compilationTree is null) return null;

        return _compilation.GetSemanticModel(compilationTree, ignoreAccessibility: true);
    }

    /// <summary>
    /// Returns a parsed SyntaxTree, re-using the cached version when the file has not changed.
    /// Updates the compilation incrementally (replaces just the changed tree) to avoid full rebuilds.
    /// </summary>
    internal SyntaxTree GetOrParseSyntaxTree(string filePath, string source)
    {
        var version = VersionOf(source);
        if (_fileCache.TryGetValue(filePath, out var cached) && cached.Version == version)
            return cached.Tree;

        var newTree = ParseFile(filePath, source);
        _fileCache[filePath] = (newTree, version);

        if (_compilation is not null)
        {
            var oldTree = _compilation.SyntaxTrees
                .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            _compilation = oldTree is not null
                ? _compilation.ReplaceSyntaxTree(oldTree, newTree)
                : _compilation.AddSyntaxTrees(newTree);
        }

        return newTree;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SyntaxTree ParseFile(string path, string source) =>
        CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
            path: path);

    /// <summary>
    /// Loads BCL + framework assemblies from the Trusted Platform Assemblies list. This covers all
    /// .NET runtime types. NuGet package assemblies specific to the project under analysis are not
    /// loaded here — domain-pattern detection uses attribute-name heuristics (strings) rather than
    /// requiring the NuGet assemblies to be present, so this level of reference coverage is
    /// sufficient for the §3.3 semantic-resolution requirement.
    /// </summary>
    private static IEnumerable<MetadataReference> BuildReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
        if (tpa is null) yield break;

        foreach (var path in tpa.Split(';'))
        {
            if (!File.Exists(path)) continue;
            MetadataReference? r = null;
            try { r = MetadataReference.CreateFromFile(path); }
            catch { /* skip unloadable */ }
            if (r is not null) yield return r;
        }
    }

    private static string VersionOf(string source)
        => source.Length.ToString("X8") + "-" + ((uint)source.GetHashCode()).ToString("X8");

    private static bool IsInExcludedDir(string path)
    {
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (seg is "obj" or "bin" or ".git" or "node_modules" or ".vs" or ".vscode")
                return true;
        }
        return false;
    }

    public void Dispose() { /* Roslyn compilation is managed memory; nothing to release */ }
}
