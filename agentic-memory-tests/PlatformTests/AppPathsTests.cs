using AgenticMemory.Configuration;

namespace AgenticMemoryTests.PlatformTests;

/// <summary>
/// Where the server decides to keep its state.
///
/// This matters more than it looks. The server ships as a sidecar inside an Electron application,
/// whose program directory is replaced wholesale on every auto-update; a database resolved to that
/// directory is deleted the first time the app updates itself. These tests pin the line that
/// prevents it — per-user state goes to the platform's data folder, model weights stay with the
/// build — and the precedence rules a host relies on to move either one.
///
/// The environment is injected rather than set, so every case here is hermetic: xUnit runs classes
/// in parallel and the process environment is shared between them. Paths are built for the running
/// platform rather than written as Windows literals, so the suite means the same thing wherever it
/// runs — the layout rules asserted here are exactly the ones nobody notices breaking on the
/// platform they do not develop on.
/// </summary>
public class AppPathsTests
{
    /// <summary>An absolute path for the running platform.</summary>
    private static string Abs(params string[] segments) =>
        Path.GetFullPath(Path.Combine(
            OperatingSystem.IsWindows() ? @"D:\" : "/", Path.Combine(segments)));

    private static readonly string Program     = Abs("apps", "hgirl", "resources", "sidecar");
    private static readonly string LocalAppData = Abs("users", "jo", "AppData", "Local");

    /// <summary>An environment with nothing set except what a case asks for.</summary>
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    private static AppPaths Resolve(
        string[]? args = null,
        string? configuredData = null,
        string? configuredModels = null,
        Func<string, string?>? environment = null,
        HostPlatform platform = HostPlatform.Windows,
        string? program = null) =>
        AppPaths.Resolve(program ?? Program, args, configuredData, configuredModels,
            environment ?? Env(("LOCALAPPDATA", LocalAppData)), platform);

    // ── Platform conventions ────────────────────────────────────────────────

    [Fact]
    public void WindowsUsesLocalAppDataNotRoaming()
    {
        var data = AppPaths.PlatformDataDirectory(
            HostPlatform.Windows,
            Env(("LOCALAPPDATA", @"C:\Users\jo\AppData\Local"), ("APPDATA", @"C:\Users\jo\AppData\Roaming")),
            Program);

        // Roaming profiles are synchronised across machines. A database that grows without bound
        // must never be dragged over the network at login.
        Assert.DoesNotContain("Roaming", data);
        Assert.Equal("C:/Users/jo/AppData/Local/AgenticMemory", Slashes(data));
    }

    [Fact]
    public void MacOsUsesApplicationSupport()
    {
        var data = AppPaths.PlatformDataDirectory(HostPlatform.MacOS, Env(("HOME", "/Users/jo")), Program);

        // Application Support, not Caches: this is the copy that has to be in the user's Time
        // Machine backup.
        Assert.Equal("/Users/jo/Library/Application Support/AgenticMemory", Slashes(data));
    }

    [Fact]
    public void LinuxFollowsXdgWhenSet()
    {
        var data = AppPaths.PlatformDataDirectory(
            HostPlatform.Unix, Env(("HOME", "/home/jo"), ("XDG_DATA_HOME", "/data/jo")), Program);

        Assert.Equal("/data/jo/agentic-memory", Slashes(data));
    }

    [Fact]
    public void LinuxFallsBackToTheSpecifiedDefaultWhenXdgIsUnset()
    {
        var data = AppPaths.PlatformDataDirectory(HostPlatform.Unix, Env(("HOME", "/home/jo")), Program);

        Assert.Equal("/home/jo/.local/share/agentic-memory", Slashes(data));
    }

    /// <summary>
    /// An explicitly set variable is a deliberate act by whoever launched the process — a container,
    /// a test harness, a host that relocates profiles. It must not be overruled by the shell folder
    /// baked into the machine.
    /// </summary>
    [Fact]
    public void AnExplicitEnvironmentVariableOutranksTheMachinesShellFolder()
    {
        var data = AppPaths.PlatformDataDirectory(
            HostPlatform.Windows, Env(("LOCALAPPDATA", @"R:\relocated")), Program);

        Assert.Equal(@"R:/relocated/AgenticMemory", Slashes(data));
    }

    // ── Precedence ──────────────────────────────────────────────────────────

    [Fact]
    public void CommandLineWinsOverEverythingElse()
    {
        var paths = Resolve(
            args: ["--data-dir", Abs("from-cli")],
            configuredData: Abs("from-config"),
            environment: Env(
                (AppPaths.DataDirectoryVariable, Abs("from-env")), ("LOCALAPPDATA", LocalAppData)));

        Assert.Equal(Abs("from-cli"), paths.DataDirectory);
        Assert.Equal(PathOrigin.CommandLine, paths.Origin);
    }

    [Fact]
    public void EnvironmentWinsOverConfiguration()
    {
        var paths = Resolve(
            configuredData: Abs("from-config"),
            environment: Env(
                (AppPaths.DataDirectoryVariable, Abs("from-env")), ("LOCALAPPDATA", LocalAppData)));

        Assert.Equal(Abs("from-env"), paths.DataDirectory);
        Assert.Equal(PathOrigin.Environment, paths.Origin);
    }

    [Fact]
    public void ConfigurationWinsOverThePlatformDefault()
    {
        var paths = Resolve(configuredData: Abs("from-config"));

        Assert.Equal(Abs("from-config"), paths.DataDirectory);
        Assert.Equal(PathOrigin.Configuration, paths.Origin);
    }

    [Fact]
    public void NothingConfiguredMeansThePlatformDefault()
    {
        var paths = Resolve();

        Assert.Equal(Path.Combine(LocalAppData, "AgenticMemory"), paths.DataDirectory);
        Assert.Equal(PathOrigin.PlatformDefault, paths.Origin);
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--models-dir")]
    public void BothArgumentSpellingsAreAccepted(string name)
    {
        var chosen = Abs("chosen");

        var separate = Resolve(args: [name, chosen]);
        var joined   = Resolve(args: [$"{name}={chosen}"]);

        var read = name == "--data-dir"
            ? (Func<AppPaths, string>)(p => p.DataDirectory)
            : p => p.ModelsDirectory;

        Assert.Equal(chosen, read(separate));
        Assert.Equal(chosen, read(joined));
    }

    /// <summary>A trailing <c>--data-dir</c> with no value must not throw or consume garbage.</summary>
    [Fact]
    public void ADanglingArgumentIsIgnored()
    {
        var paths = Resolve(args: ["--port", "3377", "--data-dir"]);

        Assert.Equal(PathOrigin.PlatformDefault, paths.Origin);
    }

    // ── Models stay with the build ──────────────────────────────────────────

    [Fact]
    public void ModelsDefaultToTheProgramDirectory()
    {
        var paths = Resolve();

        // Shipped with the binary, identical for every user on the machine, and re-supplied by the
        // next version. Copying them per user would duplicate gigabytes to no end.
        Assert.Equal(Program, paths.ModelsDirectory);
        Assert.Equal(
            Path.Combine(Program, "Models", "Embedding"),
            paths.InModels("", EmbeddingsSettings.DefaultRelativeModelsPath));
    }

    /// <summary>
    /// Moving where memories are kept says nothing about where the build's own weights live. An
    /// Electron host passing its <c>app.getPath('userData')</c> must not thereby ask for a
    /// multi-gigabyte re-download into the user's profile.
    /// </summary>
    [Fact]
    public void AnOverriddenDataDirectoryDoesNotDragTheModelsWithIt()
    {
        var userData = Abs("hgirl", "userData");
        var paths = Resolve(args: ["--data-dir", userData]);

        Assert.Equal(userData, paths.DataDirectory);
        Assert.Equal(Program, paths.ModelsDirectory);
    }

    /// <summary>The escape hatch for a read-only install that must fetch weights at runtime.</summary>
    [Fact]
    public void ModelsCanStillBePlacedElsewhere()
    {
        var userData = Abs("hgirl", "userData");
        var elsewhere = Abs("big-disk", "models");

        var paths = Resolve(args: ["--data-dir", userData, "--models-dir", elsewhere]);

        Assert.Equal(userData, paths.DataDirectory);
        Assert.Equal(elsewhere, paths.ModelsDirectory);
    }

    // ── Portable mode ───────────────────────────────────────────────────────

    [Fact]
    public void APortableMarkerPinsStateBesideTheExecutable()
    {
        using var program = new TempDirectory();
        File.WriteAllText(Path.Combine(program.Path, AppPaths.PortableMarkerFile), "");

        var paths = Resolve(program: program.Path);

        Assert.Equal(Path.Combine(program.Path, "Data"), paths.DataDirectory);
        Assert.Equal(PathOrigin.Portable, paths.Origin);
    }

    /// <summary>An explicit location is an explicit location; the marker only replaces the default.</summary>
    [Fact]
    public void AnExplicitPathStillOutranksThePortableMarker()
    {
        using var program = new TempDirectory();
        File.WriteAllText(Path.Combine(program.Path, AppPaths.PortableMarkerFile), "");

        var paths = Resolve(program: program.Path, args: ["--data-dir", Abs("explicit")]);

        Assert.Equal(Abs("explicit"), paths.DataDirectory);
    }

    [Fact]
    public void WithoutTheMarkerTheDatabaseNeverResolvesInsideTheProgramDirectory()
    {
        var paths = Resolve();

        // The property that actually protects the data: an auto-update replaces the program
        // directory, so anything resolved underneath it is deleted with the old version.
        Assert.DoesNotContain(paths.ProgramDirectory, paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            paths.ProgramDirectory,
            paths.InData("", StorageSettings.DefaultDatabaseFileName),
            StringComparison.OrdinalIgnoreCase);
    }

    // ── Resolving individual settings against the directories ───────────────

    [Fact]
    public void AnEmptySettingMeansTheStatedDefaultUnderTheDirectory()
    {
        var root = Abs("root");
        var paths = Resolve(args: ["--data-dir", root]);
        var expected = Path.Combine(root, "agentic-memory.db");

        Assert.Equal(expected, paths.InData("", "agentic-memory.db"));
        Assert.Equal(expected, paths.InData(null, "agentic-memory.db"));
        Assert.Equal(expected, paths.InData("   ", "agentic-memory.db"));
    }

    [Fact]
    public void ARelativeSettingIsResolvedAgainstTheDirectory()
    {
        var root   = Abs("root");
        var models = Abs("models");
        var paths  = Resolve(args: ["--data-dir", root, "--models-dir", models]);

        Assert.Equal(Path.Combine(root, "snapshots"), paths.InData("snapshots", "backups"));
        Assert.Equal(Path.Combine(root, "nested", "db.litedb"), paths.InData("./nested/db.litedb", "agentic-memory.db"));
        Assert.Equal(Path.Combine(models, "Models", "Embedding"), paths.InModels("Models/Embedding", "unused"));
    }

    [Fact]
    public void AnAbsoluteSettingIsHonouredAsGiven()
    {
        var paths = Resolve(args: ["--data-dir", Abs("root")]);
        var elsewhere = Abs("somewhere", "else", "my.db");

        Assert.Equal(elsewhere, paths.InData(elsewhere, "agentic-memory.db"));
    }

    /// <summary>Callers rely on this: after resolution nothing in settings is still relative.</summary>
    [Fact]
    public void EveryResolvedPathIsAbsolute()
    {
        var paths = Resolve(args: ["--data-dir", Path.Combine("relative", "to", "program")]);

        Assert.True(Path.IsPathRooted(paths.DataDirectory));
        Assert.True(Path.IsPathRooted(paths.ModelsDirectory));
        Assert.True(Path.IsPathRooted(paths.InData("", "x.db")));
        Assert.True(Path.IsPathRooted(paths.InModels("", "y")));
        Assert.StartsWith(Program, paths.DataDirectory, StringComparison.OrdinalIgnoreCase);
    }

    // ── Usability ───────────────────────────────────────────────────────────

    [Fact]
    public void EnsureUsableCreatesTheDataDirectoryAndLeavesNoProbeBehind()
    {
        using var root = new TempDirectory();
        var data = Path.Combine(root.Path, "data");

        var paths = Resolve(args: ["--data-dir", data]);
        paths.EnsureUsable();

        Assert.True(Directory.Exists(data));
        Assert.Empty(Directory.EnumerateFileSystemEntries(data));
    }

    /// <summary>
    /// A read-only models directory is normal inside a signed bundle and must not stop the server;
    /// the model loader already degrades to no semantic search on its own.
    /// </summary>
    [Fact]
    public void EnsureUsableDoesNotRequireTheModelsDirectoryToExist()
    {
        using var root = new TempDirectory();
        var missing = Path.Combine(root.Path, "nonexistent");

        var paths = Resolve(args: ["--data-dir", Path.Combine(root.Path, "data"), "--models-dir", missing]);
        paths.EnsureUsable();

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void EnsureUsableNamesTheDirectoryAndTheEscapeHatchWhenItCannotWrite()
    {
        using var root = new TempDirectory();

        // A file where the directory should be: CreateDirectory fails, and the message has to be
        // actionable rather than a bare IOException from deep inside the storage engine.
        var blocked = Path.Combine(root.Path, "blocked");
        File.WriteAllText(blocked, "not a directory");

        var paths = Resolve(args: ["--data-dir", blocked]);
        var ex = Record.Exception(paths.EnsureUsable);

        Assert.NotNull(ex);
        Assert.Contains(blocked, ex.Message);
        Assert.Contains("--data-dir", ex.Message);
        Assert.Contains(AppPaths.DataDirectoryVariable, ex.Message);
    }

    /// <summary>
    /// Compares paths without caring which separator the host uses. <c>Path.Combine</c> emits the
    /// running platform's separator, so a macOS or Linux expectation written with forward slashes
    /// would otherwise only match when the suite happens to run there.
    /// </summary>
    private static string Slashes(string path) =>
        Path.TrimEndingDirectorySeparator(path).Replace('\\', '/');
}

/// <summary>A scratch directory that removes itself.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "agentic-memory-tests", "paths-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
