namespace AgenticMemory.CodeIndex.TypeScript;

/// <summary>
/// C# implementation of the TypeScript LanguageServiceHost interface, exposed into V8 via
/// AddHostObject so the TypeScript compiler (running inside V8) can resolve files and versions.
///
/// This is the "host bridge" described in §3.2 of code-understanding-methodology.md. All file I/O
/// happens here in native C#; only compilation and type analysis happen inside V8.
///
/// VISIBILITY: This class MUST be public. ClearScript's host item binder checks Type.IsVisible
/// before wrapping members as callable JS functions. An internal class has IsVisible=false, so
/// no methods are exposed — nativeHost.GetCurrentDirectory() becomes "not a function" in V8.
///
/// NAMING: ClearScript exposes .NET members using their original PascalCase names. The bridge
/// script calls nativeHost.GetCurrentDirectory(), nativeHost.FileExists(), etc. (PascalCase).
///
/// Per §3.3: GetScriptFileNames MUST return every .ts/.tsx file under the source root up front.
/// Returning files lazily (or per-file-on-demand) breaks barrel-file re-export resolution and
/// cross-file alias chains.
/// </summary>
public sealed class LanguageServiceHost
{
    private readonly string _projectRoot;
    private readonly ScriptFileCache _cache;
    private string[]? _fileList;

    internal LanguageServiceHost(string projectRoot, ScriptFileCache cache)
    {
        _projectRoot = projectRoot;
        _cache = cache;
    }

    // ── ts.LanguageServiceHost methods (called via bridge from TypeScript inside V8) ─────

    public object GetCompilationSettings()
    {
        return new
        {
            target = 2,         // ts.ScriptTarget.ES2015
            module = 99,        // ts.ModuleKind.ESNext
            jsx = 4,            // ts.JsxEmit.ReactJSX
            strict = false,     // avoid false-positive errors in projects without full typings
            noEmit = true,
            allowJs = true,
            allowSyntheticDefaultImports = true,
            esModuleInterop = true,
            resolveJsonModule = true,
            moduleResolution = 99  // ts.ModuleResolutionKind.Bundler
        };
    }

    /// <summary>
    /// Returns ALL .ts/.tsx files in the project root up front. Per §3.3: the full file list must
    /// be enumerated here so TypeScript builds one ts.Program spanning the whole tree. Returning
    /// files on demand breaks cross-file type resolution.
    /// </summary>
    public string[] GetScriptFileNames()
    {
        _fileList ??= Directory
            .EnumerateFiles(_projectRoot, "*.ts", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(_projectRoot, "*.tsx", SearchOption.AllDirectories))
            .Where(f => !IsInExcludedDir(f))
            .Select(f => f.Replace('\\', '/'))
            .ToArray();
        return _fileList;
    }

    public string GetScriptVersion(string fileName)
        => _cache.GetVersion(fileName);

    public object? GetScriptSnapshot(string fileName)
    {
        var content = _cache.GetContent(fileName);
        if (content is null) return null;
        return new ScriptSnapshot(content);
    }

    public string GetCurrentDirectory() => _projectRoot.Replace('\\', '/');

    public string GetDefaultLibFileName(object options) => "lib.d.ts";

    public bool FileExists(string path) => File.Exists(path) || Directory.Exists(path);

    public string? ReadFile(string path, string? encoding = null)
    {
        if (!File.Exists(path)) return null;
        return File.ReadAllText(path);
    }

    public string[] ReadDirectory(string path, object? extensions, object? exclude, object? include, int depth = 100)
    {
        if (!Directory.Exists(path)) return [];
        return Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => !IsInExcludedDir(f))
            .Take(2000)
            .ToArray();
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public string[] GetDirectories(string path)
    {
        if (!Directory.Exists(path)) return [];
        return Directory.GetDirectories(path);
    }

    // Returns 4 (TSX) or 3 (TS) — must be non-static so ClearScript exposes it via the instance wrapper
    public int GetScriptKind(string fileName)
        => fileName.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase) ? 4 : 3;

    // ── Helpers ───────────────────────────────────────────────────────────────

    internal void InvalidateFile(string filePath)
    {
        _cache.Invalidate(filePath.Replace('\\', '/'));
        _fileList = null;
    }

    private static bool IsInExcludedDir(string path)
    {
        foreach (var seg in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (seg is "node_modules" or "obj" or "bin" or ".git" or ".expo" or "dist" or "build" or ".turbo")
                return true;
        }
        return false;
    }
}

/// <summary>
/// Thin wrapper that exposes file content to TypeScript's IScriptSnapshot contract.
/// Must be public — same Type.IsVisible requirement as LanguageServiceHost.
/// Bridge calls snap.GetText(s,e) and snap.GetLength() (PascalCase).
/// </summary>
public sealed class ScriptSnapshot
{
    private readonly string _content;

    public ScriptSnapshot(string content)
    {
        _content = content;
    }

    public string GetText(int start, int end)
        => _content[start..Math.Min(end, _content.Length)];

    public int GetLength() => _content.Length;
}
