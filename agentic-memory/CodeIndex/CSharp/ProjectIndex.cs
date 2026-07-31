using System.Collections.Concurrent;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace AgenticMemory.CodeIndex.CSharp;

/// <summary>
/// Holds the whole-program Roslyn compilation AND a pre-built inverted reference index for one
/// C# project root.
///
/// REFERENCE INDEX: After BuildAsync, one pass over all syntax trees constructs
/// _referenceIndex: symbolName → List&lt;ReferenceInfo&gt;
/// _declaredSymbols: symbolName → ISymbol (for semantic identity checks)
///
/// This makes FindAllReferencesAsync an O(symbolNames.Count) dictionary lookup instead of an
/// O(files × identifiers) AST traversal — the same approach used by VS/Rider.
///
/// INCREMENTAL UPDATES: When a file is re-parsed via GetOrParseSyntaxTree, that file's
/// contributions are surgically removed from the index and re-scanned. O(changed_file) instead
/// of O(whole_codebase).
///
/// THREAD SAFETY: The reference index is protected by _indexLock. The ingestion worker (writes,
/// via GetOrParseSyntaxTree) and the reference worker (reads, via QueryReferences) both take this
/// lock. Operations are fast (list manipulation + GetSymbolInfo on one file), so contention is
/// negligible.
/// </summary>
internal sealed class ProjectIndex : IDisposable
{
    private readonly string _root;
    private CSharpCompilation? _compilation;
    private readonly ConcurrentDictionary<string, (SyntaxTree Tree, string Version)> _fileCache
        = new(StringComparer.OrdinalIgnoreCase);

    // ── Reference index ───────────────────────────────────────────────────────
    // Bucketed by textual symbol NAME (so QueryReferences stays an O(names) dictionary lookup), but
    // each ReferenceInfo carries the resolved TargetDocId so consumers can attribute a site to the
    // exact overload/declaration it targets. _declaredNames drives the cheap text pre-filter;
    // _declaredDocIds is the membership set a resolved reference must hit to be a real project edge.

    private HashSet<string> _declaredNames  = new(StringComparer.Ordinal);
    private HashSet<string> _declaredDocIds = new(StringComparer.Ordinal);
    private Dictionary<string, List<ReferenceInfo>> _referenceIndex = new(StringComparer.Ordinal);
    private readonly object _indexLock = new();
    private volatile bool   _indexBuilt;

    // ── Public surface ────────────────────────────────────────────────────────

    internal string Root => _root;
    internal CSharpCompilation? Compilation => _compilation;
    internal bool IsIndexBuilt => _indexBuilt;

    internal ProjectIndex(string root, ExcludedFolderMatcher excluded)
    {
        _root = root;
        _excluded = excluded;
    }

    private readonly ExcludedFolderMatcher _excluded;

    // ── Build ─────────────────────────────────────────────────────────────────

    internal async Task BuildAsync(CancellationToken ct = default)
    {
        var csPaths = Directory.EnumerateFiles(_root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IsInExcludedDir(p))
            .ToArray();

        var trees = new SyntaxTree[csPaths.Length];
        for (var i = 0; i < csPaths.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var src  = await File.ReadAllTextAsync(csPaths[i], ct);
            var tree = ParseFile(csPaths[i], src);
            trees[i] = tree;
            _fileCache[csPaths[i]] = (tree, VersionOf(src));
        }

        _compilation = CSharpCompilation.Create(
            assemblyName: Path.GetFileName(_root),
            syntaxTrees: trees,
            references: BuildReferences(_root),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Build the inverted reference index synchronously — one-time O(N) cost.
        // All subsequent FindAllReferences queries are O(symbolNames.Count) lookups.
        BuildReferenceIndex(ct);
    }

    // ── Compilation queries ───────────────────────────────────────────────────

    internal SemanticModel? GetSemanticModel(string filePath)
    {
        if (_compilation is null) return null;
        if (!_fileCache.TryGetValue(filePath, out _)) return null;

        var compilationTree = _compilation.SyntaxTrees
            .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        return compilationTree is null ? null
            : _compilation.GetSemanticModel(compilationTree, ignoreAccessibility: true);
    }

    /// <summary>
    /// Returns a parsed SyntaxTree, re-using the cached version when the file has not changed.
    /// Updates the compilation incrementally (replaces just the changed tree) and surgically
    /// patches the reference index for that file.
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

            // Incrementally update the reference index — O(changed file) not O(whole codebase).
            if (_indexBuilt)
                UpdateFileInIndex(filePath, newTree);
        }

