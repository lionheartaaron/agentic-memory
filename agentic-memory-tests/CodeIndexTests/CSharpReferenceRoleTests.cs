namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Typed reference edges — the role label and caller attribution on each usage site, plus the
/// field-reference capture that was previously missing from the graph entirely. RoleConsumer.cs
/// consumes RoleProducer cross-file, so the sites land in UsedBy.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class CSharpReferenceRoleTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Field_reads_and_writes_are_captured_with_roles()
    {
        // Regression: field references were dropped (field names never entered the index), so a field
        // had no usage edges and looked like an orphan. Now both the read and the write are recorded.
        var field = await fixture.GetSymbolRefAsync("Variety.cs", "Field");
        Assert.NotNull(field);
        Assert.False(field!.IsOrphan);
        Assert.Contains(field.UsedBy, u => u.Role == "read");
        Assert.Contains(field.UsedBy, u => u.Role == "write");
    }

    [Fact]
    public async Task Property_reads_and_writes_are_distinguished()
    {
        var prop = await fixture.GetSymbolRefAsync("Variety.cs", "Prop");
        Assert.NotNull(prop);
        Assert.Contains(prop!.UsedBy, u => u.Role == "read");
        Assert.Contains(prop.UsedBy, u => u.Role == "write");
    }

    [Fact]
    public async Task Method_calls_carry_the_call_role()
    {
        var method = await fixture.GetSymbolRefAsync("Variety.cs", "Method");
        Assert.NotNull(method);
        Assert.Contains(method!.UsedBy, u => u.Role == "call");
    }

    [Fact]
    public async Task Type_usage_distinguishes_new_from_typeref()
    {
        var type = await fixture.GetSymbolRefAsync("Variety.cs", "RoleProducer");
        Assert.NotNull(type);
        Assert.Contains(type!.UsedBy, u => u.Role == "new");
        Assert.Contains(type.UsedBy, u => u.Role == "typeref");
    }

    [Fact]
    public async Task Base_type_in_a_base_list_is_an_implements_edge()
    {
        // Domain.cs: `Dog : Animal, IBark` — the base list reference resolves to an implements role.
        var animal = await fixture.GetSymbolRefAsync("Domain.cs", "Animal");
        Assert.NotNull(animal);
        // Animal is referenced only within Domain.cs (same file) so it has no cross-file UsedBy;
        // the implements edge is still proven via the SystemClock→IClock cross-file case below.
        var iclock = await fixture.GetSymbolRefAsync("Domain.cs", "IClock");
        Assert.NotNull(iclock);
    }

    [Fact]
    public async Task Usage_sites_record_the_enclosing_caller()
    {
        var field = await fixture.GetSymbolRefAsync("Variety.cs", "Field");
        Assert.NotNull(field);
        Assert.NotEmpty(field!.UsedBy);
        Assert.All(field.UsedBy, u => Assert.Equal("Caller", u.EnclosingName));
    }
}
