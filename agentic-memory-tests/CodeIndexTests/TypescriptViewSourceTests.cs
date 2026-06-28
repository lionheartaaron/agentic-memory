namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// "View source" correctness for TypeScript: the stored [Line..EndLine] span must bracket each
/// symbol's COMPLETE definition for every declaration shape — single-line and block arrow functions,
/// function declarations, multi-line signatures, async, interfaces, multi-line type unions, enums,
/// classes and their members. Block bodies carry VSMARK_* tokens and every slice must be brace-balanced.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypescriptViewSourceTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    [Theory]
    [InlineData("vsArrow",      "export const vsArrow = (a: number): number => a + 1;")]
    [InlineData("vsArrowBlock", "export const vsArrowBlock|VSMARK_ARROWBLOCK")]
    [InlineData("vsFunc",       "export function vsFunc(a: number): number|VSMARK_FUNC|}")]
    [InlineData("vsMultiSig",   "export function vsMultiSig(|a: number,|b: number|VSMARK_MULTISIG|}")]
    [InlineData("vsAsync",      "export async function vsAsync(): Promise<number>|VSMARK_ASYNC|}")]
    [InlineData("VsShape",      "export interface VsShape|area: number;|name: string;|}")]
    [InlineData("VsUnion",      "export type VsUnion|\"a\"|\"b\"")]
    [InlineData("VsEnum2",      "export enum VsEnum2|A = 1|B = 2|}")]
    [InlineData("VsClass",      "export class VsClass|VSMARK_METHOD|VSMARK_GETTER|}")]
    [InlineData("vsMethod",     "vsMethod(): number|VSMARK_METHOD|}")]
    [InlineData("vsGetter",     "get vsGetter(): number|VSMARK_GETTER|}")]
    [InlineData("VsDefault",    "export default function VsDefault(): number|VSMARK_DEFAULT|}")]
    public async Task View_source_slice_contains_the_complete_definition(string name, string expected)
    {
        RequireTypeScript();
        var snippet = await Fixture.ReadSymbolSourceAsync("viewsource.ts", name);
        foreach (var part in expected.Split('|'))
            Assert.Contains(part, snippet);
    }

    [Theory]
    [InlineData("vsArrow")]
    [InlineData("vsArrowBlock")]
    [InlineData("vsFunc")]
    [InlineData("vsMultiSig")]
    [InlineData("vsAsync")]
    [InlineData("VsShape")]
    [InlineData("VsUnion")]
    [InlineData("VsEnum2")]
    [InlineData("VsClass")]
    [InlineData("vsMethod")]
    [InlineData("vsGetter")]
    public async Task View_source_slice_is_brace_balanced(string name)
    {
        RequireTypeScript();
        var snippet = await Fixture.ReadSymbolSourceAsync("viewsource.ts", name);
        Assert.Equal(snippet.Count(c => c == '{'), snippet.Count(c => c == '}'));
    }

    [Fact]
    public async Task Single_line_declarations_span_exactly_one_line()
    {
        RequireTypeScript();
        var snippet = await Fixture.ReadSymbolSourceAsync("viewsource.ts", "vsArrow");
        Assert.DoesNotContain('\n', snippet);
    }

    [Fact]
    public async Task Multi_line_block_ends_on_its_closing_brace()
    {
        RequireTypeScript();
        foreach (var name in new[] { "vsArrowBlock", "vsFunc", "vsMultiSig", "vsAsync", "VsShape", "VsEnum2", "VsClass", "vsMethod", "vsGetter" })
        {
            var snippet = (await Fixture.ReadSymbolSourceAsync("viewsource.ts", name)).TrimEnd().TrimEnd(';');
            Assert.EndsWith("}", snippet);
        }
    }
}
