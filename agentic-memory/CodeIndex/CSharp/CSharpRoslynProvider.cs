using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticMemory.CodeIndex.CSharp;

/// <summary>
/// ICodeIntelligenceProvider implementation for C# — §4 of code-understanding-methodology.md.
///
/// COMPILER: Roslyn CSharpCompilation runs directly in the same CLR as agentic-memory. No foreign
/// runtime or AddHostObject bridge is needed — this is the §4.1 simplification vs the TypeScript
/// provider, not a different rule.
///
/// SEMANTIC MODEL: Every symbol/type/reference query goes through SemanticModel.GetTypeInfo() or
/// GetSymbolInfo(), never through raw syntax node shapes alone. This is the §3.3 requirement
/// applied to C#: a Zustand-equivalent bug (type alias misclassification) would arise in C# if we
/// used syntax strings instead of resolved types.
///
/// WHOLE-PROGRAM VIEW: ProjectIndex builds one CSharpCompilation spanning all .cs files in the
/// project root. RegisterProjectAsync must be called before cross-file queries (FindReferences,
/// alias chains). Single-file ExtractContext falls back to per-file parsing when no project is
/// registered.
///
/// DOMAIN PATTERNS: aspnet-controller, aspnet-di, efcore, mediatr — all hand-rolled per §4.3,
/// because no compiler API encodes "this is an EF Core DbSet" or "this is a MediatR handler".
/// </summary>
public sealed class CSharpRoslynProvider : ICodeIntelligenceProvider, IBatchReferenceProvider
{
    private readonly ConcurrentDictionary<string, ProjectIndex> _projects
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<CSharpRoslynProvider> _logger;

    public CSharpRoslynProvider(ILogger<CSharpRoslynProvider> logger) => _logger = logger;

    public string ProviderType => "dotnet-csharp";

    public TypeCapabilities Capabilities => new(
        CompilerApi: "Roslyn CSharpCompilation / SemanticModel",
        DomainPatternFamilies: ["aspnet-controller", "aspnet-di", "efcore", "mediatr"]);

    public bool CanHandle(string filePath)
        => Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    // ── Project registration ──────────────────────────────────────────────────

    /// <summary>
    /// Builds a CSharpCompilation spanning every .cs file under projectRoot (excluding obj/bin).
    /// Must be called before FindReferencesAsync; ExtractContextAsync works without it but produces
    /// richer output when a whole-program compilation is available.
    /// </summary>
    public async Task RegisterProjectAsync(string projectRoot, CancellationToken ct = default)
    {
        // When passed a .csproj file path (from sub-project registration), derive the directory.
        if (File.Exists(projectRoot) &&
            projectRoot.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            projectRoot = Path.GetDirectoryName(projectRoot)!;
        }

        if (!Directory.Exists(projectRoot))
        {
            _logger.LogWarning("C# project root does not exist: {Root}", projectRoot);
            return;
        }

        // Skip rebuild if already registered — callers invoke this on every file; rebuilding
        // the entire compilation each time would be O(N²) in files.
        if (_projects.ContainsKey(projectRoot))
        {
            _logger.LogDebug("C# Roslyn index already registered for {Root}", projectRoot);
            return;
        }

        var index = new ProjectIndex(projectRoot);
        try
        {
            await index.BuildAsync(ct);
            _projects[projectRoot] = index;
            _logger.LogInformation("C# Roslyn index built for {Root} ({Count} files)",
                projectRoot, index.Compilation?.SyntaxTrees.Length ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to build Roslyn compilation for {Root}; per-file parsing will be used", projectRoot);
            index.Dispose();
        }
    }

    // ── Context extraction ────────────────────────────────────────────────────

    public async Task<string> ExtractContextAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return string.Empty;

        var source = await File.ReadAllTextAsync(filePath, ct);
        var index = FindIndex(filePath);

        SyntaxTree tree;
        SemanticModel? model = null;

        if (index is not null)
        {
            // Use the compilation's copy of this tree so the SemanticModel has whole-project
            // context and can resolve cross-file types (the §3.3 whole-program requirement).
            tree = index.GetOrParseSyntaxTree(filePath, source);
            model = index.GetSemanticModel(filePath);
        }
        else
        {
            tree = CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
                path: filePath);
        }

