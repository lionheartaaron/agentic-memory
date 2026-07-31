using System.Reflection;

namespace AgenticMemory.Configuration;

/// <summary>
/// The version of the running build.
///
/// Deliberately separate from the database schema version. They answer different questions and move
/// at different rates: shipping a bug fix bumps the app version and must not imply that the stored
/// data changed shape, and a schema change may land in the middle of a release cycle. Conflating
/// them means either migrating on every update for no reason, or — much worse — failing to migrate
/// because two releases happened to share a version.
///
/// This value is recorded in the database for support purposes only. Nothing branches on it; the
/// schema version is the only thing that decides whether a migration runs.
/// </summary>
public static class AppVersion
{
    /// <summary>e.g. "1.1.0". Falls back to "0.0.0" if the assembly carries no version at all.</summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        // The defining assembly, not the entry assembly: under a test host the entry assembly is the
        // runner, which would stamp every test database with the version of xUnit.
        var assembly = typeof(AppVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<commit sha>" to the informational version. Keep the readable part.
            var plus = informational.IndexOf('+');
            return (plus > 0 ? informational[..plus] : informational).Trim();
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
