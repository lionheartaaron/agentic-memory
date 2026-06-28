namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Shared base for the TypeScript test classes: holds the fixture and the availability gate so the
/// whole class skips cleanly when the ClearScript/V8 provider can't initialise in the environment.
/// </summary>
public abstract class TypeScriptTestBase(CodeIndexFixture fixture)
{
    protected readonly CodeIndexFixture Fixture = fixture;

    protected void RequireTypeScript() =>
        Assert.SkipUnless(Fixture.TypeScriptAvailable, "TypeScript/V8 provider not available in this environment.");
}
