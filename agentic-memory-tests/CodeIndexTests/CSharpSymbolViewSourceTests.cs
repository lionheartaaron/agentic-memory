namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// "View source" correctness for C#: the stored [Line..EndLine] span must bracket each symbol's
/// COMPLETE definition for every declaration shape — expression-bodied, block-bodied, multi-line
/// signatures, attributes, generic constraints, block accessors, records, enums, structs,
/// constructors, fields and events. Each block body carries a unique VSMARK_* token so a truncated
/// slice is caught, and every snippet must be brace-balanced.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class CSharpSymbolViewSourceTests(CodeIndexFixture fixture)
{
    // name, '|'-separated substrings the View-source slice must contain (signature + body marker + close)
    [Theory]
    [InlineData("VsExpr",     "public int VsExpr() => 42;")]
    [InlineData("VsBlock",    "public int VsBlock()|VSMARK_BLOCK|}")]
    [InlineData("VsMultiSig", "public int VsMultiSig(|int a,|int b)|VSMARK_MULTISIG|}")]
    [InlineData("VsAttr",     "[Obsolete(|public int VsAttr()|VSMARK_ATTR|}")]
    [InlineData("VsGeneric",  "public T VsGeneric<T>(T input)|where T : class|VSMARK_GENERIC|}")]
    [InlineData("VsAuto",     "public int VsAuto { get; set; }")]
    [InlineData("VsProp",     "public int VsProp|VSMARK_PROP|set|}")]
    [InlineData("VsExprProp", "public int VsExprProp => _backing;")]
    [InlineData("VsConst",    "public const int VsConst = 7;")]
    [InlineData("VsEvent",    "event Action|VsEvent")]
    [InlineData("VsHost()",   "public VsHost()|VSMARK_CTOR|}")]
    [InlineData("VsArea",     "int VsArea();")]
    [InlineData("VsRecord",   "public record VsRecord(|VsFirst|VsSecond")]
    [InlineData("VsFirst",    "record VsRecord(|VsFirst")]
    [InlineData("VsEnum",     "public enum VsEnum|VsA = 1|VsB = 2|}")]
    [InlineData("VsStruct",   "public struct VsStruct|VsX|VSMARK_STRUCT|}")]
    public async Task View_source_slice_contains_the_complete_definition(string name, string expected)
    {
        var snippet = await fixture.ReadSymbolSourceAsync("ViewSource.cs", name);
        foreach (var part in expected.Split('|'))
            Assert.Contains(part, snippet);
    }

    // A complete declaration slice always has balanced braces — a truncated EndLine would not.
    [Theory]
    [InlineData("VsExpr")]
    [InlineData("VsBlock")]
    [InlineData("VsMultiSig")]
    [InlineData("VsAttr")]
    [InlineData("VsGeneric")]
    [InlineData("VsAuto")]
    [InlineData("VsProp")]
    [InlineData("VsExprProp")]
    [InlineData("VsHost()")]
    [InlineData("VsEnum")]
    [InlineData("VsStruct")]
    public async Task View_source_slice_is_brace_balanced(string name)
    {
        var snippet = await fixture.ReadSymbolSourceAsync("ViewSource.cs", name);
        Assert.Equal(snippet.Count(c => c == '{'), snippet.Count(c => c == '}'));
    }

    [Fact]
    public async Task Single_line_declarations_span_exactly_one_line()
    {
        foreach (var name in new[] { "VsExpr", "VsAuto", "VsExprProp", "VsConst", "VsEvent", "VsArea", "VsRecord" })
        {
            var snippet = await fixture.ReadSymbolSourceAsync("ViewSource.cs", name);
            Assert.DoesNotContain('\n', snippet);
        }
    }

    [Fact]
    public async Task Multi_line_block_ends_on_its_closing_brace()
    {
        foreach (var name in new[] { "VsBlock", "VsMultiSig", "VsAttr", "VsGeneric", "VsProp", "VsHost()", "VsEnum", "VsStruct" })
        {
            var snippet = (await fixture.ReadSymbolSourceAsync("ViewSource.cs", name)).TrimEnd();
            Assert.EndsWith("}", snippet);
        }
    }
}
