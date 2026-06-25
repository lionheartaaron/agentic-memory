using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticMemory.CodeIndex.CSharp;

/// <summary>
/// Domain-pattern layer for the C# provider — per §4.3 of code-understanding-methodology.md.
///
/// No compiler API encodes framework conventions. These detectors run on top of real Roslyn
/// AST/type data produced by the compiler, but they are hand-rolled by necessity: nothing else
/// knows that "[HttpGet]" is an ASP.NET route marker, or that "DbSet&lt;T&gt;" is an EF Core entity set.
///
/// Families declared here correspond to the DomainPatternFamilies entries on TypeCapabilities:
///   aspnet-controller  — route prefix + per-action HTTP method, route, return type, parameters
///   aspnet-di          — constructor-injected dependencies (type + parameter name)
///   efcore             — DbSet entity types, table-name attributes
///   mediatr            — IRequest/IRequestHandler pairs
/// </summary>
internal static class CSharpDomainPatterns
{
    internal record RouteEntry(
        string HttpMethod,
        string Route,
        string ActionName,
        string ReturnType,
        string Parameters);

    internal record DependencyEntry(string Type, string ParameterName);

    // ── aspnet-controller ─────────────────────────────────────────────────────

    internal static IReadOnlyList<RouteEntry> ExtractRoutes(TypeDeclarationSyntax type)
    {
        var classRoute = GetRouteTemplate(type.AttributeLists) ?? "";
        var routes = new List<RouteEntry>();

        foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
        {
            if (IsPrivateOrProtected(method)) continue;

            string? httpMethod = null;
            string? methodRoute = null;

            foreach (var attr in method.AttributeLists.SelectMany(al => al.Attributes))
            {
                var attrName = attr.Name.ToString().Split('.').Last()
                    .TrimEnd("Attribute".ToCharArray());
                if (attrName is "HttpGet" or "HttpPost" or "HttpPut" or "HttpDelete" or "HttpPatch" or "HttpHead" or "HttpOptions")
                {
                    httpMethod = attrName["Http".Length..].ToUpperInvariant();
                    methodRoute = attr.ArgumentList?.Arguments.FirstOrDefault()
                        ?.Expression.ToString().Trim('"', '\'') ?? "";
                }
            }

            if (httpMethod is null) continue;

            var fullRoute = CombineRoutes(classRoute, methodRoute ?? "");
            var returnType = CleanReturnType(method.ReturnType.ToString());
            var parameters = string.Join(", ", method.ParameterList.Parameters
                .Where(p => !HasAttr(p, "FromServices"))
                .Select(p => $"{p.Type} {p.Identifier.Text}"));

            routes.Add(new RouteEntry(httpMethod, fullRoute, method.Identifier.Text, returnType, parameters));
        }

        return routes;
    }

    internal static string? GetClassRoutePrefix(TypeDeclarationSyntax type)
        => GetRouteTemplate(type.AttributeLists);

    // ── aspnet-di ─────────────────────────────────────────────────────────────

    internal static IReadOnlyList<DependencyEntry> ExtractDependencies(TypeDeclarationSyntax type)
    {
        var ctor = type.Members.OfType<ConstructorDeclarationSyntax>()
            .OrderByDescending(c => c.ParameterList.Parameters.Count)
            .FirstOrDefault();

        if (ctor is null) return [];

        return ctor.ParameterList.Parameters
            .Select(p => new DependencyEntry(p.Type?.ToString() ?? "?", p.Identifier.Text))
            .ToList();
    }

    // ── efcore ────────────────────────────────────────────────────────────────

    /// <summary>Returns the [Table("name")] override, or null if not present.</summary>
    internal static string? GetEfTableName(TypeDeclarationSyntax type)
    {
        foreach (var attr in type.AttributeLists.SelectMany(al => al.Attributes))
        {
            var name = attr.Name.ToString().Split('.').Last().TrimEnd("Attribute".ToCharArray());
            if (name is "Table")
                return attr.ArgumentList?.Arguments.FirstOrDefault()
                    ?.Expression.ToString().Trim('"', '\'');
        }
        return null;
    }

    /// <summary>Returns the entity types exposed as DbSet&lt;T&gt; on a DbContext class.</summary>
    internal static IReadOnlyList<string> GetDbSetEntityTypes(TypeDeclarationSyntax type)
    {
        var results = new List<string>();
        foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
        {
            var typeStr = prop.Type.ToString();
            if (typeStr.StartsWith("DbSet<", StringComparison.Ordinal) && typeStr.EndsWith(">"))
                results.Add(typeStr[6..^1]);
        }
        return results;
    }

    // ── mediatr ───────────────────────────────────────────────────────────────

    internal static bool IsMediatrRequest(TypeDeclarationSyntax type)
        => type.BaseList?.Types.Any(b =>
        {
            var s = b.Type.ToString();
            return s.StartsWith("IRequest", StringComparison.Ordinal) ||
                   s.StartsWith("ICommand", StringComparison.Ordinal) ||
                   s.StartsWith("IQuery", StringComparison.Ordinal);
        }) == true;

    internal static bool IsMediatrHandler(TypeDeclarationSyntax type)
        => type.BaseList?.Types.Any(b =>
            b.Type.ToString().StartsWith("IRequestHandler<", StringComparison.Ordinal)) == true;

    // ── Shared utilities ──────────────────────────────────────────────────────

    internal static string CleanReturnType(string returnType)
    {
        if (returnType.StartsWith("Task<", StringComparison.Ordinal) && returnType.EndsWith(">"))
            return returnType[5..^1];
        if (returnType.StartsWith("ValueTask<", StringComparison.Ordinal) && returnType.EndsWith(">"))
            return returnType[10..^1];
        if (returnType is "Task" or "ValueTask") return "void";
        return returnType;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string? GetRouteTemplate(SyntaxList<AttributeListSyntax> attrLists)
    {
        foreach (var attr in attrLists.SelectMany(al => al.Attributes))
        {
            var name = attr.Name.ToString().Split('.').Last().TrimEnd("Attribute".ToCharArray());
            if (name is "Route")
                return attr.ArgumentList?.Arguments.FirstOrDefault()
                    ?.Expression.ToString().Trim('"', '\'');
        }
        return null;
    }

    private static string CombineRoutes(string classRoute, string methodRoute)
    {
        if (string.IsNullOrEmpty(methodRoute)) return classRoute;
        if (string.IsNullOrEmpty(classRoute)) return methodRoute;
        return classRoute.TrimEnd('/') + "/" + methodRoute.TrimStart('/');
    }

    private static bool IsPrivateOrProtected(MethodDeclarationSyntax method)
        => method.Modifiers.Any(m =>
            m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.PrivateKeyword) ||
            m.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.ProtectedKeyword));

    private static bool HasAttr(ParameterSyntax param, string attrName)
        => param.AttributeLists.SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var n = a.Name.ToString().Split('.').Last().TrimEnd("Attribute".ToCharArray());
                return n == attrName;
            });
}
