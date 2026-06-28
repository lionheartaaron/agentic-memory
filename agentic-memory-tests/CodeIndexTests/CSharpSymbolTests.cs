namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>Structured C# symbol-shape extraction (Roslyn provider → SymbolRecord).</summary>
[Collection(CodeIndexCollection.Name)]
public class CSharpSymbolTests(CodeIndexFixture fixture)
{
    [Fact]
    public async Task Class_carries_kind_accessibility_doc_and_span()
    {
        var calc = await fixture.GetSymbolAsync("Domain.cs", "Calculator");
        Assert.Equal("class", calc.Kind);
        Assert.Equal("public", calc.Accessibility);
        Assert.Equal("Performs arithmetic operations.", calc.DocSummary);
        Assert.True(calc.EndLine > calc.Line, "type span should cover multiple lines");
    }

    [Fact]
    public async Task Method_has_structured_parameters_return_type_and_param_docs()
    {
        var add = await fixture.GetSymbolAsync("Domain.cs", "Add"); // first overload (a, b)
        Assert.Equal("method", add.Kind);
        Assert.Equal(2, add.Parameters.Count);
        Assert.Equal(["a", "b"], add.Parameters.Select(p => p.Name));
        Assert.All(add.Parameters, p => Assert.Equal("int", p.Type));
        Assert.Equal("int", add.Type);
        Assert.Equal("Adds two integers.", add.DocSummary);
        Assert.Equal("The first addend.", add.ParamDocs["a"]);
    }

    [Fact]
    public async Task Async_method_unwraps_task_return_type()
    {
        var m = await fixture.GetSymbolAsync("Domain.cs", "CountAsync");
        Assert.True(m.IsAsync);
        Assert.True(m.IsAwaitable);
        Assert.Equal("int", m.ReturnTypeUnwrapped);
    }

    [Fact]
    public async Task Accessibility_is_derived_from_the_symbol_not_a_keyword_scan()
    {
        var secret = await fixture.GetSymbolAsync("Domain.cs", "Secret");
        Assert.Equal("private", secret.Accessibility);

        var add = await fixture.GetSymbolAsync("Domain.cs", "Add");
        Assert.Equal("public", add.Accessibility);
    }

    [Fact]
    public async Task Deprecated_member_is_flagged_with_message()
    {
        var sum = await fixture.GetSymbolAsync("Domain.cs", "Sum");
        Assert.True(sum.IsDeprecated);
        Assert.Equal("Use Add instead.", sum.DeprecationMessage);
    }

    [Fact]
    public async Task Enum_members_and_constant_values_are_captured()
    {
        var status = await fixture.GetSymbolAsync("Domain.cs", "Status");
        Assert.Equal("enum", status.Kind);
        Assert.Equal(3, status.EnumMembers.Count);
        Assert.Equal(1, status.EnumMembers.Single(m => m.Name == "Active").Value);
        Assert.Equal(3, status.EnumMembers.Single(m => m.Name == "Pending").Value);
    }

    [Fact]
    public async Task Flags_enum_is_detected()
    {
        var perm = await fixture.GetSymbolAsync("Domain.cs", "Permission");
        Assert.True(perm.IsFlags);
    }

    [Fact]
    public async Task Validation_attributes_are_extracted_and_rolled_up()
    {
        var name = await fixture.GetSymbolAsync("Domain.cs", "Name");
        Assert.Contains(name.ValidationRules, r => r.Rule == "Required");

        var age = await fixture.GetSymbolAsync("Domain.cs", "Age");
        Assert.Contains(age.ValidationRules, r => r.Rule == "Range");

        var record = await fixture.GetRecordAsync("Domain.cs");
        Assert.True(record.HasValidation);
    }

    [Fact]
    public async Task Disposable_contract_flag_is_set()
    {
        var holder = await fixture.GetSymbolAsync("Domain.cs", "ResourceHolder");
        Assert.True(holder.ImplementsIDisposable);
    }

    [Fact]
    public async Task Generic_type_parameters_and_constraints_are_captured()
    {
        var box = await fixture.GetSymbolAsync("Domain.cs", "Box");
        Assert.Contains(box.TypeParameters, tp => tp.Name == "T" && tp.Constraints.Contains("class"));
    }

    [Fact]
    public async Task Constructor_is_extracted_as_its_own_symbol()
    {
        var ctor = await fixture.GetSymbolAsync("Domain.cs", "Widget()");
        Assert.Equal("constructor", ctor.Kind);
    }

