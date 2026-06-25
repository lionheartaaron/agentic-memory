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
public sealed class CSharpRoslynProvider : ICodeIntelligenceProvider
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
        if (!Directory.Exists(projectRoot))
        {
            _logger.LogWarning("C# project root does not exist: {Root}", projectRoot);
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

            string accessibility = GetAccessibility(decl);
            string? resolvedType = null;
            int line = tree.GetLineSpan(decl.Span).StartLinePosition.Line + 1;

            if (model is not null)
            {
                var symbol = model.GetDeclaredSymbol(decl);
                if (symbol is not null)
                {
                    accessibility = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
                    resolvedType = symbol switch
                    {
                        IMethodSymbol m => m.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IPropertySymbol p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        IFieldSymbol f => f.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        INamedTypeSymbol t => t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        _ => null
                    };
                }
            }

            results.Add(new SymbolInfo(name, kind, resolvedType, accessibility, line));
        }

        return results;
    }

    // ── Reference search ──────────────────────────────────────────────────────

    /// <summary>
    /// Finds all references to symbolName across the whole registered project. This uses the
    /// SemanticModel to confirm identity (not just text matching) — per §3.3: cross-file
    /// resolution requires the whole-program compilation.
    /// </summary>
    public async Task<IReadOnlyList<ReferenceInfo>> FindReferencesAsync(
        string filePath, string symbolName, CancellationToken ct = default)
    {
        var index = FindIndex(filePath);
        if (index?.Compilation is null)
        {
            _logger.LogDebug("FindReferences for {Symbol}: no project index; call RegisterProjectAsync first", symbolName);
            return [];
        }

        // Locate the target symbol in the compilation
        var targetSymbols = index.Compilation.GetSymbolsWithName(symbolName).ToList();
        if (targetSymbols.Count == 0) return [];
        var target = targetSymbols[0];

        var results = new List<ReferenceInfo>();

        foreach (var tree in index.Compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            var root = await tree.GetRootAsync(ct);
            var model = index.Compilation.GetSemanticModel(tree, ignoreAccessibility: true);

            foreach (var id in root.DescendantNodes().OfType<IdentifierNameSyntax>()
                .Where(id => id.Identifier.Text == symbolName))
            {
                var symbolInfo = model.GetSymbolInfo(id, ct);
                var resolved = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (resolved is null || !SymbolEqualityComparer.Default.Equals(resolved, target))
                    continue;

                var line = tree.GetLineSpan(id.Span).StartLinePosition.Line + 1;
                results.Add(new ReferenceInfo(
                    tree.FilePath,
                    line,
                    id.Parent?.ToString() ?? symbolName));
            }
        }

        return results;
    }

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
            ConstructorDeclarationSyntax c => (c.Identifier.Text + "()", "constructor"),
            _                            => (null, "unknown")
        };

    private static string GetAccessibility(MemberDeclarationSyntax decl)
    {
        var mods = decl.Modifiers;
        if (mods.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))    return "public";
        if (mods.Any(m => m.IsKind(SyntaxKind.PrivateKeyword)))   return "private";
        if (mods.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return "protected";
        if (mods.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))  return "internal";
        return "private"; // default for class members
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
