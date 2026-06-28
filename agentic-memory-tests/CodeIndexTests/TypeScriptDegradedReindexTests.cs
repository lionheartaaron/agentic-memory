using AgenticMemory.CodeIndex;
using AgenticMemory.Configuration;
using AgenticMemory.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgenticMemoryTests.CodeIndexTests;

/// <summary>
/// The staleness scanner must auto-correct a TypeScript sub-project that was indexed type-less
/// (no node_modules) once node_modules appears — re-queuing its files for a full-fidelity re-index,
/// even though their content/mtime are unchanged.
/// </summary>
public sealed class TypeScriptDegradedReindexTests : IDisposable
{
    private readonly string _root;
    private readonly SharedLiteDatabase _db;
    private readonly LiteDbCodeIndexRepository _repo;

    public TypeScriptDegradedReindexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "am-degraded-tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "web", "src"));
        File.WriteAllText(Path.Combine(_root, "web", "package.json"),
            "{\"name\":\"web\",\"devDependencies\":{\"typescript\":\"5.5.4\"}}");
        File.WriteAllText(Path.Combine(_root, "web", "src", "a.ts"), "export const x = 1;\n");
        _db = new SharedLiteDatabase(Path.Combine(_root, "x.db"));
        _repo = new LiteDbCodeIndexRepository(_db);
    }

    [Fact]
    public async Task Degraded_ts_file_is_requeued_only_once_types_become_available()
    {
        var ct = TestContext.Current.CancellationToken;
        var webRoot = Path.Combine(_root, "web");
        var aTs = Path.Combine(webRoot, "src", "a.ts");
        var sub = new SubProjectRecord("sub1", "ws", "web", webRoot, SubProjectType.TypeScript,
            Path.Combine(webRoot, "package.json"), "typescript", "sub:sub1");
        var subs = new List<SubProjectRecord> { sub };

        // A current record (future mtime → not time-stale) that was indexed type-less.
        await _repo.UpsertAsync(new CodeIndexRecord
        {
            Id = LiteDbCodeIndexRepository.ComputeId(aTs), ProjectId = "ws", SubProjectId = "sub1",
            FilePath = aTs, FileName = "a.ts", RelativePath = "src/a.ts", Language = "typescript",
            ContentHash = "unchanged", FileModifiedAt = File.GetLastWriteTimeUtc(aTs).AddMinutes(5),
            IsStale = false, TypeScriptTypesResolved = false
        }, ct);

        var queue = new CapturingIngestionQueue();
        var scanner = new StalenessScanner(_repo, queue, new NoOpSummaryQueue(), new NoOpReferenceQueue(),
            new CodeIndexSettings(), NullLogger<StalenessScanner>.Instance);

        // No node_modules yet → degraded file stays put (re-indexing to degraded would be pointless).
        await scanner.ScanWorkspaceAsync("ws", _root, subs, ct);
        Assert.DoesNotContain(queue.Jobs, j => j.FilePath.Replace('\\', '/').EndsWith("a.ts"));

        // `npm install` appears → the scanner re-queues the file for a full re-index automatically.
        var libDir = Path.Combine(webRoot, "node_modules", "typescript", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "lib.es2020.full.d.ts"), "// stub lib");

        await scanner.ScanWorkspaceAsync("ws", _root, subs, ct);
        Assert.Contains(queue.Jobs, j => j.FilePath.Replace('\\', '/').EndsWith("a.ts") && j.Force);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class CapturingIngestionQueue : IIngestionQueue
    {
        public List<IngestionJob> Jobs { get; } = [];
        public bool TryEnqueue(IngestionJob job) { Jobs.Add(job); return true; }
        public int Depth => Jobs.Count;
        public void Clear() => Jobs.Clear();
    }

    private sealed class NoOpReferenceQueue : IReferenceQueue
    {
        public bool TryEnqueue(ReferenceJob job) => true;
        public bool TryEnqueueDelete(string fileId) => true;
        public int Depth => 0;
        public void Clear() { }
    }
}