        return newTree;
    }

    // ── Reference index queries ───────────────────────────────────────────────

    /// <summary>
    /// O(symbolNames.Count) lookup into the pre-built index.
    /// Returns a snapshot so callers can read safely without holding the lock.
    /// </summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>> QueryReferences(
        IReadOnlyList<string> symbolNames)
    {
        var result = new Dictionary<string, IReadOnlyList<ReferenceInfo>>(StringComparer.Ordinal);
        lock (_indexLock)
        {
            foreach (var name in symbolNames)
            {
                if (_referenceIndex.TryGetValue(name, out var refs) && refs.Count > 0)
                    result[name] = refs.ToList(); // snapshot under lock
            }
        }
        return result;
    }

    // ── Reference index build (one-time, after compilation is ready) ──────────

    private void BuildReferenceIndex(CancellationToken ct)
    {
        if (_compilation is null) return;

        var declaredNames  = new HashSet<string>(StringComparer.Ordinal);
        var declaredDocIds = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: collect EVERY declared symbol's name and stable DocId across the whole program.
        // Unlike a name→firstSymbol map, recording all DocIds means overloads and same-named members
        // on different types each get their own identity — references can't be dropped or stolen.
        foreach (var tree in _compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var root  = tree.GetRoot(ct);
            var model = _compilation.GetSemanticModel(tree, ignoreAccessibility: true);

            foreach (var decl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                foreach (var sym in DeclaredSymbolsOf(model, decl))
                {
                    if (string.IsNullOrEmpty(sym.Name)) continue;
                    declaredNames.Add(sym.Name);
                    var docId = sym.GetDocumentationCommentId();
                    if (docId is not null) declaredDocIds.Add(docId);
                }
        }

        var refIndex = new Dictionary<string, List<ReferenceInfo>>(StringComparer.Ordinal);

        // Pass 2: single traversal of all trees, collecting references to all known symbols
        // simultaneously. The text pre-filter (declaredNames.Contains) before the expensive
        // GetSymbolInfo call skips the vast majority of identifiers (locals, keywords, etc.).
        foreach (var tree in _compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            ScanTreeIntoIndex(tree, _compilation, declaredNames, declaredDocIds, refIndex, ct);
        }

        lock (_indexLock)
        {
            _declaredNames   = declaredNames;
            _declaredDocIds  = declaredDocIds;
            _referenceIndex  = refIndex;
            _indexBuilt      = true;
        }
    }

    // ── Incremental update when one file changes ──────────────────────────────

    private void UpdateFileInIndex(string filePath, SyntaxTree newTree)
    {
        if (_compilation is null) return;

        lock (_indexLock)
        {
            var model = _compilation.GetSemanticModel(newTree, ignoreAccessibility: true);
            var root  = newTree.GetRoot();

            // Remove all references FROM this file so we don't accumulate stale entries.
            foreach (var list in _referenceIndex.Values)
                list.RemoveAll(r => r.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            // If the file declares new symbols (e.g., new class added), register their name + DocId.
            foreach (var decl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
                foreach (var sym in DeclaredSymbolsOf(model, decl))
                {
                    if (string.IsNullOrEmpty(sym.Name)) continue;
                    _declaredNames.Add(sym.Name);
                    var docId = sym.GetDocumentationCommentId();
                    if (docId is not null) _declaredDocIds.Add(docId);
                }

            // Re-scan this file and add its updated references.
            ScanTreeIntoIndex(newTree, _compilation, _declaredNames, _declaredDocIds, _referenceIndex, default);
        }
    }

    // Field / event-field declarations are not directly declarable — each variable in the declaration
    // has its own symbol on the VariableDeclaratorSyntax. GetDeclaredSymbol on the field node returns
    // null, which previously left field/event names out of the index entirely (their references were
    // never captured → every field looked like an orphan).
    private static IEnumerable<ISymbol> DeclaredSymbolsOf(SemanticModel model, MemberDeclarationSyntax decl)
    {
        if (decl is BaseFieldDeclarationSyntax field)
        {
            foreach (var v in field.Declaration.Variables)
                if (model.GetDeclaredSymbol(v) is { } s) yield return s;
        }
        else if (model.GetDeclaredSymbol(decl) is { } s)
        {
            yield return s;
        }
    }

    // ── Shared scan helper ────────────────────────────────────────────────────

    private static void ScanTreeIntoIndex(
        SyntaxTree tree,
        CSharpCompilation compilation,
        HashSet<string> declaredNames,
        HashSet<string> declaredDocIds,
        Dictionary<string, List<ReferenceInfo>> refIndex,
        CancellationToken ct)
    {
        var root  = tree.GetRoot(ct);
        var model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);

        foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                     .Where(id => declaredNames.Contains(id.Identifier.Text)))
        {
            ct.ThrowIfCancellationRequested();

            var name = id.Identifier.Text;
            var info = model.GetSymbolInfo(id, ct);
            var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (resolved is null) continue;

            // Normalise to the declaration identity so the DocId matches declaredDocIds:
            //   • ReducedFrom: extension method called as receiver.Method() — Roslyn gives the
            //     reduced form (this-param hidden); ReducedFrom is the original static declaration.
            //   • OriginalDefinition: strips constructed generic type arguments.
            // Both normalizations must be applied: a generic extension method needs both.
            var declSym = resolved is IMethodSymbol { ReducedFrom: { } rf }
                ? (ISymbol)(rf.OriginalDefinition ?? rf)
                : (resolved.OriginalDefinition ?? resolved);
            var docId = declSym.GetDocumentationCommentId();
            // Null DocId means Roslyn resolved the symbol but couldn't produce a stable id
            // (common when a parameter type is an unresolved framework type, e.g. WebApplication).
            // Fall back to name-only matching rather than dropping the reference entirely.
            // Non-null DocId not in declaredDocIds → belongs to a different symbol → skip.
            if (docId is not null && !declaredDocIds.Contains(docId)) continue;

            var line = tree.GetLineSpan(id.Span).StartLinePosition.Line + 1;
            if (!refIndex.TryGetValue(name, out var list))
                refIndex[name] = list = [];
            var (encId, encName) = EnclosingOf(model, id.SpanStart, ct);
            list.Add(new ReferenceInfo(tree.FilePath, line, id.Parent?.ToString() ?? name)
            {
                Role              = ClassifyRole(id),
                EnclosingSymbolId = encId,
                EnclosingName     = encName,
                TargetDocId       = docId,
            });
        }

        // Extension method call sites: receiver.Foo() where Foo is defined as static Foo(this T, …).
        // GetSymbolInfo on just the IdentifierNameSyntax name node can return null for extension
        // methods because the receiver type is needed to resolve the `this`-parameter match.
        // Calling GetSymbolInfo on the full InvocationExpressionSyntax is more reliable.
        // ReducedFrom may be null when the binding is only partial (receiver or parameter types
        // not fully resolved); in that case the symbol IS already in its original static form.
        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (inv.Expression is not MemberAccessExpressionSyntax ma) continue;
            var name = ma.Name.Identifier.Text;
            if (!declaredNames.Contains(name)) continue;

            var info = model.GetSymbolInfo(inv, ct);
            var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (resolved is not IMethodSymbol { IsExtensionMethod: true } ms) continue;

            var origMethod = ms.ReducedFrom is { } rf
                ? (ISymbol)(rf.OriginalDefinition ?? rf)
                : (ISymbol)(ms.OriginalDefinition ?? ms);
            var docId = origMethod.GetDocumentationCommentId();
            if (docId is not null && !declaredDocIds.Contains(docId)) continue;

            var line = tree.GetLineSpan(inv.Span).StartLinePosition.Line + 1;
            // Skip if the IdentifierNameSyntax loop already captured this call site.
            if (refIndex.TryGetValue(name, out var existing) &&
                existing.Any(r => string.Equals(r.FilePath, tree.FilePath, StringComparison.OrdinalIgnoreCase)
                               && r.Line == line)) continue;

            if (!refIndex.TryGetValue(name, out var list))
                refIndex[name] = list = [];
            var (encId, encName) = EnclosingOf(model, inv.SpanStart, ct);
            list.Add(new ReferenceInfo(tree.FilePath, line, ma.ToString())
            {
                Role              = "call",
                EnclosingSymbolId = encId,
                EnclosingName     = encName,
                TargetDocId       = docId,
            });
        }

        // Generic type references — e.g. DedicatedWorker<T> in a base-class list — use
        // GenericNameSyntax whose Identifier is NOT an IdentifierNameSyntax child, so the
        // loop above misses them. Without this pass every generic abstract base class looks
        // like an orphan even when three concrete subclasses inherit from it.
        foreach (var gn in root.DescendantNodes().OfType<GenericNameSyntax>()
                     .Where(gn => declaredNames.Contains(gn.Identifier.Text)))
        {
            ct.ThrowIfCancellationRequested();

            var name = gn.Identifier.Text;
            var info = model.GetSymbolInfo(gn, ct);
            var resolved = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (resolved is null) continue;

            var declSym = resolved is IMethodSymbol { ReducedFrom: { } rf }
                ? (ISymbol)(rf.OriginalDefinition ?? rf)
                : (resolved.OriginalDefinition ?? resolved);
            var docId = declSym.GetDocumentationCommentId();
            if (docId is not null && !declaredDocIds.Contains(docId)) continue;

            var line = tree.GetLineSpan(gn.Span).StartLinePosition.Line + 1;
            if (!refIndex.TryGetValue(name, out var list))
                refIndex[name] = list = [];
            var (encId, encName) = EnclosingOf(model, gn.SpanStart, ct);
            list.Add(new ReferenceInfo(tree.FilePath, line, gn.Parent?.ToString() ?? name)
            {
                Role              = gn.Parent is BaseTypeSyntax ? "implements" : "typeref",
                EnclosingSymbolId = encId,
                EnclosingName     = encName,
                TargetDocId       = docId,
            });
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static SyntaxTree ParseFile(string path, string source) =>
        CSharpSyntaxTree.ParseText(
            SourceText.From(source, Encoding.UTF8),
            // DocumentationMode.Parse is set EXPLICITLY (P2): GetDocumentationCommentXml only returns
            // doc XML when the tree was parsed with doc comments enabled. Relying on the default is brittle.
            new CSharpParseOptions(languageVersion: LanguageVersion.Latest, documentationMode: DocumentationMode.Parse),
            path: path);

    /// <summary>P5: resolves the named member/type that contains a position (caller attribution),
    /// skipping past lambdas/local functions to the nearest referenceable owner.</summary>
    internal static (string? Id, string? Name) EnclosingOf(SemanticModel model, int position, CancellationToken ct)
    {
        var enc = model.GetEnclosingSymbol(position, ct);
        while (enc is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            enc = enc.ContainingSymbol;
        if (enc is null || enc.Kind == SymbolKind.Namespace) return (null, null);
        return (enc.GetDocumentationCommentId(), enc.Name);
    }

    /// <summary>P2: classifies how an identifier reference uses its target (call/new/write/typeref/read).</summary>
    internal static string ClassifyRole(SyntaxNode id)
    {
        var parent = id.Parent;
        switch (parent)
        {
            case ObjectCreationExpressionSyntax:
                return "new";
            case BaseTypeSyntax:
                return "implements";
            case AssignmentExpressionSyntax asg when asg.Left == id:
                return "write";
            case PostfixUnaryExpressionSyntax or PrefixUnaryExpressionSyntax:
                return "write";
            case InvocationExpressionSyntax inv when inv.Expression == id:
                return "call";
            case MemberAccessExpressionSyntax ma when ma.Name == id:
                if (ma.Parent is InvocationExpressionSyntax) return "call";
                if (ma.Parent is AssignmentExpressionSyntax a2 && a2.Left == ma) return "write";
                return "read";
            case ParameterSyntax or VariableDeclarationSyntax or TypeArgumentListSyntax or CastExpressionSyntax:
                return "typeref";
            default:
                return "read";
        }
    }

    // P0 soundness fix: the compilation must see the TARGET project's real reference closure, not just
    // the host runtime's TRUSTED_PLATFORM_ASSEMBLIES. TPA covers the BCL/shared framework; the project's
    // own build output (bin/) carries its NuGet + ProjectReference assemblies. We add both, deduped by
    // assembly simple-name (TPA wins) to avoid duplicate-identity conflicts. Best-effort: if the project
    // has never been built, we fall back to TPA-only (same as before) rather than fail.
    private static IEnumerable<MetadataReference> BuildReferences(string root)
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")?.ToString();
        if (tpa is not null)
            foreach (var path in tpa.Split(Path.PathSeparator))
                AddReference(path, refs, seen);

        foreach (var dll in EnumerateProjectOutputAssemblies(root))
            AddReference(dll, refs, seen);

        return refs;
    }

    private static void AddReference(string path, List<MetadataReference> refs, HashSet<string> seen)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (!seen.Add(name)) return; // dedupe by simple name — TPA added first wins for framework assemblies
        try { refs.Add(MetadataReference.CreateFromFile(path)); }
        catch { /* skip native/unloadable */ }
    }

    private static IEnumerable<string> EnumerateProjectOutputAssemblies(string root)
    {
        string[] binDirs;
        try { binDirs = Directory.GetDirectories(root, "bin", SearchOption.AllDirectories); }
        catch { yield break; }

        var sep = Path.DirectorySeparatorChar;
        int count = 0;
        foreach (var bin in binDirs)
        {
            if (bin.Contains($"{sep}node_modules{sep}", StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> dlls;
            try { dlls = Directory.EnumerateFiles(bin, "*.dll", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var d in dlls)
            {
                if (count++ >= 1500) yield break; // backstop against a pathological output tree
                yield return d;
            }
        }
    }

    private static string VersionOf(string source)
        => source.Length.ToString("X8") + "-" + ((uint)source.GetHashCode()).ToString("X8");

    private bool IsInExcludedDir(string path) => _excluded.IsExcluded(path, _root);

    public void Dispose() { }
}
