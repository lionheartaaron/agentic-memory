using System.Text.Json;
using System.Xml.Linq;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Parses project manifests (.csproj / package.json / tsconfig.json) under a root into
/// ProjectManifestRecords. Pure declarative-config parsing — no compiler involvement — so it is
/// methodology-clean (manifests are not compiler output). Best-effort: malformed files are skipped.
/// </summary>
public static class ManifestExtractor
{
    private static readonly EnumerationOptions Opts = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible    = true,
    };

    public static IReadOnlyList<ProjectManifestRecord> Extract(
        string root, string projectId, string? subProjectId, DateTime nowUtc,
        IReadOnlyList<string>? excludePatterns = null)
    {
        var results = new List<ProjectManifestRecord>();
        if (!Directory.Exists(root)) return results;

        // Callers without settings access get the configured defaults
        var excluded = new ExcludedFolderMatcher(
            excludePatterns ?? new Configuration.CodeIndexSettings().ExcludePatterns);

        foreach (var path in EnumerateManifests(root, excluded))
        {
            var rec = path switch
            {
                _ when path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) => ParseCsproj(path),
                _ when path.EndsWith("package.json", StringComparison.OrdinalIgnoreCase) => ParsePackageJson(path),
                _ when path.EndsWith("tsconfig.json", StringComparison.OrdinalIgnoreCase) => ParseTsConfig(path),
                _ => null,
            };
            if (rec is null) continue;

            rec.Id           = $"{projectId}::{path.ToLowerInvariant()}";
            rec.ProjectId    = projectId;
            rec.SubProjectId = subProjectId;
            rec.ManifestPath = path;
            rec.IndexedAt    = nowUtc;
            results.Add(rec);
        }
        return results;
    }

    private static IEnumerable<string> EnumerateManifests(string root, ExcludedFolderMatcher excluded)
    {
        IEnumerable<string> all;
        try
        {
            all = Directory.EnumerateFiles(root, "*.csproj", Opts)
                .Concat(Directory.EnumerateFiles(root, "package.json", Opts))
                .Concat(Directory.EnumerateFiles(root, "tsconfig.json", Opts));
        }
        catch { yield break; }

        int count = 0;
        foreach (var f in all)
        {
            if (excluded.IsExcluded(f, root)) continue;
            if (count++ >= 100) yield break;
            yield return f;
        }
    }

    private static ProjectManifestRecord? ParseCsproj(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            var rec = new ProjectManifestRecord { ManifestType = "csproj" };

            string? Prop(string name) => doc.Descendants(name)
                .Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);

            var tfm  = Prop("TargetFramework");
            var tfms = Prop("TargetFrameworks");
            if (tfm is not null)  rec.TargetFrameworks.Add(tfm);
            if (tfms is not null) rec.TargetFrameworks.AddRange(tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            rec.OutputKind     = Prop("OutputType");
            rec.LangVersion    = Prop("LangVersion");
            rec.Nullable       = Prop("Nullable");
            rec.ImplicitUsings = string.Equals(Prop("ImplicitUsings"), "enable", StringComparison.OrdinalIgnoreCase);

            foreach (var pr in doc.Descendants("PackageReference"))
            {
                var name = pr.Attribute("Include")?.Value;
                if (string.IsNullOrEmpty(name)) continue;
                var version = pr.Attribute("Version")?.Value
                              ?? pr.Element("Version")?.Value ?? "";
                rec.Packages.Add(new PackageDependency { Name = name, Version = version });
            }

            foreach (var pr in doc.Descendants("ProjectReference"))
            {
                var inc = pr.Attribute("Include")?.Value;
                if (!string.IsNullOrEmpty(inc)) rec.ProjectReferences.Add(inc);
            }

            return rec;
        }
        catch { return null; }
    }

    private static ProjectManifestRecord? ParsePackageJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var rec  = new ProjectManifestRecord { ManifestType = "package.json" };

            void AddDeps(string prop, bool isDev)
            {
                if (root.TryGetProperty(prop, out var deps) && deps.ValueKind == JsonValueKind.Object)
                    foreach (var d in deps.EnumerateObject())
                        rec.Packages.Add(new PackageDependency { Name = d.Name, Version = d.Value.GetString() ?? "", IsDev = isDev });
            }
            AddDeps("dependencies", false);
            AddDeps("devDependencies", true);

            if (root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                foreach (var s in scripts.EnumerateObject())
                    rec.Scripts[s.Name] = s.Value.GetString() ?? "";

            return rec;
        }
        catch { return null; }
    }

    private static ProjectManifestRecord? ParseTsConfig(string path)
    {
        try
        {
            // tsconfig.json permits comments/trailing commas; tolerate them.
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            var rec = new ProjectManifestRecord { ManifestType = "tsconfig.json" };
            if (doc.RootElement.TryGetProperty("compilerOptions", out var co) && co.ValueKind == JsonValueKind.Object)
            {
                if (co.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String)
                    rec.TargetFrameworks.Add(t.GetString()!);
                if (co.TryGetProperty("strict", out var st) && st.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    rec.Nullable = st.GetBoolean() ? "strict" : "loose";
            }
            return rec;
        }
        catch { return null; }
    }
}
