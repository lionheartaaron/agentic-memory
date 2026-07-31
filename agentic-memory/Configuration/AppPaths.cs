namespace AgenticMemory.Configuration;

/// <summary>
/// How the data directory was chosen. Reported at startup and by <c>GET /api/admin/paths</c>, because
/// the one question that must never be guesswork is "where are my memories actually stored".
/// </summary>
public enum PathOrigin
{
    /// <summary>The per-user location for this operating system.</summary>
    PlatformDefault,

    /// <summary>A <c>portable.txt</c> marker beside the executable pinned state to the program directory.</summary>
    Portable,

    /// <summary><c>Storage:DataDirectory</c> in configuration.</summary>
    Configuration,

    /// <summary>The <c>AGENTIC_MEMORY_DATA_DIR</c> environment variable.</summary>
    Environment,

    /// <summary>A <c>--data-dir</c> argument.</summary>
    CommandLine,
}

/// <summary>Which per-user layout convention to follow. Separated from the running OS so it can be tested.</summary>
public enum HostPlatform
{
    Windows,
    MacOS,
    Unix,
}

/// <summary>
/// The two directories this server reads and writes, and the line between them.
///
/// <b>Data — per-user, writable, survives an update.</b> The database and its snapshots. This server
/// ships as a sidecar inside an Electron application, whose program directory is
/// <list type="bullet">
///   <item>read-only on macOS once the bundle is signed and installed under /Applications,</item>
///   <item>often under <c>%ProgramFiles%</c> on Windows, needing elevation to write,</item>
///   <item>and — the one that actually destroys data — <em>replaced wholesale on every auto-update</em>.</item>
/// </list>
/// A database resolved into that directory is deleted the first time the app updates itself. For a
/// companion app whose entire premise is that it remembers you, an update that silently wipes every
/// memory is the worst failure the system has, and it would look exactly like the "losing memories
/// randomly" behaviour this subsystem exists to rule out.
///
/// <b>Models — beside the program, shipped with the binary.</b> Model weights are part of the build,
/// not part of the user. They are identical for every user on the machine, they are large, and
/// copying them into a per-user folder would duplicate gigabytes for nothing. They also do not need
/// to survive an update: the new version brings its own. A host that installs into a read-only
/// location and expects the weights to be fetched at runtime can point them elsewhere with
/// <c>--models-dir</c>.
/// </summary>
public sealed class AppPaths
{
    public const string DataDirectoryVariable   = "AGENTIC_MEMORY_DATA_DIR";
    public const string ModelsDirectoryVariable = "AGENTIC_MEMORY_MODELS_DIR";

    /// <summary>
    /// A file with this name beside the executable pins the data directory to the program directory.
    /// For running from a checkout, or from a USB stick, where "leaves no trace on this machine" is
    /// the point.
    /// </summary>
    public const string PortableMarkerFile = "portable.txt";

    private const string BrandedFolderName = "AgenticMemory";
    private const string UnixFolderName    = "agentic-memory";

    /// <summary>Where the executable and its bundled assets live.</summary>
    public string ProgramDirectory { get; }

    /// <summary>Irreplaceable per-user state: the database and its snapshots.</summary>
    public string DataDirectory { get; }

    /// <summary>Model weights. The program directory unless a host moves them.</summary>
    public string ModelsDirectory { get; }

    public PathOrigin Origin { get; }

    private AppPaths(string programDirectory, string dataDirectory, string modelsDirectory, PathOrigin origin)
    {
        // AppContext.BaseDirectory ends in a separator; a configured path may or may not. These are
        // shown to the user and compared against each other, so they are normalised once here rather
        // than at every call site. TrimEndingDirectorySeparator leaves a root such as "C:\" alone.
        ProgramDirectory = Path.TrimEndingDirectorySeparator(programDirectory);
        DataDirectory    = Path.TrimEndingDirectorySeparator(dataDirectory);
        ModelsDirectory  = Path.TrimEndingDirectorySeparator(modelsDirectory);
        Origin           = origin;
    }

    /// <summary>The platform whose layout convention this process should follow.</summary>
    public static HostPlatform CurrentPlatform =>
        OperatingSystem.IsWindows() ? HostPlatform.Windows
        : OperatingSystem.IsMacOS() ? HostPlatform.MacOS
        : HostPlatform.Unix;

