using System.Diagnostics;
using AgenticMemory.Brain.Embeddings;
using AgenticMemory.CodeIndex;
using AgenticMemory.CodeIndex.CSharp;
using AgenticMemory.CodeIndex.TypeScript;
using AgenticMemory.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// Spins up the real code-index pipeline against the <see cref="SampleProject"/> seed once, then
/// shares it across every test in the <see cref="CodeIndexCollection"/>.
///
/// Fidelity vs. determinism: ingestion is driven synchronously through the same
/// <see cref="FileIngestionService"/> the worker calls (so there is no background-thread race on the
/// ingest side), but the reference graph is built by the genuine <see cref="ReferenceIndexWorker"/>
/// running on its dedicated thread — exactly as in production — and we block until it drains.
///
/// Embeddings and LLM summaries are disabled (no model downloads): the structural index, references,
/// domain facts and manifests are all fully exercised; semantic-search assertions live
/// elsewhere and are gated on availability.
/// </summary>
public sealed class CodeIndexFixture : IAsyncLifetime
{
    private string _tempRoot = "";
    private SharedLiteDatabase? _db;
    private ReferenceIndexWorker? _referenceWorker;
    private CodeIndexService? _codeIndex;

    public ICodeIndexRepository Repository { get; private set; } = null!;
    public CodeIndexService CodeIndex => _codeIndex!;

    public string WorkspaceId { get; } = Guid.NewGuid().ToString("N");
    public string WorkspaceRoot => _tempRoot;

    public string BackendSubProjectId { get; private set; } = "";
    public string WebSubProjectId { get; private set; } = "";
    public string BackendRoot { get; private set; } = "";
    public string WebRoot { get; private set; } = "";

    /// <summary>True when the TypeScript/V8 provider initialised and produced symbols for the seed.</summary>
    public bool TypeScriptAvailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "agentic-codeindex-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempRoot);
        SampleProject.Write(_tempRoot);

        EnsureBridgeScriptPresent();

        _db = new SharedLiteDatabase(Path.Combine(_tempRoot, "index.db"));
        Repository = new LiteDbCodeIndexRepository(_db);

        var tracker  = new WorkerStatusTracker();
        var embedding = NullEmbeddingService.Instance;

        var providers = new List<ICodeIntelligenceProvider>
        {
            new CSharpRoslynProvider(NullLogger<CSharpRoslynProvider>.Instance),
        };
        var tsPath = ResolveTypeScriptCompilerPath();
        if (tsPath is not null)
            providers.Add(new TypeScriptClearScriptProvider(tsPath, NullLogger<TypeScriptClearScriptProvider>.Instance));

        _codeIndex = new CodeIndexService(providers, NullLogger<CodeIndexService>.Instance);

        _referenceWorker = new ReferenceIndexWorker(
            Repository, _codeIndex, tracker, NullLogger<ReferenceIndexWorker>.Instance);

        var ingestion = new FileIngestionService(
            _codeIndex, Repository, embedding,
            new NoOpSummaryQueue(), _referenceWorker,
            NullLogger<FileIngestionService>.Instance);

        // 1. Discover sub-projects (.csproj → C#, package.json → TS) exactly as the workspace flow does.
        var discovery = new WorkspaceDiscoveryService(NullLogger<WorkspaceDiscoveryService>.Instance);
        var subProjects = await discovery.DiscoverAsync(_tempRoot);

        foreach (var sub in subProjects)
        {
            if (sub.Type == SubProjectType.CSharpProject) { BackendSubProjectId = sub.Id; BackendRoot = sub.RootPath; }
            if (sub.Type == SubProjectType.TypeScript)    { WebSubProjectId = sub.Id;    WebRoot = sub.RootPath; }
        }

        // 2. Register each sub-project with its provider (builds the whole-program compilation/index).
        foreach (var sub in subProjects)
            await _codeIndex.RegisterSubProjectAsync(sub);

        // 3. Capture manifests (.csproj / package.json / tsconfig.json) like StalenessScanner does.
        var manifests = ManifestExtractor.Extract(_tempRoot, WorkspaceId, null, DateTime.UtcNow);
        await Repository.UpsertProjectManifestsAsync(WorkspaceId, manifests);

        // 4. Ingest every indexable file per sub-project — drives symbols, semantic metadata, domain
        //    facts, and enqueues the reference jobs the worker will consume in step 5.
        foreach (var sub in subProjects)
        {
            foreach (var file in EnumerateIndexableFiles(sub.RootPath))
            {
                await ingestion.IngestAsync(
                    file, WorkspaceId, _tempRoot, force: false,
                    subProjectId: sub.Id, subProjectRoot: sub.RootPath);
            }
        }

        // 5. Run the real reference worker until the graph is fully built.
        await _referenceWorker.StartAsync(default);
        await WaitForReferenceDrainAsync(_referenceWorker, tracker, Repository);

