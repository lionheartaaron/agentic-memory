namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// TypeScript symbol-shape extraction via the in-process TypeScript compiler (ClearScript/V8),
/// mirroring <see cref="CSharpSymbolTests"/>. Gated on the provider initialising in this environment.
/// </summary>
[Collection(CodeIndexCollection.Name)]
public class TypeScriptSymbolTests(CodeIndexFixture fixture) : TypeScriptTestBase(fixture)
{
    [Fact]
    public async Task Top_level_functions_and_variables_are_extracted()
    {
        RequireTypeScript();
        var utils = await Fixture.GetRecordAsync("utils.ts");
        Assert.Equal("typescript", utils.Language);
        Assert.Contains(utils.Symbols, s => s.Name == "formatName" && s.Kind == "function");
        Assert.Contains(utils.Symbols, s => s.Name == "VERSION" && s.Kind == "variable");
    }

    [Fact]
    public async Task React_component_is_indexed()
    {
        RequireTypeScript();
        var app = await Fixture.GetRecordAsync("App.tsx");
        Assert.Contains(app.Symbols, s => s.Name == "App");
    }

    [Fact]
    public async Task Web_files_belong_to_the_typescript_sub_project()
    {
        RequireTypeScript();
        var ct = TestContext.Current.CancellationToken;
        var web = await Fixture.Repository.GetBySubProjectAsync(Fixture.WebSubProjectId, ct);
        Assert.NotEmpty(web);
        Assert.All(web, r => Assert.Equal("typescript", r.Language));
    }

    [Fact]
    public async Task Symbol_kinds_use_the_canonical_lowercase_vocabulary()
    {
        RequireTypeScript();
        var models = await Fixture.GetRecordAsync("models.ts");
        Assert.Contains(models.Symbols, s => s.Name == "User" && s.Kind == "interface");
        Assert.Contains(models.Symbols, s => s.Name == "Identifier" && s.Kind == "type-alias");
        Assert.Contains(models.Symbols, s => s.Name == "Repository" && s.Kind == "class");
        Assert.Contains(models.Symbols, s => s.Name == "Role" && s.Kind == "enum");
        Assert.Contains(models.Symbols, s => s.Name == "makeUser" && s.Kind == "function");
    }

    [Fact]
    public async Task Class_and_interface_members_are_extracted_with_containing_type()
    {
        RequireTypeScript();
        var models = await Fixture.GetRecordAsync("models.ts");

        var add = models.Symbols.SingleOrDefault(s => s.Name == "add");
        Assert.NotNull(add);
        Assert.Equal("method", add!.Kind);
        Assert.Equal("Repository", add.ContainingTypeFullName);
        Assert.Contains(add.Parameters, p => p.Name == "item");

        Assert.Contains(models.Symbols, s => s.Name == "all" && s.Kind == "method");
        Assert.Contains(models.Symbols, s => s.Name == "id" && s.Kind == "property" && s.ContainingTypeFullName == "User");
    }

    [Fact]
    public async Task Static_member_is_flagged()
    {
        RequireTypeScript();
        var empty = await Fixture.GetSymbolAsync("models.ts", "empty");
        Assert.Equal("property", empty.Kind);
        Assert.True(empty.IsStatic);
    }

    [Fact]
    public async Task Getter_is_surfaced_as_a_property()
    {
        RequireTypeScript();
        var count = await Fixture.GetSymbolAsync("models.ts", "count");
        Assert.Equal("property", count.Kind);
        Assert.Equal("Repository", count.ContainingTypeFullName);
    }

    [Fact]
    public async Task Private_member_keeps_private_accessibility()
    {
        RequireTypeScript();
        var items = await Fixture.GetSymbolAsync("models.ts", "items");
        Assert.Equal("private", items.Accessibility);
    }

    [Fact]
    public async Task Exported_symbols_are_public()
    {
        RequireTypeScript();
        var formatName = await Fixture.GetSymbolAsync("utils.ts", "formatName");
        Assert.Equal("public", formatName.Accessibility);
    }

    [Fact]
    public async Task Enum_members_are_captured()
    {
        RequireTypeScript();
        var role = await Fixture.GetSymbolAsync("models.ts", "Role");
        Assert.Equal("enum", role.Kind);
        Assert.Equal(1, role.EnumMembers.Single(m => m.Name == "Admin").Value);
        Assert.Equal(2, role.EnumMembers.Single(m => m.Name == "Member").Value);
    }

