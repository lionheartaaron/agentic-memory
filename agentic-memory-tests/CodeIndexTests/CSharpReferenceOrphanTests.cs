namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// The reference graph + orphan detection — the behaviours the recent fixes targeted:
/// constructors reached via instantiation, precise per-symbol attribution, same-file usage counting,
/// and test-linkage rollups.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class CSharpReferenceOrphanTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Referenced_class_is_not_orphan_and_has_cross_file_usage()
    {
        var calc = await fixture.GetSymbolRefAsync("Domain.cs", "Calculator");
        Assert.NotNull(calc);
        Assert.False(calc!.IsOrphan);
        Assert.True(calc.ExternalUseCount > 0);
        // Consumer.cs and CalculatorTests.cs both use it.
        Assert.True(calc.UsedBy.Select(u => u.FileId).Distinct().Count() >= 2);
    }

    [Fact]
    public async Task Unreferenced_public_method_is_an_orphan()
    {
        var unused = await fixture.GetSymbolRefAsync("Domain.cs", "Unused");
        Assert.NotNull(unused);
        Assert.True(unused!.IsOrphan);
        Assert.Equal(0, unused.ExternalUseCount);
    }

    [Fact]
    public async Task Constructor_reached_via_new_is_not_an_orphan()
    {
        // Regression: constructors are stored as "Widget()" but reached through `new Widget(...)`,
        // which resolves to the TYPE — they must be attributed via the type, not flagged dead.
        var ctor = await fixture.GetSymbolRefAsync("Domain.cs", "Widget()");
        Assert.NotNull(ctor);
        Assert.False(ctor!.IsOrphan);
        Assert.Contains(ctor.UsedBy, u => u.Role == "new");
    }

    [Fact]
    public async Task Constructor_on_a_truly_dead_type_is_still_flagged()
    {
        var ctor = await fixture.GetSymbolRefAsync("Domain.cs", "Lonely()");
        Assert.NotNull(ctor);
        Assert.True(ctor!.IsOrphan);
    }

    [Fact]
    public async Task Private_member_gets_no_reference_record()
    {
        var secret = await fixture.GetSymbolRefAsync("Domain.cs", "Secret");
        Assert.Null(secret);
    }

    [Fact]
    public async Task Test_file_usage_is_rolled_up_into_TestedBy()
    {
        var calc = await fixture.GetSymbolRefAsync("Domain.cs", "Calculator");
        Assert.NotNull(calc);
        Assert.NotEmpty(calc!.TestedByFileIds);
    }

    [Fact]
    public async Task File_level_fan_in_and_fan_out_are_derived()
    {
        var domain   = await fixture.GetRecordAsync("Domain.cs");
        var consumer = await fixture.GetRecordAsync("Consumer.cs");
        Assert.True(domain.FanIn > 0, "Domain.cs is depended on by Consumer/tests");
        Assert.True(consumer.FanOut > 0, "Consumer.cs depends on Domain.cs");
        Assert.Contains(consumer.DependsOnFileIds, id => id == domain.Id);
    }

    [Fact]
    public async Task File_level_orphan_rollups_are_set()
    {
        var domain = await fixture.GetRecordAsync("Domain.cs");
        Assert.True(domain.HasUnusedPublicSymbols);
        Assert.True(domain.OrphanSymbolCount > 0);
    }
}