        TypeScriptAvailable = await DetectTypeScriptAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_referenceWorker is not null)
            try { await _referenceWorker.StopAsync(default); } catch { /* best effort */ }
        _codeIndex?.Dispose();
        try { _db?.Dispose(); } catch { /* best effort */ }

        await Task.Delay(50);
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* temp cleanup is best-effort */ }
    }

    // ── Query helpers used by the test classes ────────────────────────────────────

    /// <summary>Finds the indexed record whose relative path ends with <paramref name="relativeSuffix"/>.</summary>
    public async Task<CodeIndexRecord> GetRecordAsync(string relativeSuffix)
    {
        var all = await Repository.GetByProjectAsync(WorkspaceId);
        var norm = relativeSuffix.Replace('\\', '/');
        var rec = all.FirstOrDefault(r => r.RelativePath.Replace('\\', '/').EndsWith(norm, StringComparison.OrdinalIgnoreCase));
        return rec ?? throw new InvalidOperationException(
            $"No indexed record ends with '{relativeSuffix}'. Indexed: {string.Join(", ", all.Select(r => r.RelativePath))}");
    }

    public async Task<SymbolRecord> GetSymbolAsync(string relativeSuffix, string symbolName)
    {
        var rec = await GetRecordAsync(relativeSuffix);
        return rec.Symbols.FirstOrDefault(s => s.Name == symbolName)
            ?? throw new InvalidOperationException(
                $"Symbol '{symbolName}' not found in {rec.RelativePath}. Have: {string.Join(", ", rec.Symbols.Select(s => s.Name))}");
    }

    public async Task<SymbolReferenceRecord?> GetSymbolRefAsync(string relativeSuffix, string symbolName)
    {
        var rec  = await GetRecordAsync(relativeSuffix);
        var refs = await Repository.GetDefinedInFileAsync(rec.Id);
        return refs.FirstOrDefault(r => r.SymbolName == symbolName);
    }

    public Task<IReadOnlyList<DomainFactRecord>> BackendFactsAsync(string? kind = null) =>
        Repository.GetDomainFactsByProjectAsync(WorkspaceId, kind, BackendSubProjectId);

    // ── Internal plumbing ─────────────────────────────────────────────────────────

    private static readonly string[] IndexedExtensions = [".cs", ".ts", ".tsx"];
    private static readonly string[] ExcludeSegments = ["node_modules", "bin", "obj", ".git", "dist", "out", ".next", "coverage"];

    private static IEnumerable<string> EnumerateIndexableFiles(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", opts))
        {
            if (!IndexedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(root, file);
            var segs = rel.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segs.Take(segs.Length - 1).Any(s => ExcludeSegments.Contains(s, StringComparer.OrdinalIgnoreCase))) continue;
            yield return file;
        }
    }

    private static async Task WaitForReferenceDrainAsync(
        ReferenceIndexWorker worker, WorkerStatusTracker tracker, ICodeIndexRepository repo)
    {
        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(90);
        while (sw.Elapsed < timeout)
        {
            if (worker.Depth == 0 && tracker.GetSnapshot(repo).CurrentReferenceFile is null)
            {
                await Task.Delay(150);
                if (worker.Depth == 0 && tracker.GetSnapshot(repo).CurrentReferenceFile is null)
                    return;
            }
            await Task.Delay(75);
        }
        throw new TimeoutException("Reference index worker did not drain within 90s.");
    }

    private async Task<bool> DetectTypeScriptAsync()
    {
        try
        {
            var all = await Repository.GetByProjectAsync(WorkspaceId);
            var ts = all.FirstOrDefault(r => r.RelativePath.Replace('\\', '/').EndsWith("utils.ts", StringComparison.OrdinalIgnoreCase));
            return ts is { Symbols.Count: > 0 };
        }
        catch { return false; }
    }

    // bridge.js is loaded from AppContext.BaseDirectory by the TS provider; ensure it is present
    // (the content item should flow through the project reference, but copy defensively if not).
    private static void EnsureBridgeScriptPresent()
    {
        var dest = Path.Combine(AppContext.BaseDirectory, "CodeIndex", "TypeScript", "bridge.js");
        if (File.Exists(dest)) return;
        var src = FindRepoFile(Path.Combine("agentic-memory", "CodeIndex", "TypeScript", "bridge.js"));
        if (src is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        try { File.Copy(src, dest, overwrite: true); } catch { /* best effort */ }
    }

    private static string? ResolveTypeScriptCompilerPath() =>
        FindRepoFile(Path.Combine("agentic-memory", "Models", "TypeScript", "typescript.js"));

    /// <summary>Walks up from the test output directory to locate a file by repo-relative path.</summary>
    private static string? FindRepoFile(string repoRelative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, repoRelative);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}

/// <summary>No-op summary queue — LLM summaries are out of scope for the structural index tests.</summary>
internal sealed class NoOpSummaryQueue : ISummaryQueue
{
    public bool TryEnqueue(SummaryJob job) => true;
}

[CollectionDefinition(CodeIndexCollection.Name)]
public sealed class CodeIndexCollection : ICollectionFixture<CodeIndexFixture>
{
    public const string Name = "codeindex";
}
