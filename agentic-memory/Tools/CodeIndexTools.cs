using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.CodeIndex;
using ModelContextProtocol.Server;

namespace AgenticMemory.Tools;

/// <summary>
/// The agentic-memory code-intelligence tool surface for MCP clients.
///
/// Five tools, ordered by how an agent should reach for them (see <c>core-tools-mcp.md</c>):
///   1. get_subproject_context  — orient: what sub-projects exist, their entry points and manifests.
///   2. get_file_context        — a file's API surface (symbols, imports/exports, deps) without its body.
///   3. get_symbol_context      — a symbol's definition, implementations, callers and references — replaces grep.
///   4. get_symbol_sourcecode   — the exact source of one symbol — replaces a cold read_file.
///   5. search_code             — semantic/keyword file search when you don't yet know the file or symbol.
///
/// All tools operate on the agent's CURRENT workspace: the active project if one is activated, otherwise
/// the sole registered workspace. The compiler-resolved index makes every answer precise and scoped, so
/// an agent spends a few hundred tokens here instead of thousands grepping and cold-reading files.
/// </summary>
[McpServerToolType]
public class CodeIndexTools
{
    private readonly IKeyValueStore _kv;
    private readonly ICodeIndexRepository _repo;
    private readonly IEmbeddingService _embedding;
    private readonly ActiveProjectService _activeProject;

