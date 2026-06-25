using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticMemory.CodeIndex.CSharp;

using static CSharpDomainPatterns;
using static CSharpFileClassifier;

/// <summary>
/// Produces LLM-ready context strings from Roslyn AST data.
/// Consumes semantic-model information (resolved types, call signatures) wherever available;
/// falls back to syntax-level strings when the model is null.
/// </summary>
internal static partial class CSharpContextFormatter
{
    internal static string Format(string fileName, CompilationUnitSyntax root, SemanticModel? model)
    {
        var fileClass = Classify(root);
        var sb = new StringBuilder();

        sb.Append("// FILE: ").Append(fileName).Append("  [").Append(ClassLabel(fileClass)).AppendLine("]");

        // Using namespace summary
        var usings = root.Usings
            .Select(u => u.Name?.ToString().Split('.').Last() ?? "")
            .Where(n => n.Length > 0)
            .Distinct()
            .ToList();
        if (usings.Count > 0)
        {
            const int showMax = 6;
            sb.Append("// imports: ").Append(string.Join(", ", usings.Take(showMax)));
            if (usings.Count > showMax)
                sb.Append(" (+").Append(usings.Count - showMax).Append(" more)");
            sb.AppendLine();
        }

        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        foreach (var type in types)
        {
            sb.AppendLine();
            switch (fileClass)
            {
                case CsFileClass.Controller:    FormatController(sb, type, model); break;
                case CsFileClass.Service:       FormatService(sb, type, model);    break;
                case CsFileClass.Repository:    FormatRepository(sb, type, model); break;
                case CsFileClass.Entity:        FormatEntity(sb, type, model);     break;
                case CsFileClass.BackgroundService: FormatService(sb, type, model);   break;
                case CsFileClass.Extension:     FormatExtension(sb, type, model);  break;
                case CsFileClass.Middleware:    FormatMiddleware(sb, type, model); break;
                default:                        FormatGeneric(sb, type, model);    break;
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Per-class formatters ──────────────────────────────────────────────────

    private static void FormatController(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var prefix = GetClassRoutePrefix(type);
        if (prefix is not null) sb.Append("// route: ").AppendLine(prefix);

        var deps = ExtractDependencies(type);
        if (deps.Count > 0)
            sb.Append("// depends: ").AppendLine(string.Join(", ", deps.Select(d => d.Type)));

        sb.AppendLine();

        var routes = ExtractRoutes(type);
        foreach (var r in routes)
        {
            var route = string.IsNullOrEmpty(r.Route) ? "/" : ("/" + r.Route.TrimStart('/'));
            var returnType = r.ReturnType;
            if (model is not null)
            {
                // Use semantic model to resolve the return type of the corresponding method
                var method = type.Members.OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(m => m.Identifier.Text == r.ActionName);
                if (method is not null)
                {
                    var typeInfo = model.GetTypeInfo(method.ReturnType);
                    if (typeInfo.Type is not null)
                        returnType = CleanReturnType(typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
                }
            }

            sb.Append("// ").Append(r.HttpMethod.PadRight(7))
              .Append(route.PadRight(22))
              .Append("→ ").Append(returnType.PadRight(26))
              .Append(r.ActionName);
            if (!string.IsNullOrEmpty(r.Parameters))
                sb.Append('(').Append(r.Parameters).Append(')');
            sb.AppendLine();
        }
    }

    private static void FormatService(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var deps = ExtractDependencies(type);
        if (deps.Count > 0)
            sb.Append("// depends: ").AppendLine(string.Join(", ", deps.Select(d => d.Type)));

        var publicMethods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();

        if (publicMethods.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("// PUBLIC");
            foreach (var m in publicMethods)
            {
                sb.Append("  ").Append(MethodSignature(m, model));
                var doc = DocSummary(m);
                if (doc is not null) sb.Append("  // ").Append(doc);
                sb.AppendLine();
            }
        }
    }

    private static void FormatRepository(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var entityTypes = GetDbSetEntityTypes(type);
        if (entityTypes.Count > 0)
            sb.Append("// entity: ").AppendLine(string.Join(", ", entityTypes));
        else
        {
            // Fall back to generic interface type argument
            var ifaceEntity = ExtractGenericTypeArg(type);
            if (ifaceEntity is not null) sb.Append("// entity: ").AppendLine(ifaceEntity);
        }

        var publicMethods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();

        sb.AppendLine();
        foreach (var m in publicMethods)
        {
            sb.Append("// ").Append(MethodSignature(m, model));
            var doc = DocSummary(m);
            if (doc is not null) sb.Append("  // ").Append(doc);
            sb.AppendLine();
        }
    }

    private static void FormatEntity(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var tableAttr = GetEfTableName(type);
        if (tableAttr is not null) sb.Append("// table: ").AppendLine(tableAttr);

        var props = type.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();

        if (props.Count > 0)
        {
            const int showMax = 8;
            var fieldList = props.Select(p =>
            {
                var typeStr = p.Type.ToString();
                if (model is not null)
                {
                    var ti = model.GetTypeInfo(p.Type);
                    if (ti.Type is not null)
                        typeStr = ti.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                }
                return $"{typeStr} {p.Identifier.Text}";
            }).ToList();
            sb.Append("// fields: ").Append(string.Join(", ", fieldList.Take(showMax)));
            if (fieldList.Count > showMax) sb.Append($" (+{fieldList.Count - showMax} more)");
            sb.AppendLine();
        }

        var methods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();
        foreach (var m in methods)
        {
            sb.Append("// ").Append(MethodSignature(m, model));
            var doc = DocSummary(m);
            if (doc is not null) sb.Append("  // ").Append(doc);
            sb.AppendLine();
        }
    }

    private static void FormatExtension(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        foreach (var m in type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword))))
        {
            sb.Append("// ").Append(MethodSignature(m, model));
            var doc = DocSummary(m);
            if (doc is not null) sb.Append("  // ").Append(doc);
            sb.AppendLine();
        }
    }

    private static void FormatMiddleware(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var deps = ExtractDependencies(type);
        if (deps.Count > 0)
            sb.Append("// depends: ").AppendLine(string.Join(", ", deps.Select(d => d.Type)));

        var invoke = type.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "InvokeAsync");
        if (invoke is not null)
            sb.Append("// pipeline: ").AppendLine(MethodSignature(invoke, model));
    }