        var root = (CompilationUnitSyntax)await tree.GetRootAsync(ct);
        return CSharpContextFormatter.Format(Path.GetFileName(filePath), root, model);
    }

    // ── Symbol queries ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SymbolInfo>> GetSymbolsAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return [];

        var source = await File.ReadAllTextAsync(filePath, ct);
        var index = FindIndex(filePath);
        SyntaxTree tree;
        SemanticModel? model = null;

        if (index is not null)
        {
            tree = index.GetOrParseSyntaxTree(filePath, source);
            model = index.GetSemanticModel(filePath);
        }
        else
        {
            tree = CSharpSyntaxTree.ParseText(source, path: filePath);
        }

        var root = await tree.GetRootAsync(ct);
        var results = new List<SymbolInfo>();

        foreach (var decl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            var (name, kind) = GetDeclNameAndKind(decl);
            if (name is null) continue;

            var span = tree.GetLineSpan(decl.Span);
            string accessibility = GetAccessibility(decl);
            string? resolvedType = null;
            int line    = span.StartLinePosition.Line + 1;
            int endLine = span.EndLinePosition.Line + 1;

            var info = new SymbolInfo(name, kind, resolvedType, accessibility, line) { EndLine = endLine };

            if (model is not null)
            {
                var symbol = GetDeclaredMemberSymbol(model, decl);
                if (symbol is not null)
                {
                    accessibility = AccessibilityOf(symbol);
                    resolvedType = symbol switch
                    {
                        IMethodSymbol m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IFieldSymbol f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IEventSymbol e => e.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        _ => null
                    };
                    info = EnrichFromSymbol(info with { Type = resolvedType, Accessibility = accessibility }, symbol, decl);
                }
            }

            results.Add(info);

            // Positional record parameters synthesize public properties that are NOT themselves
            // MemberDeclarationSyntax — surface them so record DTOs expose their fields (incl. any
            // [property: …] validation attributes), which the wire-contract use case depends on.
            if (model is not null &&
                decl is RecordDeclarationSyntax { ParameterList: { } plist } recDecl &&
                model.GetDeclaredSymbol(recDecl) is INamedTypeSymbol recType)
            {
                foreach (var prm in plist.Parameters)
                {
                    if (recType.GetMembers(prm.Identifier.Text).OfType<IPropertySymbol>().FirstOrDefault() is not { } prop)
                        continue;
                    var pspan = prm.GetLocation().GetLineSpan();
                    var pInfo = new SymbolInfo(
                        prop.Name, "property",
                        prop.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        AccessibilityOf(prop),
                        pspan.StartLinePosition.Line + 1)
                    { EndLine = pspan.EndLinePosition.Line + 1 };
                    results.Add(EnrichFromSymbol(pInfo, prop, recDecl));
                }
            }
        }

        return results;
    }

    // FieldDeclarationSyntax / EventFieldDeclarationSyntax are not directly declarable — their symbol
    // lives on the variable declarator. Calling GetDeclaredSymbol on the field node returns null,
    // which silently drops field type / modifiers / constant values. Resolve via the declarator.
    private static ISymbol? GetDeclaredMemberSymbol(SemanticModel model, MemberDeclarationSyntax decl) =>
        decl is BaseFieldDeclarationSyntax field
            ? (field.Declaration.Variables.Count > 0 ? model.GetDeclaredSymbol(field.Declaration.Variables[0]) : null)
            : model.GetDeclaredSymbol(decl);

    private static string AccessibilityOf(ISymbol symbol) => symbol.DeclaredAccessibility switch
    {
        Accessibility.ProtectedOrInternal  => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => symbol.DeclaredAccessibility.ToString().ToLowerInvariant()
    };

    // ── Reference search ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds all references to symbolName. Uses the pre-built inverted index when available
    /// (O(1) lookup); falls back to a full compilation traversal otherwise.
    /// </summary>
    public Task<IReadOnlyList<ReferenceInfo>> FindReferencesAsync(
        string filePath, string symbolName, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index?.Compilation is null)
        {
            _logger.LogDebug("FindReferences for {Symbol}: no project index", symbolName);
            return Task.FromResult<IReadOnlyList<ReferenceInfo>>([]);
        }

        if (index.IsIndexBuilt)
        {
            var snapshot = index.QueryReferences([symbolName]);
            return Task.FromResult(
                snapshot.TryGetValue(symbolName, out var refs) ? refs : (IReadOnlyList<ReferenceInfo>)[]);
        }

        return FindReferencesFallbackAsync(filePath, symbolName, index, ct);
    }

    /// <summary>
    /// Finds references for all named symbols. Uses the pre-built inverted index when available
    /// (O(symbolNames.Count) lookup); falls back to a single-pass AST traversal otherwise.
    /// </summary>
    public Task<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>> FindAllReferencesAsync(
        string filePath,
        IReadOnlyList<string> symbolNames,
        CancellationToken ct = default)
    {
        if (symbolNames.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>>(
                new Dictionary<string, IReadOnlyList<ReferenceInfo>>());

        var index = FindIndex(filePath);
        if (index?.Compilation is null)
        {
            _logger.LogDebug("FindAllReferences: no project index for {File}", Path.GetFileName(filePath));
            return Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>>(
                new Dictionary<string, IReadOnlyList<ReferenceInfo>>());
        }

        // Fast path: pre-built index is an O(symbolNames.Count) dictionary lookup.
        if (index.IsIndexBuilt)
            return Task.FromResult(index.QueryReferences(symbolNames));

        // Fallback: single-pass AST traversal (index not yet ready).
        return FindAllReferencesFallbackAsync(filePath, symbolNames, index, ct);
    }

    // Fallback implementations (used only before the index is built) ──────────

    private static async Task<IReadOnlyList<ReferenceInfo>> FindReferencesFallbackAsync(
        string filePath, string symbolName, ProjectIndex index, CancellationToken ct)
    {
        var multi = await FindAllReferencesFallbackAsync(filePath, [symbolName], index, ct);
        return multi.TryGetValue(symbolName, out var r) ? r : [];
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<ReferenceInfo>>> FindAllReferencesFallbackAsync(
        string filePath, IReadOnlyList<string> symbolNames, ProjectIndex index, CancellationToken ct)
    {
        var symbolSet = new HashSet<string>(symbolNames, StringComparer.Ordinal);

        // Every DocId for the requested names (all overloads / same-named declarations), so a site is
        // attributed to the exact symbol it resolved to — mirrors the pre-built index's semantics.
        var declaredDocIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in symbolNames)
            foreach (var sym in index.Compilation!.GetSymbolsWithName(name))
            {
                var d = sym.GetDocumentationCommentId();
                if (d is not null) declaredDocIds.Add(d);
            }

        if (declaredDocIds.Count == 0)
            return new Dictionary<string, IReadOnlyList<ReferenceInfo>>();

        var results = new Dictionary<string, List<ReferenceInfo>>(StringComparer.Ordinal);

        foreach (var tree in index.Compilation!.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var root  = tree.GetRoot(ct);
            var model = index.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);

            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                         .Where(id => symbolSet.Contains(id.Identifier.Text)))
            {
                var name     = id.Identifier.Text;
                var symInfo  = model.GetSymbolInfo(id, ct);
                var resolved = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault();
                if (resolved is null) continue;
                var docId = (resolved.OriginalDefinition ?? resolved).GetDocumentationCommentId();
                if (docId is null || !declaredDocIds.Contains(docId)) continue;

                var line = tree.GetLineSpan(id.Span).StartLinePosition.Line + 1;
                if (!results.TryGetValue(name, out var list)) results[name] = list = [];
                var (encId, encName) = ProjectIndex.EnclosingOf(model, id.SpanStart, ct);
                list.Add(new ReferenceInfo(tree.FilePath, line, id.Parent?.ToString() ?? name)
                {
                    Role = ProjectIndex.ClassifyRole(id), EnclosingSymbolId = encId, EnclosingName = encName,
                    TargetDocId = docId,
                });
            }
        }

        return results.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<ReferenceInfo>)kv.Value,
            StringComparer.Ordinal);
    }

    // ── Semantic metadata (Phase 4) ───────────────────────────────────────────

    public async Task<SemanticMetadata> ExtractSemanticMetadataAsync(
        string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return SemanticMetadata.Empty;

        var source = await File.ReadAllTextAsync(filePath, ct);
        var index = FindIndex(filePath);
        SyntaxTree tree;
        SemanticModel? model = null;

        if (index is not null)
        {
            tree = index.GetOrParseSyntaxTree(filePath, source);
            model = index.GetSemanticModel(filePath);
        }
        else
        {
            tree = CSharpSyntaxTree.ParseText(source,
                new CSharpParseOptions(languageVersion: LanguageVersion.Latest),
                path: filePath);
        }

        var root = (CompilationUnitSyntax)await tree.GetRootAsync(ct);

        // Imports: top-level using directives, excluding aliases
        var imports = root.Usings
            .Where(u => u.Alias is null)
            .Select(u => u.Name?.ToString() ?? "")
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();

        // Type hierarchy: "ClassName : Base1, IFace1" for each declared type with a base list
        var typeHierarchy = new List<string>();
        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (typeDecl.BaseList is null || typeDecl.BaseList.Types.Count == 0) continue;

            var typeName = typeDecl.Identifier.Text;
            var bases = new List<string>();

            foreach (var bt in typeDecl.BaseList.Types)
            {
                if (model is not null)
                {
                    var sym = model.GetTypeInfo(bt.Type, ct).Type;
                    if (sym is not null)
                    {
                        bases.Add(sym.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                        continue;
                    }
                }
                bases.Add(bt.Type.ToString());
            }

            typeHierarchy.Add($"{typeName} : {string.Join(", ", bases)}");
        }

        // Domain tags: file-class + framework pattern tags
        var domainTags = new List<string>();
        var fileClass = CSharpFileClassifier.Classify(root);
        domainTags.Add(fileClass.ToString().ToLowerInvariant());

        var allTypes = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        if (allTypes.Any(t => CSharpDomainPatterns.ExtractRoutes(t).Count > 0))
            domainTags.Add("aspnet-controller");
        if (allTypes.Any(t => CSharpDomainPatterns.IsMediatrRequest(t) || CSharpDomainPatterns.IsMediatrHandler(t)))
            domainTags.Add("mediatr");
        if (allTypes.Any(t => CSharpDomainPatterns.GetDbSetEntityTypes(t).Count > 0))
            domainTags.Add("efcore");
        if (allTypes.Any(t => CSharpDomainPatterns.ExtractDependencies(t).Count > 1))
            domainTags.Add("aspnet-di");

        domainTags = domainTags.Distinct().ToList();

        // Diagnostic summary: condensed error/warning count from real compiler
        var diagnosticSummary = "";
        try
        {
            var diags = await GetDiagnosticsAsync(filePath, ct);
            if (diags.Count > 0)
            {
                var errors   = diags.Count(d => d.Severity == CodeDiagnosticSeverity.Error);
                var warnings = diags.Count(d => d.Severity == CodeDiagnosticSeverity.Warning);
                var parts = new List<string>();
                if (errors   > 0) parts.Add($"{errors} error{(errors > 1 ? "s" : "")}");
                if (warnings > 0) parts.Add($"{warnings} warning{(warnings > 1 ? "s" : "")}");
                if (parts.Count > 0) diagnosticSummary = string.Join(", ", parts);
            }
        }
        catch { /* diagnostics are best-effort; never fail metadata extraction */ }

        return new SemanticMetadata(domainTags, imports, typeHierarchy, diagnosticSummary);
    }

    // ── Domain facts (P1 promotion — semantic, never the syntactic CSharpDomainPatterns hacks) ──

    public async Task<IReadOnlyList<DomainFact>> ExtractDomainFactsAsync(
        string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return [];

        var index = FindIndex(filePath);
        // Domain facts are persisted durably, so we only emit them when a SemanticModel is available
        // (§5: do not launder syntactic guesses into records). No project registered => no facts.
        if (index?.Compilation is null) return [];

        var source = await File.ReadAllTextAsync(filePath, ct);
        var tree   = index.GetOrParseSyntaxTree(filePath, source);
        var model  = index.GetSemanticModel(filePath);
        if (model is null) return [];

        var root  = (CompilationUnitSyntax)await tree.GetRootAsync(ct);
        var facts = new List<DomainFact>();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            ct.ThrowIfCancellationRequested();
            if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol typeSym) continue;
            ExtractEndpoints(typeSym, facts);
            ExtractDiInjections(typeSym, facts);
            ExtractEfEntities(typeSym, facts);
            ExtractTypeRelations(typeSym, facts);
            ExtractMediatr(typeSym, facts);
        }

        ExtractConfigAndSinks(root, model, facts);
        return facts;
    }

    private static readonly HashSet<string> ConfigMethods = new(StringComparer.Ordinal)
        { "GetValue", "GetConnectionString", "GetSection", "GetRequiredSection" };

    // Declared §4.3 security-sink family: known-dangerous APIs matched by symbol identity.
    private static readonly Dictionary<string, string> SinkApis = new(StringComparer.Ordinal)
    {
        ["Start"] = "process", ["CreateInstance"] = "reflection",
        ["Load"]  = "reflection", ["LoadFrom"] = "reflection",
        ["ExecuteSqlRaw"] = "sql", ["ExecuteSqlRawAsync"] = "sql", ["FromSqlRaw"] = "sql",
    };

    private static void ExtractConfigAndSinks(CompilationUnitSyntax root, SemanticModel model, List<DomainFact> facts)
    {
        var tree = root.SyntaxTree;

        foreach (var inv in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = (inv.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text
                       ?? (inv.Expression as IdentifierNameSyntax)?.Identifier.Text;
            if (name is null) continue;
            var line = tree.GetLineSpan(inv.Span).StartLinePosition.Line + 1;

            if (ConfigMethods.Contains(name))
            {
                var sym = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                var ns  = sym?.ContainingType?.ContainingNamespace?.ToDisplayString() ?? "";
                if (sym is null || ns.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal))
                {
                    var key = FirstStringArg(inv);
                    if (key is not null)
                        facts.Add(new DomainFact { Kind = "config-key", Name = key, Method = name, Line = line });
                }
            }

            if (MinimalApiVerb.TryGetValue(name, out var verb))
            {
                // Minimal APIs always pass the route template as the first argument literal.
                var route = FirstStringArg(inv);
                if (route is not null)
                {
                    var mapSym = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                    var mapNs  = mapSym?.ContainingType?.ContainingNamespace?.ToDisplayString();
                    var isAspNet = mapNs is not null &&
                                   mapNs.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal);
                    // The ASP.NET shared-framework assemblies are usually absent from a framework-
                    // dependent app's bin closure, so MapGet often won't resolve. Rather than miss the
                    // whole minimal-API surface, accept an unresolved Map{Verb} whose first argument is
                    // a "/route" literal — a high-confidence syntactic signal for this declared family.
                    if (isAspNet || (mapSym is null && route.StartsWith('/')))
                        facts.Add(new DomainFact
                        {
                            Kind   = "http-endpoint",
                            Method = verb,
                            Route  = route,
                            Name   = route,
                            Line   = line,
                        });
                }
            }

            if (SinkApis.TryGetValue(name, out var sinkKind))
            {
                var sym       = model.GetSymbolInfo(inv).Symbol as IMethodSymbol;
                var container = sym?.ContainingType?.Name;
                var ok = name switch
                {
                    "Start"               => container == "Process",
                    "CreateInstance"      => container == "Activator",
                    "Load" or "LoadFrom"  => container == "Assembly",
                    _                     => true, // FromSqlRaw/ExecuteSqlRaw* are EF-specific, unambiguous
                };
                if (ok)
                    facts.Add(new DomainFact
                    {
                        Kind    = "security-sink",
                        Name    = sinkKind,
                        TypeRef = sym is not null ? $"{sym.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{name}" : name,
                        Line    = line,
                    });
            }
        }

        foreach (var ea in root.DescendantNodes().OfType<ElementAccessExpressionSyntax>())
        {
            var t = model.GetTypeInfo(ea.Expression).Type;
            if (t is null) continue;
            if (t.Name == "IConfiguration" || t.AllInterfaces.Any(i => i.Name == "IConfiguration"))
            {
                var key = ea.ArgumentList.Arguments.Count == 1
                    ? (ea.ArgumentList.Arguments[0].Expression as LiteralExpressionSyntax)?.Token.ValueText : null;
                if (key is not null)
                    facts.Add(new DomainFact
                    {
                        Kind = "config-key", Name = key, Method = "indexer",
                        Line = tree.GetLineSpan(ea.Span).StartLinePosition.Line + 1,
                    });
            }
        }
    }

    private static string? FirstStringArg(InvocationExpressionSyntax inv)
    {
        var arg = inv.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        return arg is LiteralExpressionSyntax lit && lit.IsKind(SyntaxKind.StringLiteralExpression)
            ? lit.Token.ValueText : null;
    }

    private static readonly Dictionary<string, string> HttpAttrVerb = new(StringComparer.Ordinal)
    {
        ["HttpGetAttribute"]    = "GET",  ["HttpPostAttribute"]   = "POST",
        ["HttpPutAttribute"]    = "PUT",  ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"]  = "PATCH",["HttpHeadAttribute"]   = "HEAD",
        ["HttpOptionsAttribute"]= "OPTIONS",
    };

    // Minimal-API endpoint mapping — `app.MapGet("/route", handler)`. Declared §4.3 domain family
    // (the same status as the attribute-controller detector): a hand-recognised framework convention,
    // not reimplemented compiler analysis.
    private static readonly Dictionary<string, string> MinimalApiVerb = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    private static void ExtractEndpoints(INamedTypeSymbol type, List<DomainFact> facts)
    {
        var prefix = RouteTemplateOf(type.GetAttributes(), type.Name);

        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind != MethodKind.Ordinary || m.DeclaredAccessibility != Accessibility.Public)
                continue;

            string? verb = null, methodRoute = null;
            foreach (var a in m.GetAttributes())
            {
                if (a.AttributeClass?.Name is { } an && HttpAttrVerb.TryGetValue(an, out var v))
                {
                    verb = v;
                    methodRoute = a.ConstructorArguments.Length > 0
                        ? a.ConstructorArguments[0].Value as string : null;
                }
            }
            if (verb is null) continue;

            var ps = m.Parameters
                .Where(p => !p.GetAttributes().Any(x => x.AttributeClass?.Name == "FromServicesAttribute"))
                .Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}")
                .ToList();

            facts.Add(new DomainFact
            {
                Kind      = "http-endpoint",
                Method    = verb,
                Route     = CombineRoutes(prefix, methodRoute),
                Name      = m.Name,
                TypeRef   = UnwrapAwaitable(m.ReturnType) ?? m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                OwnerType = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Items     = ps,
                Line      = LineOf(m),
            });
        }
    }

    private static void ExtractDiInjections(INamedTypeSymbol type, List<DomainFact> facts)
    {
        var ctor = type.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared && c.Parameters.Length > 0)
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        if (ctor is null) return;

        foreach (var p in ctor.Parameters)
        {
            // Classic DI edge: an injected abstraction (interface or abstract type), not a value/DTO param.
            if (p.Type.TypeKind != TypeKind.Interface && !p.Type.IsAbstract) continue;

            facts.Add(new DomainFact
            {
                Kind      = "di-injection",
                Name      = p.Name,
                TypeRef   = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                OwnerType = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Line      = LineOf(ctor),
            });
        }
    }

    private static void ExtractEfEntities(INamedTypeSymbol type, List<DomainFact> facts)
    {
        foreach (var p in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (p.Type is not INamedTypeSymbol { IsGenericType: true, Name: "DbSet" } dbset ||
                dbset.TypeArguments.Length != 1 ||
                dbset.ContainingNamespace?.ToDisplayString() != "Microsoft.EntityFrameworkCore")
                continue;

            var entity = dbset.TypeArguments[0];
            var table  = (entity as INamedTypeSymbol)?.GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.Name == "TableAttribute")
                ?.ConstructorArguments.FirstOrDefault().Value as string;

            facts.Add(new DomainFact
            {
                Kind      = "ef-entity",
                Name      = entity.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                TypeRef   = table,
                OwnerType = type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Line      = LineOf(p),
            });
        }
    }

    private static void ExtractTypeRelations(INamedTypeSymbol type, List<DomainFact> facts)
    {
        var min  = SymbolDisplayFormat.MinimallyQualifiedFormat;
        var self = type.ToDisplayString(min);

        if (type.BaseType is { } bt &&
            bt.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType))
            facts.Add(new DomainFact { Kind = "type-relation", Method = "extends", Name = bt.ToDisplayString(min), OwnerType = self, Line = LineOf(type) });

        foreach (var i in type.AllInterfaces)
        {
            var ns = i.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns.StartsWith("System", StringComparison.Ordinal) || ns.StartsWith("Microsoft", StringComparison.Ordinal))
                continue; // skip framework-interface noise; keep project-relevant edges
            facts.Add(new DomainFact { Kind = "type-relation", Method = "implements", Name = i.ToDisplayString(min), OwnerType = self, Line = LineOf(type) });
        }
    }

    private static void ExtractMediatr(INamedTypeSymbol type, List<DomainFact> facts)
    {
        var min  = SymbolDisplayFormat.MinimallyQualifiedFormat;
        var self = type.ToDisplayString(min);

        foreach (var i in type.AllInterfaces)
        {
            if (i.ContainingNamespace?.ToDisplayString() != "MediatR") continue;

            switch (i.Name)
            {
                case "IRequest" or "ICommand" or "IQuery":
                    facts.Add(new DomainFact
                    {
                        Kind    = "mediatr-message",
                        Name    = self,
                        TypeRef = i.TypeArguments.Length == 1 ? i.TypeArguments[0].ToDisplayString(min) : null,
                        Line    = LineOf(type),
                    });
                    break;
                case "INotification":
                    facts.Add(new DomainFact { Kind = "mediatr-message", Name = self, Line = LineOf(type) });
                    break;
                case "IRequestHandler" or "INotificationHandler":
                    var items = i.TypeArguments.Select(a => a.ToDisplayString(min)).ToList();
                    facts.Add(new DomainFact
                    {
                        Kind      = "mediatr-handler",
                        Name      = self,
                        OwnerType = items.Count > 0 ? items[0] : null, // the message type this handler serves
                        Items     = items,
                        Line      = LineOf(type),
                    });
                    break;
            }
        }
    }

    private static string? RouteTemplateOf(System.Collections.Immutable.ImmutableArray<AttributeData> attrs, string typeName)
    {
        var route = attrs
            .FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "RoutePrefixAttribute")
            ?.ConstructorArguments.FirstOrDefault().Value as string;
        // ASP.NET [controller] token expands to the controller name minus the "Controller" suffix.
        if (route is not null && route.Contains("[controller]"))
        {
            var ctrl = typeName.EndsWith("Controller", StringComparison.Ordinal)
                ? typeName[..^"Controller".Length] : typeName;
            route = route.Replace("[controller]", ctrl);
        }
        return route;
    }

    private static string CombineRoutes(string? classRoute, string? methodRoute)
    {
        if (string.IsNullOrEmpty(methodRoute)) return classRoute ?? "";
        if (string.IsNullOrEmpty(classRoute))  return methodRoute;
        return classRoute.TrimEnd('/') + "/" + methodRoute.TrimStart('/');
    }

    private static int LineOf(ISymbol s)
        => (s.Locations.FirstOrDefault(l => l.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? 0) + 1;

    // ── Diagnostics ───────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DiagnosticInfo>> GetDiagnosticsAsync(
        string filePath, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index?.Compilation is null) return [];

        var tree = index.Compilation.SyntaxTrees
            .FirstOrDefault(t => string.Equals(t.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (tree is null) return [];

        var model = index.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);
        var diags = model.GetDiagnostics(cancellationToken: ct);

        return diags
            .Where(d => d.Severity != DiagnosticSeverity.Hidden)
            .Select(d => new DiagnosticInfo(
                MapSeverity(d.Severity),
                d.Id,
                d.GetMessage(),
                d.Location.GetLineSpan().StartLinePosition.Line + 1))
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProjectIndex? FindIndex(string filePath)
    {
        foreach (var (root, index) in _projects)
        {
            if (filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return null;
    }

    private static (string? Name, string Kind) GetDeclNameAndKind(MemberDeclarationSyntax decl) =>
        decl switch
        {
            ClassDeclarationSyntax c     => (c.Identifier.Text,    "class"),
            InterfaceDeclarationSyntax i => (i.Identifier.Text,    "interface"),
            RecordDeclarationSyntax r    => (r.Identifier.Text,    "record"),
            StructDeclarationSyntax s    => (s.Identifier.Text,    "struct"),
            EnumDeclarationSyntax e      => (e.Identifier.Text,    "enum"),
            MethodDeclarationSyntax m    => (m.Identifier.Text,    "method"),
            PropertyDeclarationSyntax p  => (p.Identifier.Text,    "property"),
            FieldDeclarationSyntax f     => (f.Declaration.Variables.FirstOrDefault()?.Identifier.Text, "field"),
            EventDeclarationSyntax ev    => (ev.Identifier.Text, "event"),
            EventFieldDeclarationSyntax ef => (ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text, "event"),
            ConstructorDeclarationSyntax c => (c.Identifier.Text + "()", "constructor"),
            _                            => (null, "unknown")
        };

    // Syntax-only fallback (used when no SemanticModel is available). The semantic path derives
    // accessibility from DeclaredAccessibility directly; this must still express the combined
    // modifiers the old first-match scan dropped (protected internal / private protected).
    private static string GetAccessibility(MemberDeclarationSyntax decl)
    {
        var mods = decl.Modifiers;
        bool pub  = mods.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
        bool priv = mods.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));
        bool prot = mods.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword));
        bool intl = mods.Any(m => m.IsKind(SyntaxKind.InternalKeyword));

        if (pub) return "public";
        if (priv && prot) return "private protected";
        if (prot && intl) return "protected internal";
        if (prot) return "protected";
        if (intl) return "internal";
        if (priv) return "private";
        // No explicit modifier: top-level type declarations default to internal; members to private.
        return decl is BaseTypeDeclarationSyntax ? "internal" : "private";
    }

    // ── P1 Tier 0: structured symbol-shape extraction (every field is one read off the resolved symbol) ──

    private static SymbolInfo EnrichFromSymbol(SymbolInfo info, ISymbol symbol, MemberDeclarationSyntax decl)
    {
        var (depMsg, isDep) = ObsoleteOf(symbol);
        info = info with
        {
            ContainingTypeFullName = symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            ContainingNamespace    = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            SymbolDocId            = symbol.GetDocumentationCommentId(),
            Modifiers              = GetModifiers(symbol),
            IsStatic               = symbol.IsStatic,
            IsAbstract             = symbol.IsAbstract,
            IsSealed               = symbol.IsSealed,
            IsVirtual              = symbol.IsVirtual,
            IsOverride             = symbol.IsOverride,
            Attributes             = MapAttributes(symbol.GetAttributes()),
            IsDeprecated           = isDep,
            DeprecationMessage     = depMsg,
            ValidationRules        = ExtractValidationRules(symbol),
            NlDescription          = BuildNlDescription(symbol),
        };

        info = ApplyDocComment(info, symbol);

        switch (symbol)
        {
            case IMethodSymbol m:
                info = info with
                {
                    IsAsync             = m.IsAsync,
                    Parameters          = MapParameters(m.Parameters),
                    ReturnTypeUnwrapped = UnwrapAwaitable(m.ReturnType),
                    IsAwaitable         = m.ReturnType is INamedTypeSymbol { Name: "Task" or "ValueTask" or "IAsyncEnumerable" },
                    IsAsyncEnumerable   = m.ReturnType is INamedTypeSymbol { Name: "IAsyncEnumerable" },
                    UsesLock            = decl.DescendantNodes().OfType<LockStatementSyntax>().Any(),
                    UsesInterlocked     = decl.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "Interlocked"),
                    BlocksOnAsync       = decl.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                                              .Any(ma => ma.Name.Identifier.Text is "Result" or "Wait" or "GetResult"),
                    TypeParameters      = MapTypeParameters(m.TypeParameters),
                    OverriddenSymbolId  = m.IsOverride ? m.OverriddenMethod?.GetDocumentationCommentId() : null,
                    ThrownExceptions    = DirectThrows(decl),
                };
                break;

            case INamedTypeSymbol { TypeKind: TypeKind.Enum } e:
                info = info with
                {
                    EnumMembers        = MapEnumMembers(e),
                    EnumUnderlyingType = e.EnumUnderlyingType?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    IsFlags            = e.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute"),
                };
                break;

            case INamedTypeSymbol t when t.TypeKind is TypeKind.Class or TypeKind.Struct or TypeKind.Interface:
                var min = SymbolDisplayFormat.MinimallyQualifiedFormat;
                info = info with
                {
                    ImplementsIDisposable      = ImplementsInterface(t, "IDisposable", "System"),
                    ImplementsIAsyncDisposable = ImplementsInterface(t, "IAsyncDisposable", "System"),
                    IsBackgroundService        = ImplementsInterface(t, "IHostedService", "Microsoft.Extensions.Hosting"),
                    HasStaticMutableState      = t.GetMembers().OfType<IFieldSymbol>()
                                                     .Any(f => f.IsStatic && !f.IsConst && !f.IsReadOnly),
                    TypeParameters             = MapTypeParameters(t.TypeParameters),
                    BaseChain                  = BaseChainOf(t),
                    Interfaces                 = t.AllInterfaces.Select(i => i.ToDisplayString(min)).Distinct().ToList(),
                };
                break;

            case IFieldSymbol f when f.HasConstantValue:
                info = info with { ConstantValue = FormatConstant(f.ConstantValue) };
                break;
        }

        return info;
    }

    private static bool ImplementsInterface(INamedTypeSymbol type, string ifaceName, string ns)
        => type.AllInterfaces.Any(i =>
            i.Name == ifaceName && i.ContainingNamespace?.ToDisplayString() == ns);

    private static List<string> GetModifiers(ISymbol s)
    {
        var m = new List<string>();
        if (s.IsStatic)   m.Add("static");
        if (s.IsAbstract) m.Add("abstract");
        if (s.IsSealed)   m.Add("sealed");
        if (s.IsVirtual)  m.Add("virtual");
        if (s.IsOverride) m.Add("override");
        if (s is IMethodSymbol { IsAsync: true }) m.Add("async");
        if (s is IFieldSymbol f)
        {
            if (f.IsReadOnly) m.Add("readonly");
            if (f.IsConst)    m.Add("const");
            if (f.IsRequired) m.Add("required");
        }
        if (s is IPropertySymbol { IsRequired: true }) m.Add("required");
        if (s.DeclaringSyntaxReferences.Length > 1) m.Add("partial");
        return m;
    }

    private static List<ParameterRecord> MapParameters(System.Collections.Immutable.ImmutableArray<IParameterSymbol> ps)
        => ps.Select(p => new ParameterRecord
        {
            Name               = p.Name,
            Type               = p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Ordinal            = p.Ordinal,
            IsOptional         = p.IsOptional,
            DefaultValue       = p.HasExplicitDefaultValue ? FormatConstant(p.ExplicitDefaultValue) : null,
            RefKind            = p.RefKind == RefKind.None ? "none" : p.RefKind.ToString().ToLowerInvariant(),
            IsParams           = p.IsParams,
            NullableAnnotation = p.NullableAnnotation switch
            {
                NullableAnnotation.Annotated    => "annotated",
                NullableAnnotation.NotAnnotated => "notannotated",
                _                               => "none"
            },
        }).ToList();

    private static List<EnumMemberRecord> MapEnumMembers(INamedTypeSymbol e)
        => e.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.IsConst)
            .Select(f => new EnumMemberRecord
            {
                Name  = f.Name,
                Value = f.ConstantValue is null ? null : Convert.ToInt64(f.ConstantValue),
            })
            .ToList();

    private static List<AttributeRecord> MapAttributes(System.Collections.Immutable.ImmutableArray<AttributeData> attrs)
        => attrs
            .Where(a => a.AttributeClass is not null)
            .Select(a => new AttributeRecord
            {
                Name            = a.AttributeClass!.Name,
                ConstructorArgs = a.ConstructorArguments.Select(FormatTypedConstant).ToList(),
                NamedArgs       = a.NamedArguments.ToDictionary(n => n.Key, n => FormatTypedConstant(n.Value)),
            })
            .ToList();

    private static string FormatTypedConstant(TypedConstant tc)
    {
        const int Cap = 200;
        string s = tc.Kind switch
        {
            TypedConstantKind.Array => "[" + string.Join(", ", tc.Values.Select(FormatTypedConstant)) + "]",
            TypedConstantKind.Type  => (tc.Value as ITypeSymbol)?.ToDisplayString() ?? "typeof(?)",
            _                       => FormatConstant(tc.Value),
        };
        return s.Length > Cap ? s[..Cap] : s;
    }

    private static string FormatConstant(object? value) => value switch
    {
        null      => "null",
        string st => "\"" + (st.Length > 120 ? st[..120] : st) + "\"",
        bool b    => b ? "true" : "false",
        _         => value.ToString() ?? "",
    };

    private static string? UnwrapAwaitable(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol { IsGenericType: true } n &&
            n.TypeArguments.Length == 1 &&
            n.Name is "Task" or "ValueTask" or "IAsyncEnumerable")
            return n.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        return null;
    }

    private static List<TypeParameterRecord> MapTypeParameters(
        System.Collections.Immutable.ImmutableArray<ITypeParameterSymbol> tps)
    {
        var min = SymbolDisplayFormat.MinimallyQualifiedFormat;
        return tps.Select(tp =>
        {
            var constraints = new List<string>();
            if (tp.HasReferenceTypeConstraint) constraints.Add("class");
            if (tp.HasValueTypeConstraint)     constraints.Add("struct");
            if (tp.HasConstructorConstraint)   constraints.Add("new()");
            foreach (var c in tp.ConstraintTypes) constraints.Add(c.ToDisplayString(min));
            return new TypeParameterRecord
            {
                Name        = tp.Name,
                Constraints = constraints,
                Variance    = tp.Variance == VarianceKind.None ? null : tp.Variance.ToString().ToLowerInvariant(),
            };
        }).ToList();
    }

    // P5: directly-thrown exception types (syntactic — `throw new X(...)`). NOT what the method
    // transitively throws (that needs the call graph); labeled "direct" in the schema docs.
    private static List<string> DirectThrows(MemberDeclarationSyntax decl)
    {
        var thrown = new List<string>();
        foreach (var node in decl.DescendantNodes())
        {
            ObjectCreationExpressionSyntax? oce = node switch
            {
                ThrowStatementSyntax { Expression: ObjectCreationExpressionSyntax o } => o,
                ThrowExpressionSyntax { Expression: ObjectCreationExpressionSyntax o } => o,
                _ => null,
            };
            if (oce is not null)
            {
                var name = oce.Type.ToString();
                if (!thrown.Contains(name)) thrown.Add(name);
            }
        }
        return thrown;
    }

    private static List<string> BaseChainOf(INamedTypeSymbol t)
    {
        var min = SymbolDisplayFormat.MinimallyQualifiedFormat;
        var chain = new List<string>();
        for (var b = t.BaseType; b is not null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
            chain.Add(b.ToDisplayString(min));
        return chain;
    }

    // ── P2: intent & contracts ──────────────────────────────────────────────────

    private static (string? Message, bool IsDeprecated) ObsoleteOf(ISymbol symbol)
    {
        var attr = symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ObsoleteAttribute");
        if (attr is null) return (null, false);
        var msg = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;
        return (msg, true);
    }

    private static string BuildNlDescription(ISymbol symbol)
    {
        var min = SymbolDisplayFormat.MinimallyQualifiedFormat;
        return symbol switch
        {
            IMethodSymbol m   => $"{m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type.ToDisplayString(min)} {p.Name}"))}) -> {m.ReturnType.ToDisplayString(min)}",
            IPropertySymbol p => $"{p.Name}: {p.Type.ToDisplayString(min)}",
            IFieldSymbol f    => $"{f.Name}: {f.Type.ToDisplayString(min)}",
            INamedTypeSymbol t => $"{t.TypeKind.ToString().ToLowerInvariant()} {t.Name}",
            _ => symbol.Name,
        };
    }

    private static readonly string[] ValidationNs = ["System.ComponentModel.DataAnnotations"];

    private static List<ValidationRuleRecord> ExtractValidationRules(ISymbol symbol)
    {
        var rules = new List<ValidationRuleRecord>();

        static void Collect(string member, System.Collections.Immutable.ImmutableArray<AttributeData> attrs, List<ValidationRuleRecord> into)
        {
            foreach (var a in attrs)
            {
                var ns = a.AttributeClass?.ContainingNamespace?.ToDisplayString();
                if (ns is null || !ValidationNs.Contains(ns)) continue;
                var rule = a.AttributeClass!.Name.Replace("Attribute", "");
                var args = new Dictionary<string, string>();
                for (int i = 0; i < a.ConstructorArguments.Length; i++)
                    args[$"arg{i}"] = FormatTypedConstant(a.ConstructorArguments[i]);
                foreach (var na in a.NamedArguments) args[na.Key] = FormatTypedConstant(na.Value);
                into.Add(new ValidationRuleRecord { Member = member, Rule = rule, Args = args });
            }
        }

        switch (symbol)
        {
            case IMethodSymbol m:
                foreach (var p in m.Parameters) Collect(p.Name, p.GetAttributes(), rules);
                break;
            case IPropertySymbol or IFieldSymbol:
                Collect(symbol.Name, symbol.GetAttributes(), rules);
                break;
        }
        return rules;
    }

    private static SymbolInfo ApplyDocComment(SymbolInfo info, ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return info;

        try
        {
            var member = System.Xml.Linq.XDocument.Parse(xml).Root;
            if (member is null) return info;

            static string? Norm(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return null;
                var collapsed = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return collapsed.Length > 500 ? collapsed[..500] : collapsed;
            }

            var paramDocs = new Dictionary<string, string>();
            foreach (var p in member.Elements("param"))
            {
                var name = p.Attribute("name")?.Value;
                var val  = Norm(p.Value);
                if (name is not null && val is not null) paramDocs[name] = val;
            }

            var exceptions = member.Elements("exception")
                .Select(e => e.Attribute("cref")?.Value?.TrimStart('T', ':'))
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .Distinct()
                .ToList();

            return info with
            {
                DocSummary           = Norm(member.Element("summary")?.Value),
                DocRemarks           = Norm(member.Element("remarks")?.Value),
                ReturnsDoc           = Norm(member.Element("returns")?.Value),
                ParamDocs            = paramDocs,
                DocumentedExceptions = exceptions,
            };
        }
        catch { return info; }
    }

    private static CodeDiagnosticSeverity MapSeverity(DiagnosticSeverity s) => s switch
    {
        DiagnosticSeverity.Error   => CodeDiagnosticSeverity.Error,
        DiagnosticSeverity.Warning => CodeDiagnosticSeverity.Warning,
        DiagnosticSeverity.Info    => CodeDiagnosticSeverity.Info,
        _                          => CodeDiagnosticSeverity.Hidden
    };

    public ValueTask DisposeAsync()
    {
        foreach (var index in _projects.Values) index.Dispose();
        _projects.Clear();
        return ValueTask.CompletedTask;
    }
}