    /// <summary>
    /// Resolves both directories. Data directory precedence, highest first:
    /// <list type="number">
    ///   <item><c>--data-dir</c> — how an Electron host aligns the sidecar with its own
    ///     <c>app.getPath('userData')</c>,</item>
    ///   <item><c>AGENTIC_MEMORY_DATA_DIR</c>,</item>
    ///   <item><c>Storage:DataDirectory</c> in configuration,</item>
    ///   <item>a <c>portable.txt</c> marker beside the executable,</item>
    ///   <item>the per-user location for this platform.</item>
    /// </list>
    /// The models directory follows the same order via <c>--models-dir</c>,
    /// <c>AGENTIC_MEMORY_MODELS_DIR</c> and <c>Storage:ModelsDirectory</c>, defaulting to the program
    /// directory. It deliberately does <em>not</em> follow an overridden data directory: moving where
    /// memories are kept says nothing about where the build's own weights live.
    /// </summary>
    /// <param name="environment">
    /// Environment lookup, injectable so tests need not mutate the process environment — which they
    /// share, and which xUnit runs in parallel.
    /// </param>
    public static AppPaths Resolve(
        string programDirectory,
        string[]? args = null,
        string? configuredDataDirectory = null,
        string? configuredModelsDirectory = null,
        Func<string, string?>? environment = null,
        HostPlatform? platform = null)
    {
        programDirectory = Path.GetFullPath(programDirectory);
        environment ??= System.Environment.GetEnvironmentVariable;

        var (data, origin) = FirstOf(
            (Argument(args, "--data-dir"),       PathOrigin.CommandLine),
            (environment(DataDirectoryVariable), PathOrigin.Environment),
            (configuredDataDirectory,            PathOrigin.Configuration),
            (PortableRoot(programDirectory),     PathOrigin.Portable))
            ?? (PlatformDataDirectory(platform ?? CurrentPlatform, environment, programDirectory),
                PathOrigin.PlatformDefault);

        var models = FirstOf(
            (Argument(args, "--models-dir"),       PathOrigin.CommandLine),
            (environment(ModelsDirectoryVariable), PathOrigin.Environment),
            (configuredModelsDirectory,            PathOrigin.Configuration))
            ?.Value ?? programDirectory;

        return new AppPaths(
            programDirectory,
            Absolute(data, programDirectory),
            Absolute(models, programDirectory),
            origin);
    }

    /// <summary>The per-user data location for a platform, following its own convention.</summary>
    public static string PlatformDataDirectory(
        HostPlatform platform, Func<string, string?> environment, string programDirectory)
    {
        switch (platform)
        {
            case HostPlatform.Windows:
            {
                // Local, not Roaming: a database that grows without bound must never be dragged
                // across the network by a roaming profile.
                //
                // The environment variable is consulted first and the shell folder second. An
                // explicitly set variable is a deliberate act by whoever launched the process — a
                // test harness, a container, a host that relocates profiles — and should not be
                // silently overruled by the value baked into the user's registry.
                var local = FirstNonEmpty(
                    environment("LOCALAPPDATA"),
                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData));

                if (local is null) break;
                return Path.Combine(local, BrandedFolderName);
            }

            case HostPlatform.MacOS:
            {
                // Application Support, not Caches: this is the copy that must be in the user's
                // Time Machine backup.
                var home = HomeDirectory(environment);
                if (home is null) break;

                return Path.Combine(home, "Library", "Application Support", BrandedFolderName);
            }

            case HostPlatform.Unix:
            {
                var home = HomeDirectory(environment);
                var data = FirstNonEmpty(environment("XDG_DATA_HOME"))
                           ?? (home is null ? null : Path.Combine(home, ".local", "share"));

                if (data is null) break;
                return Path.Combine(data, UnixFolderName);
            }
        }

        // No home directory — an unusual service or container context. Staying beside the program is
        // wrong for all the reasons above, but it is better than failing to start, and the startup
        // banner names the location either way.
        return Path.Combine(programDirectory, "Data");
    }

    /// <summary>
    /// Resolves a configured file or folder against the data directory: empty means the stated
    /// default, a relative path is relative to the directory, and an absolute path is honoured as
    /// given.
    /// </summary>
    public string InData(string? configured, string defaultRelative) =>
        Combine(DataDirectory, configured, defaultRelative);

    /// <summary>The same rule against the models directory.</summary>
    public string InModels(string? configured, string defaultRelative) =>
        Combine(ModelsDirectory, configured, defaultRelative);

    private static string Combine(string root, string? configured, string defaultRelative)
    {
        var value = string.IsNullOrWhiteSpace(configured) ? defaultRelative : configured.Trim();
        return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    /// <summary>
    /// Creates the data directory and proves it is writable.
    ///
    /// The probe is not redundant with <see cref="Directory.CreateDirectory(string)"/>: an existing
    /// directory the process cannot write to creates nothing and throws nothing. Failing here, by
    /// name, beats failing several seconds later inside the storage engine.
    ///
    /// The models directory is deliberately not checked — it is legitimately read-only inside a
    /// signed application bundle, and the model loader already degrades on its own.
    /// </summary>
    public void EnsureUsable()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);

            var probe = Path.Combine(DataDirectory, $".write-probe-{System.Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new IOException(
                $"The data directory '{DataDirectory}' is not usable ({ex.Message}). " +
                $"Pass --data-dir <path>, or set {DataDirectoryVariable}, to choose a different location.", ex);
        }
    }

    /// <summary>Reads <c>--name value</c> or <c>--name=value</c>.</summary>
    private static string? Argument(string[]? args, string name)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name)
                return i + 1 < args.Length ? args[i + 1] : null;

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
                return args[i][(name.Length + 1)..];
        }

        return null;
    }

    private static string? PortableRoot(string programDirectory) =>
        File.Exists(Path.Combine(programDirectory, PortableMarkerFile))
            ? Path.Combine(programDirectory, "Data")
            : null;

    private static (string Value, PathOrigin Origin)? FirstOf(params (string? Value, PathOrigin Origin)[] candidates)
    {
        foreach (var (value, origin) in candidates)
            if (!string.IsNullOrWhiteSpace(value))
                return (value.Trim(), origin);

        return null;
    }

    private static string Absolute(string path, string programDirectory) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(programDirectory, path));

    /// <summary>Environment first, shell folder second — see the note in <see cref="PlatformDataDirectory"/>.</summary>
    private static string? HomeDirectory(Func<string, string?> environment) =>
        FirstNonEmpty(
            environment("HOME"),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile));

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
