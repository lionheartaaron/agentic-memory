using System.Collections.Concurrent;
using System.Text;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;

namespace AgenticMemory.CodeIndex.TypeScript;

/// <summary>
/// ICodeIntelligenceProvider implementation for TypeScript / React Native / Expo — §3 of
/// code-understanding-methodology.md.
///
/// COMPILER: Microsoft.ClearScript.V8 hosts a real V8 instance inside the C# process. The actual
/// typescript.js bundle — the same isomorphic file that runs the TypeScript Playground in a browser
/// — is loaded into that V8 instance once per ProjectIndex. This is the literal TypeScript compiler
/// running in-process, not a reimplementation.
///
/// HOST BRIDGE: LanguageServiceHost is exposed into V8 via AddHostObject. It implements the
/// ts.LanguageServiceHost contract required by ts.createLanguageService: all file I/O runs in
/// native C#; only compilation and type analysis happen inside V8.
///
/// WHOLE-PROGRAM VIEW (§3.3): LanguageServiceHost.GetScriptFileNames() enumerates every .ts/.tsx
/// file under the project root up front, so ts.createLanguageService builds one ts.Program
/// spanning the whole tree. This is what makes barrel-file re-export resolution work.
///
/// JSX BRANCHING (§3.3): Each .tsx file is loaded with ScriptKind.TSX specifically via the
/// scriptKind property on ScriptSnapshot. Using one fixed ScriptKind project-wide silently
/// produces empty symbol trees for JSX files.
///
/// SETUP REQUIRED: This provider requires typescript.js to be present at the path configured in
/// CodeIndexSettings.TypeScriptCompilerPath. Obtain it from the TypeScript npm package:
///     npx tsc --version   # confirm installed
///     node -e "require('path').resolve(require.resolve('typescript'), '../typescript.js')"
/// or copy node_modules/typescript/lib/typescript.js from any project that already uses TypeScript.
///
/// DOMAIN PATTERNS (§3.4): react-page, react-hook, react-component, api-client, ts-store are
/// detected on top of the compiler's symbol data. These are hand-rolled by necessity — no compiler
/// API encodes "this is a Zustand store" or "this is a TanStack Query hook".
/// </summary>
public sealed class TypeScriptClearScriptProvider : ICodeIntelligenceProvider
{
    private readonly string? _typescriptJsPath;
    private readonly ConcurrentDictionary<string, ProjectIndex> _projects
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TypeScriptClearScriptProvider> _logger;

    public TypeScriptClearScriptProvider(
        string? typescriptJsPath,
        ILogger<TypeScriptClearScriptProvider> logger)
    {
        _typescriptJsPath = typescriptJsPath;
        _logger = logger;
    }

    public string ProviderType => "typescript-react-native-expo";

    public TypeCapabilities Capabilities => new(
        CompilerApi: "TypeScript LanguageService via Microsoft.ClearScript.V8",
        DomainPatternFamilies: ["react-page", "react-hook", "react-component", "api-client", "ts-store", "tanstack-query"]);

    public bool CanHandle(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tsx", StringComparison.OrdinalIgnoreCase);
    }

    // ── Project registration ──────────────────────────────────────────────────

    public async Task RegisterProjectAsync(string projectRoot, CancellationToken ct = default)
    {
        if (!Directory.Exists(projectRoot)) return;

        if (string.IsNullOrEmpty(_typescriptJsPath) || !File.Exists(_typescriptJsPath))
        {
            _logger.LogWarning(
                "TypeScript provider disabled: typescript.js not found at '{Path}'. " +
                "Set CodeIndex.TypeScriptCompilerPath in appsettings.json to the path of " +
                "node_modules/typescript/lib/typescript.js from any TypeScript project.",
                _typescriptJsPath ?? "(not configured)");
            return;
        }

        var index = new ProjectIndex(projectRoot, _typescriptJsPath, _logger);
        try
        {
            await index.InitialiseAsync(ct);
            _projects[projectRoot] = index;
            _logger.LogInformation("TypeScript Roslyn-equivalent index built for {Root}", projectRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TypeScript project registration failed for {Root}", projectRoot);
            await index.DisposeAsync();
        }
    }

    // ── Context extraction ────────────────────────────────────────────────────

    public async Task<string> ExtractContextAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var index = FindIndex(filePath);
        if (index is null)
        {
            _logger.LogDebug("No TypeScript project index for {File}; returning empty context", filePath);
            return string.Empty;
        }

        return await Task.Run(() => index.ExtractContext(filePath), ct);
    }

