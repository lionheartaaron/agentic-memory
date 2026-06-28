using AgenticMemory.CodeIndex.CSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Provider-level tests for the reference graph + domain-fact building blocks that the
/// orphan detector and API-surface view stand on. Each builds a throwaway Roslyn project
/// from in-memory sources — no DB, no model download.
/// </summary>
public sealed class CSharpRoslynProviderTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    private string CreateProject(params (string Name, string Source)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "am-roslyn-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);
        foreach (var (name, src) in files)
            File.WriteAllText(Path.Combine(root, name), src);
        return root;
    }

    private static CSharpRoslynProvider NewProvider() => new(NullLogger<CSharpRoslynProvider>.Instance);

    [Fact]
    public async Task Constructor_is_reachable_via_type_instantiation()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateProject(
            ("Foo.cs",  "namespace P; public class Foo { public Foo() {} public void Bar() {} }"),
            ("User.cs", "namespace P; public class User { public void Use() { var f = new Foo(); f.Bar(); } }"));

        var provider = NewProvider();
        await provider.RegisterProjectAsync(root, ct);

        var refs = await provider.FindAllReferencesAsync(Path.Combine(root, "Foo.cs"), ["Foo", "Bar"], ct);

        // `new Foo()` resolves the identifier to the TYPE — this is what keeps the constructor alive,
        // so the orphan detector must look it up under the type name (not the munged "Foo()").
        Assert.True(refs.TryGetValue("Foo", out var fooRefs));
        Assert.Contains(fooRefs!, r => r.Role == "new");
        Assert.All(fooRefs!, r => Assert.NotNull(r.TargetDocId));

        Assert.True(refs.TryGetValue("Bar", out var barRefs));
        Assert.Contains(barRefs!, r => r.Role == "call");
    }

    [Fact]
    public async Task Same_named_members_on_different_types_get_distinct_identities()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateProject(
            ("A.cs", "namespace P; public class A { public void Handle() {} }"),
            ("B.cs", "namespace P; public class B { public void Handle() {} }"),
            ("C.cs", "namespace P; public class C { public void Use(A a, B b) { a.Handle(); b.Handle(); } }"));

        var provider = NewProvider();
        await provider.RegisterProjectAsync(root, ct);

        var refs = await provider.FindAllReferencesAsync(Path.Combine(root, "A.cs"), ["Handle"], ct);
        Assert.True(refs.TryGetValue("Handle", out var handleRefs));

        // Both call sites are recorded (no first-declaration-wins dropping that produced false orphans)
        Assert.Equal(2, handleRefs!.Count(r => r.Role == "call"));
        // ...and each is attributed to its own declaration so they can't steal each other's references.
        Assert.Equal(2, handleRefs!.Select(r => r.TargetDocId).Distinct().Count());
    }

    [Fact]
    public async Task Minimal_api_map_calls_become_http_endpoint_facts()
    {
        var ct = TestContext.Current.CancellationToken;
        var src = """
            namespace P;
            public static class Endpoints
            {
                public static void Map(object app)
                {
                    app.MapGet("/api/health", () => "ok");
                    app.MapPost("/api/items", () => "created");
                }
            }
            """;
        var root = CreateProject(("Endpoints.cs", src));

        var provider = NewProvider();
        await provider.RegisterProjectAsync(root, ct);

        var facts = await provider.ExtractDomainFactsAsync(Path.Combine(root, "Endpoints.cs"), ct);
        var endpoints = facts.Where(f => f.Kind == "http-endpoint").ToList();

        Assert.Contains(endpoints, f => f.Method == "GET"  && f.Route == "/api/health");
        Assert.Contains(endpoints, f => f.Method == "POST" && f.Route == "/api/items");
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
    }
}
