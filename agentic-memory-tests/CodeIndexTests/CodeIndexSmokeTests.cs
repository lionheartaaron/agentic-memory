using AgenticMemory.CodeIndex;

namespace AgenticMemoryTests.CodeIndexTests;

[Collection(CodeIndexCollection.Name)]
public class CodeIndexSmokeTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Backend_files_are_indexed_with_symbols()
    {
        var domain = await fixture.GetRecordAsync("Domain.cs");
        Assert.Equal("csharp", domain.Language);
        Assert.Equal(fixture.BackendSubProjectId, domain.SubProjectId);
        Assert.NotEmpty(domain.Symbols);
        Assert.Contains(domain.Symbols, s => s.Name == "Calculator");
    }

    [Fact]
    public async Task Reference_graph_was_built()
    {
        var count = await fixture.Repository.CountSymbolReferencesAsync(TestContext.Current.CancellationToken);
        Assert.True(count > 0, "expected the reference worker to have written symbol references");
    }

    [Fact]
    public void Both_sub_projects_were_discovered()
    {
        Assert.False(string.IsNullOrEmpty(fixture.BackendSubProjectId));
        Assert.False(string.IsNullOrEmpty(fixture.WebSubProjectId));
    }
}