    // ── Symbol / reference / diagnostic queries ───────────────────────────────

    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string filePath, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index is null) return [];
        return await Task.Run(() => index.GetSymbols(filePath), ct);
    }

    public async Task<IReadOnlyList<ReferenceInfo>> FindReferencesAsync(
        string filePath, string symbolName, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index is null) return [];
        return await Task.Run(() => index.FindReferences(filePath, symbolName), ct);
    }

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        string filePath, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index is null) return [];
        return await Task.Run(() => index.GetDiagnostics(filePath), ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProjectIndex? FindIndex(string filePath)
    {
        foreach (var (root, idx) in _projects)
            if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return idx;
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var idx in _projects.Values)
            await idx.DisposeAsync();
        _projects.Clear();
    }

    // ── Nested: one V8 engine + LanguageService per project ──────────────────

    private sealed class ProjectIndex : IAsyncDisposable
    {
        private static readonly System.Text.Json.JsonSerializerOptions _caseInsensitive =
            new() { PropertyNameCaseInsensitive = true };

        private readonly string _projectRoot;
        private readonly string _typescriptJsPath;
        private readonly ILogger _logger;
        private V8ScriptEngine? _engine;
        private ScriptFileCache _cache = new();
        private LanguageServiceHost? _host;

        internal ProjectIndex(string projectRoot, string tsPath, ILogger logger)
        {
            _projectRoot = projectRoot;
            _typescriptJsPath = tsPath;
            _logger = logger;
        }

        internal async Task InitialiseAsync(CancellationToken ct = default)
        {
            // V8 engine setup is CPU-bound; run off the thread pool
            await Task.Run(() =>
            {
                _engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableDebugging | V8ScriptEngineFlags.DisableGlobalMembers);

                // Expose C# host object to JavaScript. All file I/O routes here — V8 never touches
                // the file system directly.
                _host = new LanguageServiceHost(_projectRoot, _cache);
                _engine.AddHostObject("nativeHost", HostItemFlags.None, _host);
                _engine.AddHostType("Console", typeof(Console));

                // Load the TypeScript compiler bundle. This is the literal TypeScript compiler —
                // the same file used by the TypeScript Playground in a browser.
                var tsSource = File.ReadAllText(_typescriptJsPath);
                _engine.Execute("typescript", tsSource);

                // Create the language service, wiring the native host as the LanguageServiceHost.
                // The bridge script adapts C# method naming conventions (PascalCase) to the
                // TypeScript camelCase contract expected by ts.createLanguageService.
                _engine.Execute("bridge", LoadBridgeScript());
            }, ct);
        }

        internal string ExtractContext(string filePath)
        {
            if (_engine is null) return string.Empty;
            try
            {
                // Invalidate the file so TS picks up the current disk content
                _host?.InvalidateFile(filePath);

                // TypeScript normalizes all paths to forward slashes internally; pass the same
                // format or getSourceFile() returns undefined even when the file was registered.
                var normalized = filePath.Replace('\\', '/');
                var result = _engine.Evaluate($@"
                    (function() {{
                        var info = getFileInfo(""{normalized}"");
                        if (!info) {{ Console.WriteLine('TS getFileInfo returned null for: {normalized}'); }}
                        return info ? JSON.stringify(info) : null;
                    }})()");

                if (result is null or Undefined)
                {
                    _logger.LogWarning("TypeScript getFileInfo returned null for {File} — file may not be in the program", filePath);
                    return string.Empty;
                }
                return FormatContextFromJson(result.ToString() ?? "", filePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TypeScript context extraction failed for {File}", filePath);
                return string.Empty;
            }
        }

        internal IReadOnlyList<SymbolInfo> GetSymbols(string filePath)
        {
            if (_engine is null) return [];
            try
            {
                var normalized = filePath.Replace('\\', '/');
                var json = _engine.Evaluate($@"JSON.stringify(getSymbols(""{normalized}""))");
                if (json is null or Undefined) return [];
                return System.Text.Json.JsonSerializer.Deserialize<List<SymbolInfo>>(json.ToString() ?? "[]", _caseInsensitive) ?? [];
            }
            catch { return []; }
        }

        internal IReadOnlyList<ReferenceInfo> FindReferences(string filePath, string symbolName)
        {
            if (_engine is null) return [];
            try
            {
                var normalized = filePath.Replace('\\', '/');
                var escapedName = symbolName.Replace("\"", "\\\"");
                var json = _engine.Evaluate($@"JSON.stringify(findReferences(""{normalized}"", ""{escapedName}""))");
                if (json is null or Undefined) return [];
                return System.Text.Json.JsonSerializer.Deserialize<List<ReferenceInfo>>(json.ToString() ?? "[]", _caseInsensitive) ?? [];
            }
            catch { return []; }
        }

        internal IReadOnlyList<DiagnosticInfo> GetDiagnostics(string filePath)
        {
            if (_engine is null) return [];
            try
            {
                var normalized = filePath.Replace('\\', '/');
                var json = _engine.Evaluate($@"JSON.stringify(getDiagnostics(""{normalized}""))");
                if (json is null or Undefined) return [];
                return System.Text.Json.JsonSerializer.Deserialize<List<DiagnosticInfo>>(json.ToString() ?? "[]", _caseInsensitive) ?? [];
            }
            catch { return []; }
        }

        /// <summary>
        /// The bridge script adapts the C# LanguageServiceHost (PascalCase) to the TypeScript
        /// ts.LanguageServiceHost interface (camelCase), then creates the LanguageService and
        /// exposes query functions to the surrounding C# evaluation calls.
        /// </summary>
        private static string? _bridgeScript;
        private static string LoadBridgeScript()
        {
            if (_bridgeScript is not null) return _bridgeScript;
            var path = Path.Combine(AppContext.BaseDirectory, "CodeIndex\\TypeScript\\bridge.js");
            return _bridgeScript = File.ReadAllText(path);
        }
        /// <summary>
        /// Converts the JSON returned from the V8 bridge into an LLM-ready context string.
        /// This is the §3.4 domain-pattern layer: the data comes from real compiler symbol/type
        /// queries; the formatting applies hand-rolled convention detection on top.
        /// </summary>
        private static string FormatContextFromJson(string json, string filePath)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var sb = new StringBuilder();

                var fileClass = DetectFileClass(root, filePath);
                sb.Append("// FILE: ").Append(Path.GetFileName(filePath)).Append("  [").Append(fileClass).AppendLine("]");

                var hintList = root.TryGetProperty("domainHints", out var hintsEl)
                    ? hintsEl.EnumerateArray().ToList()
                    : [];

                static string? Str(System.Text.Json.JsonElement e, string key) =>
                    e.TryGetProperty(key, out var v) && v.ValueKind != System.Text.Json.JsonValueKind.Null
                        ? v.GetString() : null;

                // Route params
                var routeParams = hintList
                    .Where(h => Str(h, "kind") == "route-param")
                    .Select(h => Str(h, "name")).Where(n => n != null).Distinct().ToList();
                foreach (var rp in routeParams)
                    sb.Append("// ROUTE_PARAM: ").AppendLine(rp);

                // Collect navigate paths that already appear in mutation side-effects
                var mutationNavPaths = hintList
                    .Where(h => Str(h, "kind") == "mutation")
                    .Select(h => Str(h, "navigatesTo")).Where(p => p != null)
                    .ToHashSet(StringComparer.Ordinal);

                // Top-level navigation targets: navigate() calls and Link to= not already in mutations
                var navPaths = hintList
                    .Where(h => { var k = Str(h, "kind"); return k == "navigate-to" || k == "link-to"; })
                    .Select(h => Str(h, "path"))
                    .Where(p => p != null && !mutationNavPaths.Contains(p!))
                    .Distinct().ToList();
                foreach (var p in navPaths)
                    sb.Append("// NAVIGATES_TO: ").AppendLine(p);

                if (routeParams.Count > 0 || navPaths.Count > 0)
                    sb.AppendLine("//");

                // Data section: FETCH lines from useQuery
                var queries = hintList.Where(h => Str(h, "kind") == "query").ToList();
                var mutations = hintList.Where(h => Str(h, "kind") == "mutation").ToList();

                foreach (var q in queries)
                {
                    var fn = Str(q, "fn");
                    var key = Str(q, "key");
                    sb.Append("// FETCH:  ");
                    if (fn != null) sb.Append(fn);
                    if (key != null) { sb.Append("   queryKey: ").Append(key); }
                    sb.AppendLine();
                }

                foreach (var mut in mutations)
                {
                    var fn = Str(mut, "fn");
                    var verb = InferMutationVerb(fn);
                    sb.Append("// ").Append(verb).Append(": ");
                    if (fn != null) sb.Append(fn);

                    if (mut.TryGetProperty("invalidates", out var invEl) &&
                        invEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        var invList = invEl.EnumerateArray()
                            .Select(e => e.GetString()).Where(s => s != null).ToList();
                        if (invList.Count > 0)
                            sb.Append("   invalidates: ").Append(string.Join(", ", invList));
                    }

                    var navTo = Str(mut, "navigatesTo");
                    if (navTo != null) sb.Append("  → navigate ").Append(navTo);
                    sb.AppendLine();
                }

                // Misc structural hints
                bool hasStructural = false;
                foreach (var hint in hintList)
                {
                    switch (Str(hint, "kind"))
                    {
                        case "endpoint":
                            var method = Str(hint, "method") ?? "GET";
                            var url = Str(hint, "url") ?? "";
                            sb.Append("// endpoint: ").Append(method).Append(' ').AppendLine(url);
                            hasStructural = true; break;
                        case "abortable":
                            sb.AppendLine("// abortable"); hasStructural = true; break;
                        case "sse-source":
                            sb.AppendLine("// sse-source"); hasStructural = true; break;
                        case "streams-sse":
                            sb.AppendLine("// streams-sse"); hasStructural = true; break;
                    }
                }

                // State
                var stateHints = hintList.Where(h => Str(h, "kind") == "state").ToList();
                if (stateHints.Count > 0)
                {
                    sb.AppendLine("//");
                    sb.AppendLine("// STATE:");
                    foreach (var sh in stateHints)
                    {
                        var name = Str(sh, "name");
                        var type = Str(sh, "type") ?? "unknown";
                        if (name == null && type == "unknown") continue;
                        sb.Append("//   ");
                        if (name != null) sb.Append(name).Append(": ").Append(type);
                        else sb.Append(type);
                        sb.AppendLine();
                    }
                }

                // Domain types (type-only imports)
                if (root.TryGetProperty("typeImports", out var typeImports))
                {
                    var types = typeImports.EnumerateArray()
                        .Select(e => e.GetString()).Where(s => s != null).Distinct().ToList();
                    if (types.Count > 0)
                    {
                        sb.AppendLine("//");
                        sb.Append("// DOMAIN TYPES: ").AppendLine(string.Join(", ", types));
                    }
                }

                // Utility imports from local modules (skip api modules if query/mutation data is present)
                if (root.TryGetProperty("localImports", out var localImports))
                {
                    var allUtils = new List<string>();
                    foreach (var prop in localImports.EnumerateObject())
                    {
                        // api module calls are already surfaced via FETCH/UPDATE/DELETE lines
                        if (prop.Name.Contains("api", StringComparison.OrdinalIgnoreCase) &&
                            (queries.Count > 0 || mutations.Count > 0))
                            continue;
                        foreach (var el in prop.Value.EnumerateArray())
                        {
                            var n = el.GetString();
                            if (n != null) allUtils.Add(n);
                        }
                    }
                    if (allUtils.Count > 0)
                        sb.Append("// UTILS: ").AppendLine(string.Join(", ", allUtils.Distinct()));
                }

                // Top-level symbols
                if (root.TryGetProperty("symbols", out var symbols))
                {
                    sb.AppendLine();
                    foreach (var sym in symbols.EnumerateArray())
                    {
                        var name = sym.GetProperty("name").GetString();
                        var type = sym.TryGetProperty("type", out var t) ? t.GetString() : null;
                        var kind = sym.GetProperty("kind").GetString();
                        sb.Append("// ").Append(name);
                        if (type is not null) sb.Append(": ").Append(type);
                        if (kind is not null) sb.Append("  [").Append(kind).Append(']');
                        sb.AppendLine();
                    }
                }

                return sb.ToString().TrimEnd();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string InferMutationVerb(string? fn)
        {
            if (fn == null) return "MUTATE";
            var lower = fn.ToLowerInvariant();
            if (lower.Contains(".get") || lower.Contains(".fetch") || lower.Contains(".load") ||
                lower.Contains(".find") || lower.Contains(".read")) return "FETCH";
            if (lower.Contains(".create") || lower.Contains(".add") || lower.Contains(".post") ||
                lower.Contains(".insert")) return "CREATE";
            if (lower.Contains(".update") || lower.Contains(".put") || lower.Contains(".patch") ||
                lower.Contains(".save") || lower.Contains(".edit")) return "UPDATE";
            if (lower.Contains(".delete") || lower.Contains(".remove") || lower.Contains(".destroy")) return "DELETE";
            return "MUTATE";
        }

        private static string DetectFileClass(System.Text.Json.JsonElement root, string filePath)
        {
            bool isTsx = filePath.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase);
            bool hasUsePrefix = false;

            if (root.TryGetProperty("symbols", out var symbols))
            {
                foreach (var sym in symbols.EnumerateArray())
                {
                    var name = sym.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.Length > 3 && name.StartsWith("use", StringComparison.Ordinal) && char.IsUpper(name[3]))
                        hasUsePrefix = true;
                }
            }

            if (isTsx)
            {
                // Pages live in a pages/ directory or have router hints (useParams / navigate calls)
                bool isInPagesDir = filePath.Contains("/pages/", StringComparison.OrdinalIgnoreCase) ||
                                    filePath.Contains(@"\pages\", StringComparison.OrdinalIgnoreCase);

                bool hasRouterHints = root.TryGetProperty("domainHints", out var routerDh) &&
                    routerDh.EnumerateArray().Any(h =>
                    {
                        if (!h.TryGetProperty("kind", out var k)) return false;
                        var kind = k.GetString();
                        return kind == "route-param" || kind == "navigate-to";
                    });

                if (isInPagesDir || hasRouterHints) return "react-page";
                return hasUsePrefix ? "react-hook" : "react-component";
            }

            // api-client: non-TSX file with multiple fetch endpoint hints (e.g. api.ts)
            if (root.TryGetProperty("domainHints", out var dh))
            {
                var endpointCount = dh.EnumerateArray()
                    .Count(h => h.TryGetProperty("kind", out var k) && k.GetString() == "endpoint");
                if (endpointCount >= 2) return "api-client";
            }

            return "ts-generic";
        }

        public async ValueTask DisposeAsync()
        {
            await Task.Run(() =>
            {
                _engine?.Dispose();
                _engine = null;
            });
        }
    }
}
