namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Both providers must speak ONE vocabulary so an MCP agent doesn't special-case per language:
/// lowercase kinds, the same accessibility words, and the same reference-role labels.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class CrossLanguageConsistencyTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Methods_share_the_lowercase_method_kind()
    {
        var csharp = await fixture.GetSymbolAsync("Domain.cs", "Add");      // C# method
        Assert.Equal("method", csharp.Kind);

        if (fixture.TypeScriptAvailable)
        {
            var ts = await fixture.GetSymbolAsync("models.ts", "add");      // TS method
            Assert.Equal("method", ts.Kind);
        }
    }

    [Fact]
    public async Task Interfaces_share_the_lowercase_interface_kind()
    {
        var csharp = await fixture.GetSymbolAsync("Domain.cs", "IClock");
        Assert.Equal("interface", csharp.Kind);

        if (fixture.TypeScriptAvailable)
        {
            var ts = await fixture.GetSymbolAsync("models.ts", "User");
            Assert.Equal("interface", ts.Kind);
        }
    }

    [Fact]
    public async Task Public_api_uses_public_in_both_languages()
    {
        var csharp = await fixture.GetSymbolAsync("Domain.cs", "Calculator");
        Assert.Equal("public", csharp.Accessibility);

        if (fixture.TypeScriptAvailable)
        {
            var ts = await fixture.GetSymbolAsync("models.ts", "Repository");
            Assert.Equal("public", ts.Accessibility);
        }
    }

    [Fact]
    public async Task Reference_roles_use_the_same_labels()
    {
        // 'call' on a C# method invocation and a TS method invocation should read identically.
        var csCall = await fixture.GetSymbolRefAsync("Domain.cs", "Add");
        Assert.NotNull(csCall);
        Assert.Contains(csCall!.UsedBy, u => u.Role == "call");

        if (fixture.TypeScriptAvailable)
        {
            var tsCall = await fixture.GetSymbolRefAsync("utils.ts", "formatName");
            Assert.NotNull(tsCall);
            Assert.Contains(tsCall!.UsedBy, u => u.Role == "call");
        }
    }
}
