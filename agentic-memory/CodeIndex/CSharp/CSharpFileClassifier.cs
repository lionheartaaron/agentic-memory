using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AgenticMemory.CodeIndex.CSharp;

/// <summary>
/// Classifies a C# file into a semantic category that drives what the context formatter
/// prioritises and what domain patterns it scans for. Detection is ordered — first match wins.
/// </summary>
internal static class CSharpFileClassifier
{
    internal enum CsFileClass
    {
        Controller,
        Service,
        Repository,
        Entity,
        Middleware,
        Extension,
        BackgroundService,
        Configuration,
        Generic
    }

    internal static CsFileClass Classify(CompilationUnitSyntax root)
    {
        var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>().ToList();
        if (types.Count == 0) return CsFileClass.Generic;

        // Controller: [ApiController] or [Controller] attribute, or inherits ControllerBase / Controller
        foreach (var t in types)
        {
            if (HasAttr(t, "ApiController", "Controller") ||
                Inherits(t, "ControllerBase", "Controller"))
                return CsFileClass.Controller;
        }

        // Background service: inherits BackgroundService or IHostedService
        foreach (var t in types)
        {
            if (Inherits(t, "BackgroundService", "IHostedService"))
                return CsFileClass.BackgroundService;
        }

        // Middleware: implements IMiddleware or has InvokeAsync(HttpContext …)
        foreach (var t in types)
        {
            if (Inherits(t, "IMiddleware") ||
                t.Members.OfType<MethodDeclarationSyntax>()
                    .Any(m => m.Identifier.Text == "InvokeAsync" &&
                              m.ParameterList.Parameters.Count >= 1))
                return CsFileClass.Middleware;
        }

        // Extension: static class with at least one method whose first parameter has 'this'
        foreach (var t in types)
        {
            if (IsStatic(t) && HasExtensionMethod(t))
                return CsFileClass.Extension;
        }

        // Name-based heuristics
        foreach (var t in types)
        {
            var name = t.Identifier.Text;
            if (name.EndsWith("Repository", StringComparison.Ordinal) || Inherits(t, "IRepository"))
                return CsFileClass.Repository;
            if (name.EndsWith("Service", StringComparison.Ordinal))
                return CsFileClass.Service;
            if (name.EndsWith("Settings", StringComparison.Ordinal) ||
                name.EndsWith("Config", StringComparison.Ordinal) ||
                name.EndsWith("Options", StringComparison.Ordinal))
                return CsFileClass.Configuration;
        }

        // Entity heuristic: public class with only properties and no methods
        foreach (var t in types)
        {
            if (t is ClassDeclarationSyntax &&
                t.Members.OfType<PropertyDeclarationSyntax>().Count() >= 2 &&
                !t.Members.OfType<MethodDeclarationSyntax>().Any())
                return CsFileClass.Entity;
        }

        // DbContext inheritor is a repository (manages entity access)
        foreach (var t in types)
        {
            if (Inherits(t, "DbContext")) return CsFileClass.Repository;
        }

        return CsFileClass.Generic;
    }

    // ── Attribute / inheritance helpers ───────────────────────────────────────

    internal static bool HasAttr(TypeDeclarationSyntax t, params string[] names)
        => t.AttributeLists
            .SelectMany(al => al.Attributes)
            .Any(a =>
            {
                var n = a.Name.ToString();
                return names.Any(name =>
                    n == name ||
                    n == name + "Attribute" ||
                    n.EndsWith("." + name) ||
                    n.EndsWith("." + name + "Attribute"));
            });

    internal static bool Inherits(TypeDeclarationSyntax t, params string[] baseNames)
        => t.BaseList?.Types.Any(b =>
        {
            var s = b.Type.ToString();
            return baseNames.Any(n =>
                s == n ||
                s.StartsWith(n + "<", StringComparison.Ordinal) ||
                s.EndsWith("." + n, StringComparison.Ordinal));
        }) == true;

    private static bool IsStatic(TypeDeclarationSyntax t)
        => t.Modifiers.Any(m => m.Text == "static");

    private static bool HasExtensionMethod(TypeDeclarationSyntax t)
        => t.Members.OfType<MethodDeclarationSyntax>()
            .Any(m => m.ParameterList.Parameters.Count > 0 &&
                m.ParameterList.Parameters[0].Modifiers
                    .Any(mod => mod.Text == "this"));
}
