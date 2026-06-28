using AgenticMemory.CodeIndex.TypeScript;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Regression guard against the real react-dashboard: default-exported React components
/// (`export default function App` imported as `import App from './App'`) must NOT be flagged orphan.
///
/// This bug only surfaces in "degraded checker" mode — a project whose node_modules types aren't in
/// the program, so the type checker resolves nothing cross-file and ALL references come from the
/// syntactic import fallback. That fallback originally handled only NAMED imports, so every page/
/// component (default exports) showed 0 references. The small in-memory seed can't reproduce it
/// (its checker works), so this points at the actual dashboard. Skips if the frontend isn't present.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypeScriptReactAppTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Default_exported_components_are_not_orphan()
    {
        Assert.SkipUnless(fixture.TypeScriptAvailable, "TypeScript/V8 provider not available.");
        var ct = TestContext.Current.CancellationToken;

        var tsPath = FindRepoFile(Path.Combine("agentic-memory", "Models", "TypeScript", "typescript.js"));
        var appTsx = FindRepoFile(Path.Combine("react-dashboard", "src", "App.tsx"));
        Assert.SkipUnless(tsPath is not null && appTsx is not null, "real react-dashboard not present.");

        var webRoot = Path.GetDirectoryName(Path.GetDirectoryName(appTsx!))!; // react-dashboard/
        var provider = new TypeScriptClearScriptProvider(tsPath!, NullLogger<TypeScriptClearScriptProvider>.Instance);
        try
        {
            await provider.RegisterProjectAsync(webRoot, ct);

            // App is default-imported by main.tsx; Overview/Layout/Browse by App.tsx.
            foreach (var name in new[] { "App", "Overview", "Layout", "Browse" })
            {
                var refs = await provider.FindReferencesAsync(appTsx!, name, ct);
                Assert.True(refs.Count > 0,
                    $"default-exported component '{name}' resolved to 0 references — would be a false orphan");
            }
        }
        finally { await provider.DisposeAsync(); }
    }

    [Fact]
    public async Task Cross_file_usage_roles_resolve_when_types_are_available()
    {
        Assert.SkipUnless(fixture.TypeScriptAvailable, "TypeScript/V8 provider not available.");
        var ct = TestContext.Current.CancellationToken;

        var tsPath = FindRepoFile(Path.Combine("agentic-memory", "Models", "TypeScript", "typescript.js"));
        var appTsx = FindRepoFile(Path.Combine("react-dashboard", "src", "App.tsx"));
        // Type resolution needs the project's own TypeScript lib (node_modules) installed.
        var libProbe = FindRepoFile(Path.Combine("react-dashboard", "node_modules", "typescript", "lib", "lib.es2020.full.d.ts"));
        Assert.SkipUnless(tsPath is not null && appTsx is not null && libProbe is not null,
            "real react-dashboard / TypeScript lib not present.");

        var webRoot = Path.GetDirectoryName(Path.GetDirectoryName(appTsx!))!;
        var provider = new TypeScriptClearScriptProvider(tsPath!, NullLogger<TypeScriptClearScriptProvider>.Instance);
        try
        {
            await provider.RegisterProjectAsync(webRoot, ct);

            // formatBytes (utils.ts) is CALLED in Overview.tsx — with types resolving, the call site
            // (not just the import) must be captured, proving the checker resolves cross-file symbols.
            var refs = await provider.FindReferencesAsync(appTsx!, "formatBytes", ct);
            Assert.Contains(refs, r => r.Role == "call");

            // <Overview /> in App.tsx is a JSX usage beyond the import.
            var overview = await provider.FindReferencesAsync(appTsx!, "Overview", ct);
            Assert.True(overview.Count >= 2);
        }
        finally { await provider.DisposeAsync(); }
    }

    private static string? FindRepoFile(string rel)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var c = Path.Combine(dir.FullName, rel);
            if (File.Exists(c)) return c;
            dir = dir.Parent;
        }
        return null;
    }
}