    [Theory]
    [InlineData("Pub", "public")]
    [InlineData("Intl", "internal")]
    [InlineData("Prot", "protected")]
    [InlineData("Priv", "private")]
    [InlineData("ProtIntl", "protected internal")]
    [InlineData("PrivProt", "private protected")]
    public async Task All_six_accessibility_forms_are_resolved(string field, string expected)
    {
        // Regression: GetAccessibility was a first-match keyword scan that defaulted to private and
        // could not express the combined forms. Derived from DeclaredAccessibility now.
        var sym = await fixture.GetSymbolAsync("Variety.cs", field);
        Assert.Equal(expected, sym.Accessibility);
    }

    [Fact]
    public async Task Field_carries_type_modifiers_and_constant_value()
    {
        // Regression: fields resolved to a null symbol (GetDeclaredSymbol on the field node), so type,
        // modifiers and constant value were all dropped.
        var c = await fixture.GetSymbolAsync("Variety.cs", "ConstField");
        Assert.Equal("field", c.Kind);
        Assert.Equal("int", c.Type);
        Assert.Contains("const", c.Modifiers);
        Assert.Equal("5", c.ConstantValue);

        var ro = await fixture.GetSymbolAsync("Variety.cs", "ReadonlyField");
        Assert.Contains("readonly", ro.Modifiers);
    }

    [Fact]
    public async Task Positional_record_properties_are_extracted_with_validation()
    {
        // Record DTO fields are synthesized properties, not MemberDeclarationSyntax — surface them.
        var first = await fixture.GetSymbolAsync("Variety.cs", "First");
        Assert.Equal("property", first.Kind);
        Assert.Equal("string", first.Type);
        Assert.Contains(first.ValidationRules, r => r.Rule == "Required");

        var last = await fixture.GetSymbolAsync("Variety.cs", "Last");
        Assert.Equal("property", last.Kind);
    }

    [Fact]
    public async Task Event_is_extracted()
    {
        var ev = await fixture.GetSymbolAsync("Variety.cs", "Changed");
        Assert.Equal("event", ev.Kind);
    }

    [Fact]
    public async Task Parameter_shapes_capture_optional_default_params_ref_and_out()
    {
        var defaults = await fixture.GetSymbolAsync("Variety.cs", "WithDefaults");
        var required = defaults.Parameters.Single(p => p.Name == "required");
        var optional = defaults.Parameters.Single(p => p.Name == "optional");
        Assert.False(required.IsOptional);
        Assert.True(optional.IsOptional);
        Assert.Equal("7", optional.DefaultValue);

        var variadic = await fixture.GetSymbolAsync("Variety.cs", "WithParams");
        Assert.True(variadic.Parameters.Single().IsParams);

        var tryThing = await fixture.GetSymbolAsync("Variety.cs", "TryThing");
        Assert.Equal("out", tryThing.Parameters.Single(p => p.Name == "result").RefKind);

        var byRef = await fixture.GetSymbolAsync("Variety.cs", "ByRef");
        Assert.Equal("ref", byRef.Parameters.Single().RefKind);
    }

    [Fact]
    public async Task Return_type_unwrap_covers_task_valuetask_and_async_enumerable()
    {
        var vt = await fixture.GetSymbolAsync("Variety.cs", "ValueTaskM");
        Assert.Equal("string", vt.ReturnTypeUnwrapped);
        Assert.True(vt.IsAwaitable);

        var stream = await fixture.GetSymbolAsync("Variety.cs", "StreamM");
        Assert.True(stream.IsAsyncEnumerable);
        Assert.Equal("int", stream.ReturnTypeUnwrapped);

        var plain = await fixture.GetSymbolAsync("Variety.cs", "PlainTask");
        Assert.True(plain.IsAwaitable);
        Assert.Null(plain.ReturnTypeUnwrapped); // non-generic Task → nothing to unwrap
    }

    [Fact]
    public async Task Override_links_to_the_base_member()
    {
        var derived = await fixture.GetRecordAsync("Variety.cs");
        var hook = derived.Symbols.Single(s => s.Name == "Hook" && s.ContainingTypeFullName == "DerivedHook");
        Assert.True(hook.IsOverride);
        Assert.Equal("M:Backend.BaseHook.Hook", hook.OverriddenSymbolId);
    }

    [Fact]
    public async Task Concurrency_markers_are_detected()
    {
        var locked = await fixture.GetSymbolAsync("Variety.cs", "Locked");
        Assert.True(locked.UsesLock);

        var blocking = await fixture.GetSymbolAsync("Variety.cs", "Blocking");
        Assert.True(blocking.BlocksOnAsync);
    }
}