    private static void FormatGeneric(StringBuilder sb, TypeDeclarationSyntax type, SemanticModel? model)
    {
        sb.AppendLine(TypeHeader(type));

        var publicMethods = type.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m.Modifiers.Any(mod => mod.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();
        var publicProps = type.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            .ToList();

        if (publicMethods.Count > 0 || publicProps.Count > 0)
        {
            sb.AppendLine("// PUBLIC");
            foreach (var p in publicProps)
            {
                sb.Append("  ").AppendLine(PropertySignature(p, model));
            }
            foreach (var m in publicMethods)
            {
                sb.Append("  ").Append(MethodSignature(m, model));
                var doc = DocSummary(m);
                if (doc is not null) sb.Append("  // ").Append(doc);
                sb.AppendLine();
            }
        }
    }

    // ── Signature builders ────────────────────────────────────────────────────

    private static string TypeHeader(TypeDeclarationSyntax type)
    {
        var keyword = type switch
        {
            ClassDeclarationSyntax     => "class",
            InterfaceDeclarationSyntax => "interface",
            RecordDeclarationSyntax r  => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            StructDeclarationSyntax    => "struct",
            _                          => "type"
        };

        var sb = new StringBuilder(keyword).Append(' ').Append(type.Identifier.Text);
        if (type.TypeParameterList is not null)
            sb.Append(type.TypeParameterList.ToString());

        var bases = type.BaseList?.Types.Take(3).Select(b => b.ToString()).ToList();
        if (bases?.Count > 0)
            sb.Append(" : ").Append(string.Join(", ", bases));

        return sb.ToString();
    }

    /// <summary>
    /// Builds a method signature string. Per §3.3: when the SemanticModel is available, parameter
    /// types and the return type are resolved through it, not taken from raw syntax strings. This
    /// is what correctly handles type aliases (ActionFn → () => void) and generic instantiations.
    /// </summary>
    private static string MethodSignature(MethodDeclarationSyntax method, SemanticModel? model)
    {
        string returnType;
        if (model is not null)
        {
            var typeInfo = model.GetTypeInfo(method.ReturnType);
            returnType = typeInfo.Type is not null
                ? CleanReturnType(typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                : CleanReturnType(method.ReturnType.ToString());
        }
        else
        {
            returnType = CleanReturnType(method.ReturnType.ToString());
        }

        var typeParams = method.TypeParameterList?.ToString() ?? "";
        var paramList = string.Join(", ", method.ParameterList.Parameters.Select(p => FormatParam(p, model)));

        return $"{method.Identifier.Text}{typeParams}({paramList}) → {returnType}";
    }

    private static string PropertySignature(PropertyDeclarationSyntax prop, SemanticModel? model)
    {
        string typeStr;
        if (model is not null)
        {
            var typeInfo = model.GetTypeInfo(prop.Type);
            typeStr = typeInfo.Type is not null
                ? typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                : prop.Type.ToString();
        }
        else
        {
            typeStr = prop.Type.ToString();
        }

        var accessors = prop.AccessorList?.Accessors.Select(a => a.Keyword.Text).Distinct() ?? [];
        var accessorStr = string.Join("; ", accessors);
        return accessorStr.Length > 0
            ? $"{typeStr} {prop.Identifier.Text} {{ {accessorStr} }}"
            : $"{typeStr} {prop.Identifier.Text}";
    }

    private static string FormatParam(ParameterSyntax p, SemanticModel? model)
    {
        var typeStr = p.Type?.ToString() ?? "?";
        if (model is not null && p.Type is not null)
        {
            var typeInfo = model.GetTypeInfo(p.Type);
            if (typeInfo.Type is not null)
                typeStr = typeInfo.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }
        return $"{typeStr} {p.Identifier.Text}";
    }

    // ── Doc-comment extraction ────────────────────────────────────────────────

    private static string? DocSummary(SyntaxNode node)
    {
        foreach (var trivia in node.GetLeadingTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;

            var text = trivia.ToString();
            var m = SummaryTagRegex().Match(text);
            if (m.Success)
            {
                return m.Groups[1].Value
                    .Split('\n')
                    .Select(l => l.TrimStart('/', ' ', '*').Trim())
                    .Where(l => l.Length > 0)
                    .FirstOrDefault();
            }

            // Plain // comment: first content line
            var line = text.Split('\n')
                .Select(l => l.TrimStart('/', ' ').Trim())
                .FirstOrDefault(l => l.Length > 0);
            return line;
        }
        return null;
    }

    // ── Misc helpers ──────────────────────────────────────────────────────────

    private static string? ExtractGenericTypeArg(TypeDeclarationSyntax type)
    {
        if (type.BaseList is null) return null;
        foreach (var b in type.BaseList.Types)
        {
            var s = b.Type.ToString();
            var m = GenericArgRegex().Match(s);
            if (m.Success) return m.Groups[1].Value;
        }
        return null;
    }

    private static string ClassLabel(CsFileClass cls) => cls switch
    {
        CsFileClass.Controller        => "cs-controller",
        CsFileClass.Service           => "cs-service",
        CsFileClass.Repository        => "cs-repository",
        CsFileClass.Entity            => "cs-entity",
        CsFileClass.Middleware        => "cs-middleware",
        CsFileClass.Extension         => "cs-extension",
        CsFileClass.BackgroundService => "cs-background",
        CsFileClass.Configuration     => "cs-configuration",
        _                             => "cs-generic"
    };

    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.Singleline)]
    private static partial Regex SummaryTagRegex();

    [GeneratedRegex(@"<([^,>]+)>")]
    private static partial Regex GenericArgRegex();
}
