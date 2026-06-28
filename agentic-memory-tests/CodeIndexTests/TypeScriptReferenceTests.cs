namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// TypeScript reference graph: cross-file + member-level + same-file references, roles, caller
/// attribution, orphans and the declaration-isn't-a-reference rule. Mirrors the C# reference tests.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypeScriptReferenceTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    [Fact]
    public async Task Cross_file_reference_resolves_with_role_and_caller()
    {
        RequireTypeScript();
        // App.tsx imports + calls formatName from utils.ts.
        var formatName = await Fixture.GetSymbolRefAsync("utils.ts", "formatName");
        Assert.NotNull(formatName);
        Assert.False(formatName!.IsOrphan);
        Assert.Contains(formatName.UsedBy, u => u.Role == "call");
        Assert.Contains(formatName.UsedBy, u => u.EnclosingName == "App");
    }

    [Fact]
    public async Task File_level_fan_in_is_derived()
    {
        RequireTypeScript();
        var utils = await Fixture.GetRecordAsync("utils.ts");
        Assert.True(utils.FanIn > 0, "App.tsx depends on utils.ts");
    }

    [Fact]
    public async Task Member_level_references_are_tracked_across_files()
    {
        RequireTypeScript();
        // consumer.ts calls repo.add(...) on a Repository<unknown> — the instantiated generic member
        // must resolve back to its declaration.
        var add = await Fixture.GetSymbolRefAsync("models.ts", "add");
        Assert.NotNull(add);
        Assert.False(add!.IsOrphan);
        Assert.Contains(add.UsedBy, u => u.Role == "call");
    }

    [Fact]
    public async Task New_and_typeref_roles_are_distinguished()
    {
        RequireTypeScript();
        var repo = await Fixture.GetSymbolRefAsync("models.ts", "Repository");
        Assert.NotNull(repo);
        Assert.Contains(repo!.UsedBy, u => u.Role == "new");      // new Repository()
        Assert.Contains(repo.UsedBy, u => u.Role == "typeref");   // : Repository<unknown>
    }

    [Fact]
    public async Task Same_file_reference_is_captured()
    {
        RequireTypeScript();
        // User is referenced only within models.ts (makeUser / loadUser return types).
        var user = await Fixture.GetSymbolRefAsync("models.ts", "User");
        Assert.NotNull(user);
        Assert.False(user!.IsOrphan);
        Assert.Equal(0, user.ExternalUseCount);
        Assert.All(user.UsedBy, u => Assert.Equal(user.DefinedInFileId, u.FileId));
    }

    [Fact]
    public async Task Imported_symbol_is_referenced_by_the_importing_file()
    {
        RequireTypeScript();
        var makeUser = await Fixture.GetSymbolRefAsync("models.ts", "makeUser");
        Assert.NotNull(makeUser);
        Assert.Contains(makeUser!.UsedBy, u => u.RelativePath.Replace('\\', '/').EndsWith("consumer.ts"));
    }

    [Fact]
    public async Task Unused_export_is_an_orphan()
    {
        RequireTypeScript();
        // identity() is an exported FUNCTION never referenced anywhere → a real dead-code candidate.
        var identity = await Fixture.GetSymbolRefAsync("models.ts", "identity");
        Assert.NotNull(identity);
        Assert.True(identity!.IsOrphan);
    }

    [Fact]
    public async Task Data_members_are_not_flagged_orphan()
    {
        RequireTypeScript();
        // An interface property and an exported const, both unreferenced, must NOT be flagged orphan —
        // they're reached via (de)serialization / dynamic access (the types.ts case).
        var prop = await Fixture.GetSymbolRefAsync("models.ts", "id"); // User.id, unreferenced
        Assert.NotNull(prop);
        Assert.False(prop!.IsOrphan);

        var variable = await Fixture.GetSymbolRefAsync("utils.ts", "VERSION"); // exported const, unreferenced
        Assert.NotNull(variable);
        Assert.False(variable!.IsOrphan);
    }

    [Fact]
    public async Task Default_exported_component_is_not_orphan()
    {
        RequireTypeScript();
        // Card.tsx is `export default function Card`, used via `import Card from "./Card"` + <Card/>.
        var card = await Fixture.GetSymbolRefAsync("Card.tsx", "Card");
        Assert.NotNull(card);
        Assert.False(card!.IsOrphan);
    }

    [Fact]
    public async Task Declaration_sites_are_not_counted_as_references()
    {
        RequireTypeScript();
        // formatName's own declaration in utils.ts must not appear as a self-reference.
        var formatName = await Fixture.GetSymbolRefAsync("utils.ts", "formatName");
        Assert.NotNull(formatName);
        Assert.DoesNotContain(formatName!.UsedBy, u => u.RelativePath.Replace('\\', '/').EndsWith("utils.ts"));
    }
}
