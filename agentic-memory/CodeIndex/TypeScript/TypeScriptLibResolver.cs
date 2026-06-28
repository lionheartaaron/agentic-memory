namespace AgenticMemory.CodeIndex.TypeScript;

/// <summary>
/// Locates the TypeScript default lib (node_modules/typescript/lib) for a project — the single source
/// of truth for "can this TS project's types resolve?". The LanguageServiceHost serves the lib path
/// from here so the checker functions; the ingestion/staleness pipeline uses <see cref="HasResolvableTypes"/>
/// to flag (and later auto-correct) files indexed in degraded, type-less mode.
/// </summary>
public static class TypeScriptLibResolver
{
    /// <summary>Full path to the default lib file (walking up from the root), or null if no TypeScript install is present.</summary>
    public static string? FindDefaultLibFile(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return null;
        for (var dir = new DirectoryInfo(projectRoot); dir is not null; dir = dir.Parent)
        {
            var full = Path.Combine(dir.FullName, "node_modules", "typescript", "lib", "lib.es2020.full.d.ts");
            if (File.Exists(full)) return full.Replace('\\', '/');
        }
        return null;
    }

    /// <summary>True when the project (or an ancestor) has a TypeScript install whose lib can be served.</summary>
    public static bool HasResolvableTypes(string projectRoot) => FindDefaultLibFile(projectRoot) is not null;
}