    [Fact]
    public async Task Generic_class_and_function_expose_type_parameters()
    {
        RequireTypeScript();
        var repo = await Fixture.GetSymbolAsync("models.ts", "Repository");
        Assert.Contains(repo.TypeParameters, t => t.Name == "T");

        var identity = await Fixture.GetSymbolAsync("models.ts", "identity");
        Assert.Contains(identity.TypeParameters, t => t.Name == "T");
    }

    [Fact]
    public async Task Function_type_reports_the_return_type()
    {
        RequireTypeScript();
        var formatName = await Fixture.GetSymbolAsync("utils.ts", "formatName");
        Assert.Equal("string", formatName.Type); // return type, not the whole signature
    }

    [Fact]
    public async Task Async_function_unwraps_its_promise()
    {
        RequireTypeScript();
        var loadUser = await Fixture.GetSymbolAsync("models.ts", "loadUser");
        Assert.True(loadUser.IsAsync);
        Assert.True(loadUser.IsAwaitable);
        Assert.Equal("User", loadUser.ReturnTypeUnwrapped); // Promise<User> -> User, like Task<T> -> T
    }

    [Fact]
    public async Task Deprecated_member_is_flagged()
    {
        RequireTypeScript();
        var legacy = await Fixture.GetSymbolAsync("models.ts", "legacy");
        Assert.True(legacy.IsDeprecated);
    }

    [Fact]
    public async Task Jsdoc_summary_is_extracted()
    {
        RequireTypeScript();
        var user = await Fixture.GetSymbolAsync("models.ts", "User");
        Assert.Equal("A user in the system.", user.DocSummary);

        var add = await Fixture.GetSymbolAsync("models.ts", "add");
        Assert.Equal("Adds an item to the repository.", add.DocSummary);
    }

    [Fact]
    public async Task Default_exported_function_is_extracted()
    {
        RequireTypeScript();
        var def = await Fixture.GetSymbolAsync("viewsource.ts", "VsDefault");
        Assert.Equal("function", def.Kind);
        Assert.Equal("public", def.Accessibility);
    }

    [Fact]
    public async Task Namespace_const_enum_and_abstract_class_are_extracted()
    {
        RequireTypeScript();
        var exotic = await Fixture.GetRecordAsync("exotic.ts");
        Assert.Contains(exotic.Symbols, s => s.Name == "ExNs" && s.Kind == "namespace");
        Assert.Contains(exotic.Symbols, s => s.Name == "ExConstEnum" && s.Kind == "enum");
        Assert.Contains(exotic.Symbols, s => s.Name == "ExAbstract" && s.Kind == "class");
        Assert.Contains(exotic.Symbols, s => s.Name == "doThing" && s.Kind == "method");
    }

    [Fact]
    public async Task Namespace_members_destructured_and_anonymous_default_are_extracted()
    {
        RequireTypeScript();
        var exotic = await Fixture.GetRecordAsync("exotic.ts");
        Assert.Contains(exotic.Symbols, s => s.Name == "nsFunc" && s.Kind == "function");          // namespace member
        Assert.Contains(exotic.Symbols, s => s.Name == "exDestructuredA" && s.Kind == "variable"); // destructured export
        Assert.Contains(exotic.Symbols, s => s.Name == "exDestructuredB" && s.Kind == "variable");
        Assert.Contains(exotic.Symbols, s => s.Name == "default" && s.Kind == "function");          // anonymous default export
    }

    [Fact]
    public async Task Files_indexed_without_node_modules_are_flagged_degraded()
    {
        RequireTypeScript();
        var ct = TestContext.Current.CancellationToken;
        var web = await Fixture.Repository.GetBySubProjectAsync(Fixture.WebSubProjectId, ct);
        Assert.NotEmpty(web);
        // The in-memory seed has no node_modules, so TS type resolution was degraded → flag is false.
        // (This count drives the dashboard's "indexed without node_modules" warning.)
        Assert.All(web, r => Assert.False(r.TypeScriptTypesResolved));
    }

    [Fact]
    public async Task Parameters_are_extracted_with_types()
    {
        RequireTypeScript();
        var makeUser = await Fixture.GetSymbolAsync("models.ts", "makeUser");
        Assert.Equal(["id", "name"], makeUser.Parameters.Select(p => p.Name));
        Assert.All(makeUser.Parameters, p => Assert.Equal("string", p.Type));
    }
}