    public CodeIndexTools(
        IKeyValueStore kv,
        ICodeIndexRepository repo,
        IEmbeddingService embedding,
        ActiveProjectService activeProject)
    {
        _kv            = kv;
        _repo          = repo;
        _embedding     = embedding;
        _activeProject = activeProject;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    // ── 1. get_subproject_context ─────────────────────────────────────────────

    [McpServerTool(Name = "get_subproject_context")]
    [Description(
        "CALL THIS FIRST at the start of a session. Returns every sub-project in the current " +
        "workspace — name, path, language, entry point, a short description, and its manifest " +
        "(framework + key dependencies). Use it to learn which sub-project owns the code you need; " +
        "the 'subproject' name it returns is the scope argument for the other tools.")]
    public async Task<string> GetSubprojectContext(CancellationToken cancellationToken = default)
    {
        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;

        if (ws.SubProjects.Count == 0)
            return $"Workspace '{ws.Name}' has no sub-projects discovered yet. " +
                   "Activate/re-discover it in the dashboard to populate the index.";

        var manifests = await _repo.GetProjectManifestsAsync(ws.Id, cancellationToken);

        var result = new List<SubprojectContextDto>();
        foreach (var sp in ws.SubProjects)
        {
            var files = await _repo.GetBySubProjectAsync(sp.Id, cancellationToken);
            var manifest = manifests.FirstOrDefault(m =>
                PathEq(m.ManifestPath, sp.ManifestPath) || IsUnder(m.ManifestPath, sp.RootPath));

            result.Add(new SubprojectContextDto(
                Name:        sp.Name,
                Path:        RelToWorkspace(ws, sp.RootPath),
                Language:    sp.Language,
                EntryPoint:  FindEntryPoint(files, ws, sp),
                Description: DescribeSubProject(sp, files),
                Manifest:    manifest is null ? null : new ManifestSummaryDto(
                    Framework:       ManifestFramework(manifest),
                    KeyDependencies: manifest.Packages.Where(p => !p.IsDev).Select(p => p.Name).Take(10).ToList(),
                    ManifestFile:    RelToWorkspace(ws, manifest.ManifestPath))));
        }

        return Json(result);
    }

    // ── 2. get_file_context ───────────────────────────────────────────────────

    [McpServerTool(Name = "get_file_context")]
    [Description(
        "CALL THIS BEFORE read_file on any file. Returns the file's compiler-resolved API surface — " +
        "symbols with signatures, imports, exported declarations, the files it depends on, line count, " +
        "and an AI summary — without the method bodies. In most cases this replaces reading the whole " +
        "file plus the files around it. Pass 'subproject' (from get_subproject_context) to scope the " +
        "lookup; omit it to search the whole workspace. 'path' may be a full or partial relative path " +
        "or just a file name.")]
    public async Task<string> GetFileContext(
        [Description("File to describe — a relative path (e.g. 'src/api.ts') or a bare file name.")]
        string path,
        [Description("Sub-project name to scope the lookup (recommended). Omit to search the whole workspace.")]
        string? subproject = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Provide a 'path' (relative path or file name).";

        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;
        var sp = ResolveSubProject(ws, subproject);
        if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
            return SubProjectNotFound(ws, subproject!);

        var candidates = sp is null
            ? await _repo.GetByProjectAsync(ws.Id, cancellationToken)
            : await _repo.GetBySubProjectAsync(sp.Id, cancellationToken);

        var matches = MatchFiles(candidates, path);
        if (matches.Count == 0)
            return $"No indexed file matches '{path}'" +
                   (sp is null ? "" : $" in sub-project '{sp.Name}'") +
                   ". Check the path, or call get_subproject_context for the layout.";
        if (matches.Count > 1)
            return "Ambiguous path — multiple files match:\n" +
                   string.Join("\n", matches.Take(10).Select(r => "  " + r.RelativePath)) +
                   "\nRe-call with a more specific path.";

        var record = matches[0];
        var lineCount = CountLines(record);

        var symbols = record.Symbols
            .OrderBy(s => s.Line)
            .Select(s => new FileSymbolDto(s.Name, s.Kind, s.Line, BuildSignature(s)))
            .ToList();

        var exports = record.Symbols
            .Where(s => IsExported(s) && string.IsNullOrEmpty(s.ContainingTypeFullName))
            .Select(s => s.Name)
            .Distinct()
            .ToList();

        var deps = (record.DependsOnFileIds.Count > 0
                ? await _repo.GetByIdsAsync(record.DependsOnFileIds.Distinct().ToList(), cancellationToken)
                : [])
            .Select(r => new DependencyDto(r.FileName, r.RelativePath))
            .OrderBy(d => d.Path)
            .ToList();

        var fileSummary = NullIfEmpty(record.LlmSummary) ?? BuildRuleBasedFileSummary(record);

        var dto = new FileContextDto(
            Path:         record.RelativePath,
            Subproject:   SubProjectNameFor(ws, record),
            Summary:      fileSummary,
            Language:     record.Language,
            Symbols:      symbols,
            Imports:      record.Imports,
            Exports:      exports,
            Dependencies: deps,
            LineCount:    lineCount);

        return Json(dto);
    }

    // ── 3. get_symbol_context ─────────────────────────────────────────────────

    [McpServerTool(Name = "get_symbol_context")]
    [Description(
        "PREFER THIS OVER GREP for any named symbol — class, interface, method, function, type or enum. " +
        "Returns the compiler-resolved definition (file, line, signature), implementations, the callers " +
        "(who calls/instantiates it and from where), the total reference count, whether it is unused, and " +
        "a summary. One call replaces grep plus reading several files to trace a symbol. Answers " +
        "'what is X', 'where is X defined', 'what implements X', 'what calls X'. Covers ALL symbols " +
        "including private/protected — private symbols return definition and signature only (no caller graph). " +
        "Pass 'subproject' to scope; 'kind' (class|interface|method|property|enum|…) to narrow when multiple " +
        "symbols share a name. When the name is ambiguous the tool returns a 'candidates' list with an 'id' " +
        "on each entry — pass that 'id' directly on the next call for an unambiguous single-result lookup.")]
    public async Task<string> GetSymbolContext(
        [Description("The symbol name. May be qualified ('Type.Member') to disambiguate. Omit when using 'id'.")]
        string symbol = "",
        [Description("Sub-project name to scope the lookup (recommended). Omit to search the whole workspace.")]
        string? subproject = null,
        [Description("Hard filter by kind: class, interface, method, property, field, enum, function, type-alias…")]
        string? kind = null,
        [Description("Direct lookup by the 'id' returned in a previous candidates response. Always unambiguous.")]
        string? id = null,
        CancellationToken cancellationToken = default)
    {
        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;

        // ── Direct ID lookup — always unambiguous ─────────────────────────────
        if (!string.IsNullOrWhiteSpace(id))
        {
            var byId = await LookupByIdAsync(id, cancellationToken);
            if (byId is null) return $"No symbol found with id '{id}'.";
            return await BuildSymbolContextAsync(ws, byId, null, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(symbol)) return "Provide a 'symbol' name or 'id'.";

        var sp = ResolveSubProject(ws, subproject);
        if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
            return SubProjectNotFound(ws, subproject!);

        // ── Name-based lookup ─────────────────────────────────────────────────
        symbol = symbol.Trim();
        string? container = null;
        var simpleName = symbol;
        var dot = symbol.LastIndexOf('.');
        if (dot > 0 && dot < symbol.Length - 1)
        {
            container  = symbol[..dot];
            simpleName = symbol[(dot + 1)..];
        }

        var normalizedKind = NormalizeKind(kind);
        var (exact, fuzzy) = await FindSymbolCandidatesAsync(ws, sp?.Id, simpleName, normalizedKind, cancellationToken);

        // When the name is qualified, filter to the matching container.
        if (container is not null && exact.Count > 0)
        {
            var qualified = new List<SymbolReferenceRecord>();
            foreach (var c in exact)
            {
                var rec = (await _repo.GetByIdsAsync([c.DefinedInFileId], cancellationToken)).FirstOrDefault();
                if (rec?.Symbols.Any(s => s.Name == c.SymbolName &&
                    (s.ContainingTypeFullName?.EndsWith(container, StringComparison.OrdinalIgnoreCase) ?? false)) is true)
                    qualified.Add(c);
            }
            if (qualified.Count > 0) exact = qualified;
        }

        // No matches in the reference index — fall back to SymbolRecord (covers private/protected symbols).
        if (exact.Count == 0 && fuzzy.Count == 0)
        {
            var fallback = await SearchInCodeIndexAsync(ws, sp?.Id, simpleName, normalizedKind, cancellationToken);
            if (fallback.Count == 0)
                return $"No symbol named '{symbol}' found" +
                       (sp is null ? "" : $" in sub-project '{sp.Name}'") +
                       (normalizedKind is null ? "" : $" of kind '{normalizedKind}'") +
                       ". Try search_code to locate it, or widen the scope.";
            if (container is not null)
            {
                var defFiles = (await _repo.GetByIdsAsync(
                    fallback.Select(r => r.DefinedInFileId).Distinct().ToList(), cancellationToken))
                    .ToDictionary(r => r.Id, StringComparer.Ordinal);
                var narrowed = fallback.Where(r =>
                    defFiles.TryGetValue(r.DefinedInFileId, out var rec)
                    && rec.Symbols.Any(s => s.Name == r.SymbolName
                        && (s.ContainingTypeFullName?.EndsWith(container, StringComparison.OrdinalIgnoreCase) ?? false)))
                    .ToList();
                if (narrowed.Count > 0) fallback = narrowed;
            }
            exact = fallback;
        }

        // No exact match — return fuzzy suggestions.
        if (exact.Count == 0)
        {
            var suggestions = string.Join(", ", fuzzy.Take(5).Select(r => $"'{r.SymbolName}' ({r.SymbolKind})"));
            return $"No symbol named exactly '{simpleName}' found" +
                   (sp is null ? "" : $" in sub-project '{sp.Name}'") +
                   $". Similar names: {suggestions}. Try search_code or adjust the name.";
        }

        // Exactly one — return full context.
        if (exact.Count == 1)
            return await BuildSymbolContextAsync(ws, exact[0], container, cancellationToken);

        // Multiple exact matches — return candidates for agent to pick.
        var candidateDtos = new List<SymbolCandidateDto>();
        foreach (var c in exact.Take(10))
        {
            var rec = (await _repo.GetByIdsAsync([c.DefinedInFileId], cancellationToken)).FirstOrDefault();
            var sym = PickSymbolRecord(rec, c.SymbolName, container);
            candidateDtos.Add(new SymbolCandidateDto(
                Id:         c.Id,
                Name:       c.SymbolName,
                Kind:       c.SymbolKind,
                File:       c.DefinedInRelativePath,
                Line:       c.DefinedAtLine,
                Signature:  sym is null ? null : BuildSignature(sym),
                Subproject: SubProjectNameForId(ws, c.SubProjectId)));
        }

        return Json(new SymbolAmbiguousDto(
            Message:    $"{candidateDtos.Count} symbols match '{simpleName}'. Narrow with 'kind=', use a " +
                        $"qualified 'Type.Member' name, or pass the 'id' of a candidate for a direct lookup.",
            Candidates: candidateDtos));
    }

    // ── 4. get_symbol_sourcecode ──────────────────────────────────────────────

    [McpServerTool(Name = "get_symbol_sourcecode")]
    [Description(
        "PREFER THIS OVER read_file when you already know the symbol you need. Returns the exact source " +
        "of one method, class, interface or function — its file, start/end lines and the code itself — " +
        "not the whole file. Use it after get_symbol_context tells you the definition exists and you now " +
        "need the implementation. Pass 'subproject' to scope; 'symbol' may be qualified ('Type.Member').")]
    public async Task<string> GetSymbolSourcecode(
        [Description("The symbol name. May be qualified ('Type.Member') to disambiguate.")]
        string symbol,
        [Description("Sub-project name to scope the lookup (recommended). Omit to search the whole workspace.")]
        string? subproject = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return "Provide a 'symbol' name.";

        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;
        var sp = ResolveSubProject(ws, subproject);
        if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
            return SubProjectNotFound(ws, subproject!);

        symbol = symbol.Trim();
        string? container = null;
        var simpleName = symbol;
        var dot = symbol.LastIndexOf('.');
        if (dot > 0 && dot < symbol.Length - 1) { container = symbol[..dot]; simpleName = symbol[(dot + 1)..]; }

        var (exact, _) = await FindSymbolCandidatesAsync(ws, sp?.Id, simpleName, null, cancellationToken);
        if (container is not null && exact.Count > 1)
        {
            var qualified = new List<SymbolReferenceRecord>();
            foreach (var c in exact)
            {
                var r2 = (await _repo.GetByIdsAsync([c.DefinedInFileId], cancellationToken)).FirstOrDefault();
                if (r2?.Symbols.Any(s => s.Name == c.SymbolName &&
                    (s.ContainingTypeFullName?.EndsWith(container, StringComparison.OrdinalIgnoreCase) ?? false)) is true)
                    qualified.Add(c);
            }
            if (qualified.Count > 0) exact = qualified;
        }

        if (exact.Count == 0)
        {
            // Fallback: private/protected symbols not in the reference index
            var fallback = await SearchInCodeIndexAsync(ws, sp?.Id, simpleName, null, cancellationToken);
            if (container is not null && fallback.Count > 1)
            {
                var qualifiedFb = new List<SymbolReferenceRecord>();
                foreach (var c in fallback)
                {
                    var r3 = (await _repo.GetByIdsAsync([c.DefinedInFileId], cancellationToken)).FirstOrDefault();
                    if (r3?.Symbols.Any(s => s.Name == c.SymbolName &&
                        (s.ContainingTypeFullName?.EndsWith(container, StringComparison.OrdinalIgnoreCase) ?? false)) is true)
                        qualifiedFb.Add(c);
                }
                if (qualifiedFb.Count > 0) fallback = qualifiedFb;
            }
            if (fallback.Count == 0)
                return $"No symbol named '{symbol}' found" +
                       (sp is null ? "" : $" in sub-project '{sp.Name}'") +
                       ". Try get_symbol_context or search_code first.";
            exact = fallback;
        }

        var reference = exact[0];
        var defRecord = (await _repo.GetByIdsAsync([reference.DefinedInFileId], cancellationToken)).FirstOrDefault();
        if (defRecord is null) return "The symbol's defining file is no longer indexed.";

        var symRecord = PickSymbolRecord(defRecord, reference.SymbolName, container);
        var startLine = symRecord?.Line > 0 ? symRecord.Line : reference.DefinedAtLine;
        var endLine   = symRecord is { EndLine: > 0 } ? symRecord.EndLine : startLine;

        if (!File.Exists(defRecord.FilePath))
            return $"The file '{defRecord.RelativePath}' is no longer on disk.";

        string[] lines;
        try { lines = await File.ReadAllLinesAsync(defRecord.FilePath, cancellationToken); }
        catch (Exception ex) { return $"Could not read '{defRecord.RelativePath}': {ex.Message}"; }

        var s = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        var e = Math.Clamp(endLine, s, Math.Max(1, lines.Length));
        var source = lines.Length == 0 ? "" : string.Join("\n", lines[(s - 1)..e]);

        var dto = new SymbolSourceDto(
            Symbol:    container is null ? reference.SymbolName : $"{container}.{reference.SymbolName}",
            File:      defRecord.RelativePath,
            LineStart: s,
            LineEnd:   e,
            Source:    source);

        return Json(dto);
    }

    // ── 5. search_code ────────────────────────────────────────────────────────

    [McpServerTool(Name = "search_code")]
    [Description(
        "Find files by meaning or keyword when you don't yet know the symbol or file name — use this " +
        "instead of grep. Returns the best-matching files with their path, top symbols and AI summary, " +
        "ranked by relevance (semantic when the embedding model is available, otherwise lexical). " +
        "Pass 'subproject' to scope to one language/area; omit to search the whole workspace. Then narrow " +
        "with get_file_context or get_symbol_context.")]
    public async Task<string> SearchCode(
        [Description("What to look for — concepts, route paths, domain terms, partial names.")]
        string query,
        [Description("Sub-project name to scope to one area (recommended). Omit to search everything.")]
        string? subproject = null,
        [Description("Maximum number of results (1–25).")]
        int top_n = 10,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return "Provide a 'query'.";

        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;
        var sp = ResolveSubProject(ws, subproject);
        if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
            return SubProjectNotFound(ws, subproject!);

        top_n = Math.Clamp(top_n, 1, 25);

        IReadOnlyList<CodeIndexRecord> results;
        if (_embedding.IsAvailable)
        {
            var vector = await _embedding.GetEmbeddingAsync(query, cancellationToken);
            var scored = await _repo.SearchByEmbeddingAsync(vector, ws.Id, sp?.Id, top_n, cancellationToken);
            results = scored.Select(x => x.Record).ToList();
        }
        else
        {
            var lexical = await _repo.SearchLexicalAsync(query, ws.Id, sp?.Id, cancellationToken);
            results = lexical.Take(top_n).ToList();
        }

        if (results.Count == 0)
            return $"No files matched '{query}'" + (sp is null ? "" : $" in sub-project '{sp.Name}'") + ".";

        var dto = results.Select(r => new SearchHitDto(
            Path:       r.RelativePath,
            Subproject: SubProjectNameFor(ws, r),
            Language:   r.Language,
            Symbols:    r.Symbols.Take(8).Select(s => s.Name).ToList(),
            Summary:    NullIfEmpty(r.LlmSummary))).ToList();

        return Json(dto);
    }

    // ── 6. list_symbols ───────────────────────────────────────────────────────

    [McpServerTool(Name = "list_symbols")]
    [Description(
        "List all indexed symbols of a given kind within a sub-project. Use this when you want a " +
        "structured inventory — 'all interfaces', 'all public classes', 'all React hooks' — rather than " +
        "searching for a specific name. Returns id, name, kind, file, line and signature. " +
        "Pass the returned 'id' to get_symbol_context for full details on any entry. " +
        "By default only public/internal/exported symbols are listed; set publicOnly=false to also include private and protected symbols.")]
    public async Task<string> ListSymbols(
        [Description("Hard filter by kind: class, interface, method, property, field, enum, function, type-alias, record, struct…")]
        string? kind = null,
        [Description("Sub-project name to scope the listing (recommended). Omit to list across the whole workspace.")]
        string? subproject = null,
        [Description("Set false to include private and protected symbols in addition to public/internal/exported. Default true (public surface only).")]
        bool publicOnly = true,
        [Description("Maximum results to return (1–200). Default 100.")]
        int top_n = 100,
        CancellationToken cancellationToken = default)
    {
        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;
        var sp = ResolveSubProject(ws, subproject);
        if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
            return SubProjectNotFound(ws, subproject!);

        top_n = Math.Clamp(top_n, 1, 200);
        var normalizedKind = NormalizeKind(kind);
        var kinds = normalizedKind is not null ? new[] { normalizedKind } : null;

        List<SymbolReferenceRecord> results;

        if (!publicOnly)
        {
            // Full scan of CodeIndexRecord.Symbols — the only complete symbol list (all accessibility).
            // The reference index only holds public/internal/exported, so appending to it puts private
            // symbols past the top_n cut. Instead scan files first, then patch real reference data back
            // in for public/internal symbols so orphan/fan-in is accurate.
            var files = sp is not null
                ? await _repo.GetBySubProjectAsync(sp.Id, cancellationToken)
                : await _repo.GetByProjectAsync(ws.Id, cancellationToken);

            results = [];
            foreach (var file in files)
                foreach (var sym in file.Symbols)
                {
                    if (normalizedKind is not null && !sym.Kind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase)) continue;
                    results.Add(SynthesizeSymbolReference(file, sym));
                }

            // Patch in real reference records (UsedBy, ExternalUseCount) for public/internal.
            var realRefs = (await _repo.SearchSymbolsAsync("", ws.Id, sp?.Id, false, kinds, 0, cancellationToken))
                .ToDictionary(r => r.Id, StringComparer.Ordinal);
            for (var i = 0; i < results.Count; i++)
                if (realRefs.TryGetValue(results[i].Id, out var real))
                    results[i] = real;
        }
        else
        {
            results = (await _repo.SearchSymbolsAsync("", ws.Id, sp?.Id, true, kinds, 0, cancellationToken)).ToList();
            if (normalizedKind is not null)
                results = results.Where(r => r.SymbolKind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (results.Count == 0)
            return $"No{(normalizedKind is null ? "" : $" {normalizedKind}")} symbols found" +
                   (sp is null ? "" : $" in sub-project '{sp.Name}'") + ".";

        var fileIds = results.Select(r => r.DefinedInFileId).Distinct().ToList();
        var fileRecs = (await _repo.GetByIdsAsync(fileIds, cancellationToken))
            .ToDictionary(r => r.Id, StringComparer.Ordinal);

        var dtos = results.Take(top_n).Select(r =>
        {
            fileRecs.TryGetValue(r.DefinedInFileId, out var rec);
            var sym = rec is null ? null : PickSymbolRecord(rec, r.SymbolName, null);
            return new ListSymbolsHitDto(
                Id:            r.Id,
                Name:          r.SymbolName,
                Kind:          r.SymbolKind,
                File:          r.DefinedInRelativePath,
                Line:          r.DefinedAtLine,
                Signature:     sym is null ? null : BuildSignature(sym),
                Accessibility: r.Accessibility,
                Subproject:    SubProjectNameForId(ws, r.SubProjectId));
        }).ToList();

        return Json(dtos);
    }

    // ── 7. get_callers ────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_callers")]
    [Description(
        "Return every known call site for a symbol — who calls it, from which file and line. " +
        "Faster than get_symbol_context when you only want the caller graph, not full context. " +
        "Pass 'symbol' (name or qualified 'Type.Member') or the 'id' from a previous candidates response.")]
    public async Task<string> GetCallers(
        [Description("Symbol name to look up. May be qualified ('Type.Member'). Omit when using 'id'.")]
        string symbol = "",
        [Description("Direct lookup by id from a previous get_symbol_context candidates response.")]
        string? id = null,
        [Description("Sub-project name to scope the lookup. Omit to search the whole workspace.")]
        string? subproject = null,
        [Description("Hard filter by kind when name is ambiguous: class, method, interface…")]
        string? kind = null,
        CancellationToken cancellationToken = default)
    {
        var (ws, error) = ResolveWorkspace();
        if (ws is null) return error!;

        SymbolReferenceRecord? reference;

        if (!string.IsNullOrWhiteSpace(id))
        {
            reference = await LookupByIdAsync(id, cancellationToken);
            if (reference is null) return $"No symbol found with id '{id}'.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(symbol)) return "Provide a 'symbol' name or 'id'.";
            var sp = ResolveSubProject(ws, subproject);
            if (!string.IsNullOrWhiteSpace(subproject) && sp is null)
                return SubProjectNotFound(ws, subproject!);

            symbol = symbol.Trim();
            var simpleName = symbol;
            var dot = symbol.LastIndexOf('.');
            string? container = null;
            if (dot > 0 && dot < symbol.Length - 1) { container = symbol[..dot]; simpleName = symbol[(dot + 1)..]; }

            var (exact, _) = await FindSymbolCandidatesAsync(ws, sp?.Id, simpleName, NormalizeKind(kind), cancellationToken);
            if (exact.Count == 0)
                return $"No symbol named '{symbol}' found. Try get_symbol_context or search_code.";
            if (exact.Count > 1)
                return Json(new SymbolAmbiguousDto(
                    Message:    $"{exact.Count} symbols match '{simpleName}'. Pass 'id' from a candidate to get callers for a specific one.",
                    Candidates: exact.Take(10).Select(c => new SymbolCandidateDto(
                        c.Id, c.SymbolName, c.SymbolKind, c.DefinedInRelativePath, c.DefinedAtLine, null,
                        SubProjectNameForId(ws, c.SubProjectId))).ToList()));

            reference = exact[0];
        }

        var callers = reference.UsedBy
            .Where(u => u.Role is "call" or "new" or "implements")
            .Select(u => new CallerDto(u.EnclosingName ?? "(top-level)", u.RelativePath, u.Line))
            .DistinctBy(c => (c.Symbol, c.File, c.Line))
            .Take(100)
            .ToList();

        return Json(new CallerListResultDto(
            Symbol:          reference.SymbolName,
            Kind:            reference.SymbolKind,
            File:            reference.DefinedInRelativePath,
            Line:            reference.DefinedAtLine,
            TotalReferences: reference.UsedBy.Count,
            Callers:         callers));
    }

    // ── Workspace / sub-project resolution ────────────────────────────────────

    private (WorkspaceRecord? Workspace, string? Error) ResolveWorkspace()
    {
        var workspaces = LoadWorkspaces();
        if (workspaces.Count == 0)
            return (null, "No workspace is registered. Add one in the agentic-memory dashboard " +
                          "(or POST /api/workspaces) and let it index, then retry.");

        var activeId = _activeProject.ActiveProjectId;
        if (!string.IsNullOrEmpty(activeId))
        {
            var active = workspaces.FirstOrDefault(w => w.Id == activeId);
            if (active is not null) return (active, null);
        }

        if (workspaces.Count == 1) return (workspaces[0], null);

        var names = string.Join(", ", workspaces.Select(w => $"\"{w.Name}\""));
        return (null, $"Multiple workspaces are registered ({names}) and none is active. " +
                      "Activate the one you're working in from the dashboard, then retry.");
    }

    private static SubProjectRecord? ResolveSubProject(WorkspaceRecord ws, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        return ws.SubProjects.FirstOrDefault(s =>
                   s.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                   s.Namespace.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                   s.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                   FolderName(s.RootPath).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string SubProjectNotFound(WorkspaceRecord ws, string name)
    {
        var available = ws.SubProjects.Count == 0
            ? "(none)"
            : string.Join(", ", ws.SubProjects.Select(s => $"\"{s.Name}\""));
        return $"No sub-project named '{name}'. Available: {available}. " +
               "Call get_subproject_context for the full layout.";
    }

    private static string SubProjectNameFor(WorkspaceRecord ws, CodeIndexRecord record) =>
        SubProjectNameForId(ws, record.SubProjectId);

    private static string SubProjectNameForId(WorkspaceRecord ws, string? subProjectId) =>
        (string.IsNullOrEmpty(subProjectId)
            ? null
            : ws.SubProjects.FirstOrDefault(s => s.Id == subProjectId)?.Name)
        ?? ws.Name;

    // ── File matching ─────────────────────────────────────────────────────────

    private static List<CodeIndexRecord> MatchFiles(IReadOnlyList<CodeIndexRecord> candidates, string path)
    {
        var q = path.Replace('\\', '/').Trim().TrimStart('.', '/');

        // Exact relative-path or file-name match wins outright.
        var exact = candidates.Where(r =>
            Norm(r.RelativePath).Equals(q, StringComparison.OrdinalIgnoreCase) ||
            r.FileName.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) return exact.DistinctBy(r => r.Id).ToList();

        // Otherwise: any file whose path ends with the query on a path-segment boundary.
        var suffixed = candidates.Where(r =>
            EndsOnSegment(Norm(r.FilePath), q) || EndsOnSegment(Norm(r.RelativePath), q)).ToList();

        return suffixed.DistinctBy(r => r.Id).OrderBy(r => r.RelativePath.Length).ToList();
    }

    private static bool EndsOnSegment(string haystack, string needle) =>
        haystack.Equals(needle, StringComparison.OrdinalIgnoreCase) ||
        haystack.EndsWith("/" + needle, StringComparison.OrdinalIgnoreCase);

    // ── Symbol resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Resolves a (possibly qualified) symbol name to its reference record. Returns the container part
    // Returns (exact matches, fuzzy/prefix matches) separately so callers can distinguish
    // "one true result" from "multiple candidates needing agent disambiguation".
    // Kind is enforced as a HARD filter — no fuzzy kind fallback.
    private async Task<(List<SymbolReferenceRecord> Exact, List<SymbolReferenceRecord> Fuzzy)>
        FindSymbolCandidatesAsync(
            WorkspaceRecord ws, string? subProjectId, string simpleName, string? normalizedKind, CancellationToken ct)
    {
        var kinds   = normalizedKind is not null ? new[] { normalizedKind } : null;
        var all     = await _repo.SearchSymbolsAsync(simpleName, ws.Id, subProjectId, false, kinds, 0, ct);

        // Double-enforce kind — SearchSymbolsAsync uses it for ranking; this makes it a hard gate.
        if (normalizedKind is not null)
            all = all.Where(r => r.SymbolKind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase)).ToList();

        var exact = all.Where(r =>  r.SymbolName.Equals(simpleName, StringComparison.OrdinalIgnoreCase)).ToList();
        var fuzzy = all.Where(r => !r.SymbolName.Equals(simpleName, StringComparison.OrdinalIgnoreCase)).ToList();
        return (exact, fuzzy);
    }

    // Direct lookup by SymbolReferenceRecord.Id ("{fileId}::{symbolName}") — always unambiguous.
    // Falls back to SymbolRecord when the symbol is private/protected and not in the reference index.
    private async Task<SymbolReferenceRecord?> LookupByIdAsync(string id, CancellationToken ct)
    {
        var sep = id.IndexOf("::", StringComparison.Ordinal);
        if (sep < 0) return null;
        var fileId  = id[..sep];
        var symName = id[(sep + 2)..];
        var refs    = await _repo.GetDefinedInFileAsync(fileId, ct);
        var found   = refs.FirstOrDefault(r => r.Id.Equals(id, StringComparison.Ordinal));
        if (found is not null) return found;

        // Fallback: private symbol — synthesize from SymbolRecord
        var file = (await _repo.GetByIdsAsync([fileId], ct)).FirstOrDefault();
        if (file is null) return null;
        var sym = file.Symbols.FirstOrDefault(s => s.Name.Equals(symName, StringComparison.OrdinalIgnoreCase));
        return sym is null ? null : SynthesizeSymbolReference(file, sym);
    }

    // Builds the full SymbolContextDto JSON for one resolved reference.
    private async Task<string> BuildSymbolContextAsync(
        WorkspaceRecord ws, SymbolReferenceRecord reference, string? container, CancellationToken ct)
    {
        var defRecord = (await _repo.GetByIdsAsync([reference.DefinedInFileId], ct)).FirstOrDefault();
        var symRecord = PickSymbolRecord(defRecord, reference.SymbolName, container);

        var implementations = await ResolveImplementationsAsync(ws, reference.SubProjectId, reference.SymbolName, ct);

        // Include "implements" role so inheriting classes appear for abstract bases / interfaces.
        var callers = reference.UsedBy
            .Where(u => u.Role is "call" or "new" or "implements")
            .Select(u => new CallerDto(u.EnclosingName ?? "(top-level)", u.RelativePath, u.Line))
            .DistinctBy(c => (c.Symbol, c.File, c.Line))
            .Take(50)
            .ToList();

        var summary = symRecord is null ? null
            : NullIfEmpty(symRecord.DocSummary)
              ?? NullIfEmpty(symRecord.NlDescription)
              ?? BuildRuleBasedSymbolSummary(symRecord);

        var dto = new SymbolContextDto(
            Symbol:          reference.SymbolName,
            Subproject:      SubProjectNameForId(ws, reference.SubProjectId),
            Kind:            reference.SymbolKind,
            Definition:      new DefinitionDto(
                File:      reference.DefinedInRelativePath,
                Line:      reference.DefinedAtLine,
                Signature: symRecord is null ? null : BuildSignature(symRecord)),
            Implementations: implementations,
            Callers:         callers,
            ReferencesCount: reference.UsedBy.Count,
            Summary:         summary,
            Id:              reference.Id);

        return Json(dto);
    }

    // Generates a minimal useful summary from structured metadata when no LLM/doc summary exists.
    private static string? BuildRuleBasedSymbolSummary(SymbolRecord s)
    {
        var parts = new List<string>();
        if (s.IsAbstract)  parts.Add("abstract");
        if (s.IsStatic)    parts.Add("static");
        if (s.IsAsync)     parts.Add("async");
        if (s.Interfaces.Count > 0)  parts.Add("implements " + string.Join(", ", s.Interfaces.Take(2)));
        if (s.BaseChain.Count > 0)   parts.Add("extends " + s.BaseChain[0]);
        if (!string.IsNullOrEmpty(s.ReturnTypeUnwrapped)) parts.Add($"→ {s.ReturnTypeUnwrapped}");
        if (s.Parameters.Count > 0)  parts.Add($"{s.Parameters.Count} param{(s.Parameters.Count == 1 ? "" : "s")}");
        return parts.Count > 0 ? string.Join("; ", parts) : null;
    }

    // Generates a file-level rule-based summary when LlmSummary has not yet been populated.
    private static string? BuildRuleBasedFileSummary(CodeIndexRecord r)
    {
        var topTypes = r.Symbols
            .Where(s => s.Kind is "class" or "interface" or "record" or "struct" or "enum" or "function")
            .Take(3)
            .Select(s => $"{s.Kind} {s.Name}")
            .ToList();
        var tags = r.DomainTags.Take(3).ToList();
        var parts = new List<string>();
        if (topTypes.Count > 0) parts.Add(string.Join(", ", topTypes));
        if (tags.Count > 0)     parts.Add(string.Join(" / ", tags));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static SymbolRecord? PickSymbolRecord(CodeIndexRecord? record, string name, string? container)
    {
        if (record is null) return null;
        var named = record.Symbols.Where(s => s.Name == name).ToList();
        if (named.Count == 0) return null;
        if (container is not null)
        {
            var scoped = named.FirstOrDefault(s =>
                s.ContainingTypeFullName?.EndsWith(container, StringComparison.OrdinalIgnoreCase) ?? false);
            if (scoped is not null) return scoped;
        }
        // Prefer the declaration with the widest span (the type/method body, not a partial).
        return named.OrderByDescending(s => Math.Max(0, s.EndLine - s.Line)).First();
    }

    // Synthesizes a stub SymbolReferenceRecord from a SymbolRecord for private/protected symbols
    // that are not present in the reference index. Caller graph is always empty for these.
    private static SymbolReferenceRecord SynthesizeSymbolReference(CodeIndexRecord file, SymbolRecord sym) =>
        new SymbolReferenceRecord
        {
            Id                    = $"{file.Id}::{sym.Name}",
            SymbolName            = sym.Name,
            SymbolKind            = sym.Kind,
            Accessibility         = sym.Accessibility,
            DefinedInFileId       = file.Id,
            DefinedInRelativePath = file.RelativePath,
            DefinedAtLine         = sym.Line,
            ProjectId             = file.ProjectId,
            SubProjectId          = string.IsNullOrEmpty(file.SubProjectId) ? null : file.SubProjectId,
            UsedBy                = [],
            ExternalUseCount      = 0,
            TestedByFileIds       = [],
            UpdatedAt             = file.IndexedAt,
        };

    // Searches CodeIndexRecord.Symbols directly — the authoritative per-file symbol list that includes
    // all accessibility levels. Used as a fallback when the reference index has no entry (private/protected).
    private async Task<List<SymbolReferenceRecord>> SearchInCodeIndexAsync(
        WorkspaceRecord ws, string? subProjectId, string simpleName, string? normalizedKind, CancellationToken ct)
    {
        var files = subProjectId is not null
            ? await _repo.GetBySubProjectAsync(subProjectId, ct)
            : await _repo.GetByProjectAsync(ws.Id, ct);

        var results = new List<SymbolReferenceRecord>();
        foreach (var file in files)
        {
            foreach (var sym in file.Symbols)
            {
                if (!sym.Name.Equals(simpleName, StringComparison.OrdinalIgnoreCase)) continue;
                if (normalizedKind is not null && !sym.Kind.Equals(normalizedKind, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(SynthesizeSymbolReference(file, sym));
            }
        }
        return results;
    }

    private async Task<IReadOnlyList<ImplementationDto>> ResolveImplementationsAsync(
        WorkspaceRecord ws, string? subProjectId, string symbolName, CancellationToken ct)
    {
        var facts = await _repo.GetDomainFactsByProjectAsync(ws.Id, "type-relation", subProjectId, ct);
        var rel = facts
            .Where(f => string.Equals(f.Name, symbolName, StringComparison.OrdinalIgnoreCase)
                     && (f.Method == "implements" || f.Method == "extends")
                     && !string.IsNullOrEmpty(f.OwnerType))
            .ToList();
        if (rel.Count == 0) return [];

        var fileIds = rel.Select(f => f.FileId).Distinct().ToList();
        var recs = await _repo.GetByIdsAsync(fileIds, ct);
        var pathById = recs.ToDictionary(r => r.Id, r => r.RelativePath, StringComparer.Ordinal);

        return rel
            .Select(f => new ImplementationDto(f.OwnerType!, pathById.GetValueOrDefault(f.FileId, ""), f.Line))
            .DistinctBy(i => (i.Name, i.File))
            .ToList();
    }

    // ── Sub-project description / manifest helpers ────────────────────────────

    private static string DescribeSubProject(SubProjectRecord sp, IReadOnlyList<CodeIndexRecord> files)
    {
        var parts = new List<string> { $"{files.Count} indexed file{(files.Count == 1 ? "" : "s")}" };

        var roles = files
            .Where(f => !string.IsNullOrEmpty(f.ArchitecturalRole))
            .GroupBy(f => f.ArchitecturalRole!)
            .OrderByDescending(g => g.Count())
            .Take(4)
            .Select(g => g.Key)
            .ToList();
        if (roles.Count > 0) parts.Add("roles: " + string.Join(", ", roles));

        var endpoints = files.Sum(f => f.DomainTags.Count(t => t.Contains("endpoint", StringComparison.OrdinalIgnoreCase)));
        if (endpoints > 0) parts.Add($"{endpoints} endpoint{(endpoints == 1 ? "" : "s")}");

        return $"{sp.Language} sub-project · {string.Join(" · ", parts)}";
    }

    private static string? ManifestFramework(ProjectManifestRecord m)
    {
        if (m.TargetFrameworks.Count > 0) return string.Join(", ", m.TargetFrameworks);
        if (m.Packages.Any(p => p.Name.Equals("react", StringComparison.OrdinalIgnoreCase))) return "react";
        if (m.Packages.Any(p => p.Name.Equals("vue", StringComparison.OrdinalIgnoreCase))) return "vue";
        return m.Packages.Count > 0 ? "node" : null;
    }

    private static string? FindEntryPoint(IReadOnlyList<CodeIndexRecord> files, WorkspaceRecord ws, SubProjectRecord sp)
    {
        var declared = files.FirstOrDefault(f => f.IsEntrypoint);
        if (declared is not null) return RelToWorkspace(ws, declared.FilePath);

        // Convention fallback, in priority order.
        string[] conventions = ["Program.cs", "main.tsx", "main.ts", "index.tsx", "index.ts", "App.tsx", "app.ts"];
        foreach (var name in conventions)
        {
            var hit = files.FirstOrDefault(f => f.FileName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return RelToWorkspace(ws, hit.FilePath);
        }
        return null;
    }

    // ── Signature building ────────────────────────────────────────────────────

    private static string BuildSignature(SymbolRecord s)
    {
        var prefix = string.IsNullOrEmpty(s.Accessibility) ? "" : s.Accessibility + " ";
        var mods = new List<string>();
        if (s.IsStatic) mods.Add("static");
        if (s.IsAbstract) mods.Add("abstract");
        if (s.IsAsync) mods.Add("async");
        var modStr = mods.Count > 0 ? string.Join(" ", mods) + " " : "";

        switch (s.Kind)
        {
            case "method":
            case "function":
            case "constructor":
            {
                var ret = NullIfEmpty(s.ReturnTypeUnwrapped) ?? NullIfEmpty(s.Type);
                var retStr = ret is null ? "" : ret + " ";
                var ps = string.Join(", ", s.Parameters
                    .OrderBy(p => p.Ordinal)
                    .Select(FormatParam));
                return $"{prefix}{modStr}{retStr}{s.Name}({ps})".Trim();
            }
            case "property":
            case "field":
            case "event":
            case "variable":
            {
                var t = NullIfEmpty(s.Type);
                var tStr = t is null ? "" : t + " ";
                return $"{prefix}{modStr}{tStr}{s.Name}".Trim();
            }
            default: // class / interface / struct / record / enum / type-alias / namespace / delegate
            {
                var tp = s.TypeParameters.Count > 0
                    ? "<" + string.Join(", ", s.TypeParameters.Select(t => t.Name)) + ">"
                    : "";
                var kindWord = string.IsNullOrEmpty(s.Kind) ? "" : s.Kind + " ";
                return $"{prefix}{modStr}{kindWord}{s.Name}{tp}".Trim();
            }
        }
    }

    private static string FormatParam(ParameterRecord p)
    {
        var refKind = string.IsNullOrEmpty(p.RefKind) || p.RefKind.Equals("none", StringComparison.OrdinalIgnoreCase)
            ? ""
            : p.RefKind.ToLowerInvariant() + " ";
        var paramsKw = p.IsParams ? "params " : "";
        var type = string.IsNullOrEmpty(p.Type) ? "" : p.Type + " ";
        var def = string.IsNullOrEmpty(p.DefaultValue) ? "" : " = " + p.DefaultValue;
        return $"{paramsKw}{refKind}{type}{p.Name}{def}".Trim();
    }

    // ── Small utilities ───────────────────────────────────────────────────────

    private static bool IsExported(SymbolRecord s) =>
        s.Accessibility is "public" or "exported" or "export";

    private static int CountLines(CodeIndexRecord record)
    {
        if (File.Exists(record.FilePath))
        {
            try { return File.ReadLines(record.FilePath).Count(); }
            catch { /* fall through to symbol-span estimate */ }
        }
        return record.Symbols.Count > 0 ? record.Symbols.Max(s => Math.Max(s.Line, s.EndLine)) : 0;
    }

    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        var k = kind.Trim().ToLowerInvariant();
        return k switch
        {
            "all" => null,
            "typealias" or "type alias" => "type-alias",
            _ => k,
        };
    }

    private static string Norm(string path) => path.Replace('\\', '/');
    private static string FolderName(string path) => new DirectoryInfo(path.TrimEnd('/', '\\')).Name;
    private static string RelToWorkspace(WorkspaceRecord ws, string fullPath) =>
        Norm(Path.GetRelativePath(ws.RootPath, fullPath));

    private static bool PathEq(string a, string b) =>
        Norm(a).TrimEnd('/').Equals(Norm(b).TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string path, string root)
    {
        var p = Norm(path);
        var r = Norm(root).TrimEnd('/') + "/";
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOpts);

    private List<WorkspaceRecord> LoadWorkspaces()
    {
        var json = _kv.Get("workspaces");
        return string.IsNullOrEmpty(json)
            ? []
            : JsonSerializer.Deserialize<List<WorkspaceRecord>>(json) ?? [];
    }
}

// ── Response DTOs (serialized snake_case to match core-tools-mcp.md) ───────────

internal sealed record SubprojectContextDto(
    string Name, string Path, string Language, string? EntryPoint,
    string Description, ManifestSummaryDto? Manifest);

internal sealed record ManifestSummaryDto(
    string? Framework, IReadOnlyList<string> KeyDependencies, string? ManifestFile);

internal sealed record FileContextDto(
    string Path, string Subproject, string? Summary, string Language,
    IReadOnlyList<FileSymbolDto> Symbols, IReadOnlyList<string> Imports,
    IReadOnlyList<string> Exports, IReadOnlyList<DependencyDto> Dependencies, int LineCount);

internal sealed record FileSymbolDto(string Name, string Kind, int Line, string? Signature);
internal sealed record DependencyDto(string Name, string Path);

internal sealed record SymbolContextDto(
    string Symbol, string Subproject, string Kind, DefinitionDto Definition,
    IReadOnlyList<ImplementationDto> Implementations, IReadOnlyList<CallerDto> Callers,
    int ReferencesCount, string? Summary, string Id);

internal sealed record DefinitionDto(string File, int Line, string? Signature);
internal sealed record ImplementationDto(string Name, string File, int Line);
internal sealed record CallerDto(string Symbol, string File, int Line);

// Returned when a name matches multiple symbols — agent picks using kind/qualified-name/id.
internal sealed record SymbolAmbiguousDto(string Message, IReadOnlyList<SymbolCandidateDto> Candidates);
internal sealed record SymbolCandidateDto(
    string Id, string Name, string Kind, string File, int Line,
    string? Signature, string Subproject);

// list_symbols result
internal sealed record ListSymbolsHitDto(
    string Id, string Name, string Kind, string File, int Line,
    string? Signature, string Accessibility, string Subproject);

// get_callers result
internal sealed record CallerListResultDto(
    string Symbol, string Kind, string File, int Line,
    int TotalReferences, IReadOnlyList<CallerDto> Callers);

internal sealed record SymbolSourceDto(
    string Symbol, string File, int LineStart, int LineEnd, string Source);

internal sealed record SearchHitDto(
    string Path, string Subproject, string Language,
    IReadOnlyList<string> Symbols, string? Summary);
