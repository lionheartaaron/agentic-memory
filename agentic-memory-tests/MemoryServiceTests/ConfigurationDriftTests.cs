using System.Text.Json;
using AgenticMemory.Configuration;

namespace AgenticMemoryTests.MemoryServiceTests;

/// <summary>
/// The shipped configuration must agree with the configuration the tests measure.
///
/// Every retrieval threshold in this system was calibrated by measurement, and the eval harness runs
/// against the code defaults. <c>appsettings.json</c> restates those numbers explicitly, so a tuning
/// change made in one place and not the other produces the worst kind of failure: a green build and
/// a server that behaves differently from everything that was verified. That happened during this
/// work — the semantic z-score cut was lowered in code while the shipped file kept the old value.
///
/// This is a drift detector, not a policy. Deliberately tuning the shipped file is fine; it just has
/// to be a deliberate act, with the default moved to match.
/// </summary>
public class ConfigurationDriftTests(ITestOutputHelper output)
{
    private static string? FindAppSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "agentic-memory", "appsettings.json");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private static JsonElement? Section(string name)
    {
        var path = FindAppSettings();
        if (path is null) return null;

        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return root.TryGetProperty(name, out var section) ? section : null;
    }

    /// <summary>Compares every number the shipped file states against the compiled default.</summary>
    private void AssertNoDrift(string sectionName, Func<string, object?> defaultFor)
    {
        var section = Section(sectionName);
        Assert.SkipWhen(section is null, "appsettings.json not found from the test output directory");

        var drifted = new List<string>();

        foreach (var property in section!.Value.EnumerateObject())
        {
            var expected = defaultFor(property.Name);
            if (expected is null) continue;                       // not a value this test governs

            var actual = property.Value.ValueKind switch
            {
                JsonValueKind.Number => (object)property.Value.GetDouble(),
                JsonValueKind.True   => true,
                JsonValueKind.False  => false,
                _                    => null!,
            };

            if (actual is null) continue;

            var same = expected switch
            {
                double d => Math.Abs(d - (double)actual) < 1e-9,
                int i    => Math.Abs(i - (double)actual) < 1e-9,
                bool b   => b.Equals(actual),
                _        => true,
            };

            if (!same) drifted.Add($"{sectionName}:{property.Name} — shipped {actual}, default {expected}");
        }

        foreach (var d in drifted) output.WriteLine(d);

        Assert.True(drifted.Count == 0,
            $"appsettings.json has drifted from the measured defaults:\n  {string.Join("\n  ", drifted)}");
    }

    [Fact]
    public void RetrievalSettingsMatchTheMeasuredDefaults()
    {
        var d = new RetrievalSettings();

        AssertNoDrift("Retrieval", name => name switch
        {
            nameof(d.MinSemanticSimilarity)             => d.MinSemanticSimilarity,
            nameof(d.MinTopSemanticZScore)              => d.MinTopSemanticZScore,
            nameof(d.MinTopSemanticZScoreForKnownTerms) => d.MinTopSemanticZScoreForKnownTerms,
            nameof(d.MinSemanticZScore)                 => d.MinSemanticZScore,
            nameof(d.MinSamplesForSemanticDistribution) => d.MinSamplesForSemanticDistribution,
            nameof(d.MinLexicalScore)                   => d.MinLexicalScore,
            nameof(d.MinTrigramSimilarity)              => d.MinTrigramSimilarity,
            nameof(d.MaxCandidatesPerChannel)           => d.MaxCandidatesPerChannel,
            nameof(d.VectorChannelWeight)               => d.VectorChannelWeight,
            nameof(d.LexicalChannelWeight)              => d.LexicalChannelWeight,
            nameof(d.SlotChannelWeight)                 => d.SlotChannelWeight,
            nameof(d.RecencyChannelWeight)              => d.RecencyChannelWeight,
            nameof(d.LinkChannelWeight)                 => d.LinkChannelWeight,
            nameof(d.DiversityLambda)                   => d.DiversityLambda,
            nameof(d.ReinforceOnRead)                   => d.ReinforceOnRead,
            _                                           => null,
        });
    }

    [Fact]
    public void ConflictSettingsMatchTheMeasuredDefaults()
    {
        var d = new ConflictSettings();

        AssertNoDrift("Conflict", name => name switch
        {
            nameof(d.DuplicateSimilarityThreshold) => d.DuplicateSimilarityThreshold,
            nameof(d.CandidateSimilarityThreshold) => d.CandidateSimilarityThreshold,
            nameof(d.MaxCandidates)                => d.MaxCandidates,
            nameof(d.AutoSupersedeEnabled)         => d.AutoSupersedeEnabled,
            _                                      => null,
        });
    }

    /// <summary>Retention and backup policy: the settings that decide whether data can be lost.</summary>
    [Fact]
    public void MaintenanceRetentionSettingsMatchTheDefaults()
    {
        var d = new MaintenanceSettings();

        AssertNoDrift("Maintenance", name => name switch
        {
            nameof(d.ArchiveEpisodicAfterDays)              => d.ArchiveEpisodicAfterDays,
            nameof(d.PurgeForgottenAfterDays)               => d.PurgeForgottenAfterDays,
            nameof(d.SimilarityThreshold)                   => d.SimilarityThreshold,
            nameof(d.BackupBeforeDestructiveOperations)     => d.BackupBeforeDestructiveOperations,
            nameof(d.BackupRetentionCount)                  => d.BackupRetentionCount,
            _                                               => null,
        });
    }

    /// <summary>
    /// The shipped file must not pin the database to a path relative to the program.
    ///
    /// This is the exact line that used to be here — <c>"./Data/agentic-memory.db"</c> — and it is
    /// the reason the database lived inside the application bundle. As a sidecar in an Electron app
    /// that directory is replaced on every auto-update, so the setting would delete every memory the
    /// first time the app updated itself. Empty means the per-user data folder; an absolute path is a
    /// deliberate choice. A relative one is neither.
    /// </summary>
    [Fact]
    public void TheShippedFileDoesNotPinStateToTheProgramDirectory()
    {
        var section = Section("Storage");
        Assert.SkipWhen(section is null, "appsettings.json not found from the test output directory");

        foreach (var key in new[]
                 {
                     nameof(StorageSettings.DataDirectory),
                     nameof(StorageSettings.DatabasePath),
                 })
        {
            if (!section!.Value.TryGetProperty(key, out var value)) continue;

            var configured = value.GetString() ?? "";
            if (configured.Length == 0) continue;

            Assert.True(Path.IsPathRooted(configured),
                $"Storage:{key} is '{configured}'. A program-relative path puts per-user state inside " +
                "the application bundle, which an auto-update replaces wholesale. Leave it empty for " +
                "the per-user data folder, or give an absolute path.");
        }
    }

    /// <summary>
    /// The shipped file must never carry an API key.
    ///
    /// A secret committed to a repository is a secret everybody has, and one shipped inside an
    /// application bundle is worse — it is identical on every install, so recovering it once
    /// unlocks every user's store. The key has to arrive from the host's own configuration or from
    /// the environment variable. Empty here means "no authentication", which is a deliberate,
    /// visible default rather than a false sense of one.
    /// </summary>
    [Fact]
    public void TheShippedFileDoesNotCarryAnApiKey()
    {
        var section = Section("Server");
        Assert.SkipWhen(section is null, "appsettings.json not found from the test output directory");

        if (!section!.Value.TryGetProperty(nameof(ServerSettings.ApiKey), out var value)) return;

        Assert.True(string.IsNullOrWhiteSpace(value.GetString()),
            "Server:ApiKey has a value in the shipped appsettings.json. A key committed here is " +
            $"identical on every install. Set it per-install, or through {ServerSettings.ApiKeyVariable}.");
    }

    /// <summary>
    /// The shipped file must not silently omit a setting that decides whether memories survive.
    /// A missing key is not a neutral act: it hands retention policy to whatever the default happens
    /// to be at the time, which is exactly the class of change nobody reviews.
    /// </summary>
    [Fact]
    public void RetentionAndBackupPolicyIsStatedExplicitly()
    {
        var section = Section("Maintenance");
        Assert.SkipWhen(section is null, "appsettings.json not found from the test output directory");

        foreach (var required in new[]
                 {
                     nameof(MaintenanceSettings.ArchiveEpisodicAfterDays),
                     nameof(MaintenanceSettings.PurgeForgottenAfterDays),
                     nameof(MaintenanceSettings.BackupBeforeDestructiveOperations),
                 })
            Assert.True(section!.Value.TryGetProperty(required, out _),
                $"appsettings.json must state Maintenance:{required} explicitly");
    }
}
