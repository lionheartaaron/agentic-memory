namespace AgenticMemory.Configuration;

/// <summary>
/// Root application settings
/// </summary>
public class AppSettings
{
    public ServerSettings Server { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public EmbeddingsSettings Embeddings { get; set; } = new();
    public GenerationSettings Generation { get; set; } = new();
    public MaintenanceSettings Maintenance { get; set; } = new();
    public ConflictSettings Conflict { get; set; } = new();
    public RetrievalSettings Retrieval { get; set; } = new();
    public CodeIndexSettings CodeIndex { get; set; } = new();
}

/// <summary>
/// Server configuration settings
/// </summary>
public class ServerSettings
{
    /// <summary>Overrides <see cref="ApiKey"/>. See the remarks there for why an environment
    /// variable is offered but a command-line flag is not.</summary>
    public const string ApiKeyVariable = "AGENTIC_MEMORY_API_KEY";

    public int Port { get; set; } = 3377;
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// Shared secret required on every API and MCP request. **Empty means no authentication**,
    /// which is the default and is only appropriate on a machine you control.
    ///
    /// Set this whenever the server is reachable by anything you did not write — which, on the
    /// default <c>0.0.0.0</c> bind address, is every machine on the local network.
    ///
    /// Also settable through the <see cref="ApiKeyVariable"/> environment variable, which takes
    /// precedence. That exists because a host application generating a key per install should not
    /// have to write it to a file it may not be able to write to. There is deliberately no
    /// command-line flag: process arguments are readable by any other process on Windows, Linux and
    /// macOS alike, so a secret passed that way is not a secret.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Header carrying <see cref="ApiKey"/>. <c>Authorization: Bearer &lt;key&gt;</c> is always
    /// accepted as well, because most HTTP and MCP clients can send that without extra
    /// configuration.
    /// </summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>Whether a key is configured at all.</summary>
    public bool RequiresAuthentication => !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Storage configuration settings.
///
/// Every path here is empty by default, meaning "use the default location" — the per-user data
/// directory for state, the program directory for model weights. See <see cref="AppPaths"/> for where
/// that line falls and why. A relative value is resolved against the corresponding directory; an
/// absolute value is honoured as given. All of them hold an absolute path by the time the container
/// is built.
/// </summary>
public class StorageSettings
{
    /// <summary>Default file name inside the data directory when <see cref="DatabasePath"/> is empty.</summary>
    public const string DefaultDatabaseFileName = "agentic-memory.db";

    /// <summary>
    /// Overrides the per-user data directory: the database, its snapshots, and anything else that
    /// cannot be recreated. Empty means the platform default. Overridden in turn by
    /// <c>--data-dir</c> and <c>AGENTIC_MEMORY_DATA_DIR</c>, which is how an Electron host points the
    /// sidecar at its own <c>app.getPath('userData')</c>.
    /// </summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// Overrides where model weights are looked up. Empty means beside the program, so the models
    /// ship with the binary and are not duplicated per user. Worth setting only when the install
    /// location is read-only and the weights have to be downloaded at runtime.
    /// </summary>
    public string ModelsDirectory { get; set; } = "";

    public string DatabasePath { get; set; } = "";
    public int MaxContentSizeBytes { get; set; } = 524288;
    public int MaxTitleLength { get; set; } = 500;
    public int MaxSummaryLength { get; set; } = 2000;
    public int MaxTagsPerMemory { get; set; } = 20;
}


/// <summary>
/// Embeddings configuration settings
/// </summary>
public class EmbeddingsSettings
{
    /// <summary>Location under the models directory when <see cref="ModelsPath"/> is empty.</summary>
    public const string DefaultRelativeModelsPath = "Models/Embedding";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Empty means <see cref="DefaultRelativeModelsPath"/> under the models directory — beside the
    /// program, so the weights ship with the binary. Relative is resolved against that directory;
    /// absolute is honoured as given.
    /// </summary>
    public string ModelsPath { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public string ModelUrlOnnx { get; set; } = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/onnx/model.onnx";
    public string ModelVocabUrlTxt { get; set; } = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main/vocab.txt";
    public string ModelFileName { get; set; } = "all-MiniLM-L6-v2.onnx";
    public string VocabFileName { get; set; } = "vocab.txt";

    /// <summary>
    /// Embedding vector dimensions (384 for all-MiniLM-L6-v2, 768 for all-mpnet-base-v2)
    /// </summary>
    public int ModelDimensions { get; set; } = 384;

    /// <summary>
    /// Maximum sequence length in tokens (256 for all-MiniLM-L6-v2, 384 for all-mpnet-base-v2)
    /// </summary>
    public int MaxSequenceLength { get; set; } = 256;

    /// <summary>
    /// Get the full path to the ONNX model file
    /// </summary>
    public string GetModelPath() => Path.Combine(ModelsPath, ModelFileName);

    /// <summary>
    /// Get the full path to the vocabulary file
    /// </summary>
    public string GetVocabPath() => Path.Combine(ModelsPath, VocabFileName);
}


/// <summary>
/// Maintenance settings for background tasks
/// </summary>
public class MaintenanceSettings
{
    /// <summary>
    /// Enable background maintenance tasks
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Enable the periodic upkeep sweep: expiring ephemeral memories and ageing out old episodics.
    /// This never deletes anything — see <see cref="PurgeForgottenAfterDays"/> for the only path
    /// that removes data.
    /// </summary>
    public bool DecayEnabled { get; set; } = true;

    /// <summary>
    /// Hours between upkeep sweeps
    /// </summary>
    public int DecayIntervalHours { get; set; } = 24;

    /// <summary>
    /// Episodic memories not recalled for this long are moved to cold storage (archived, still
    /// queryable, never deleted). Facts, preferences, persona and affect are exempt entirely.
    ///
    /// Replaces the old strength-based prune, which hard-deleted <em>any</em> memory roughly 23–46
    /// days after its last retrieval — and since retrieval was the only thing that reset the clock,
    /// a recall failure was enough to destroy a memory permanently.
    /// </summary>
    public int ArchiveEpisodicAfterDays { get; set; } = 180;

    /// <summary>
    /// How long a memory the user asked to forget is kept as a tombstone before it is physically
    /// purged. Zero disables purging entirely.
    /// </summary>
    public int PurgeForgottenAfterDays { get; set; } = 30;

    /// <summary>
    /// Enable automatic memory consolidation
    /// </summary>
    public bool ConsolidationEnabled { get; set; } = true;

    /// <summary>
    /// Hours between consolidation operations
    /// </summary>
    public int ConsolidationIntervalHours { get; set; } = 24;

    /// <summary>
    /// Raw cosine similarity above which two memories are treated as near-duplicates worth
    /// consolidating. Raw cosine, consistent with <see cref="ConflictSettings"/> — consolidation
    /// previously compared a 0.6/0.4 blend of raw cosine and trigram overlap against this same
    /// number while conflict handling used a different mapping entirely.
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.9;

    /// <summary>
    /// Minutes to wait after startup before running first maintenance task
    /// </summary>
    public int InitialDelayMinutes { get; set; } = 5;

    /// <summary>
    /// Take a file snapshot of the database before anything that cannot be undone: the retention
    /// purge, consolidation, a rebuild, or a wipe. Cheap insurance against a bug in exactly the code
    /// paths where a bug is unrecoverable.
    /// </summary>
    public bool BackupBeforeDestructiveOperations { get; set; } = true;

    /// <summary>Default location under the data directory when <see cref="BackupPath"/> is empty.</summary>
    public const string DefaultRelativeBackupPath = "backups";

    /// <summary>
    /// Where snapshots are written. Empty means <see cref="DefaultRelativeBackupPath"/> under the
    /// data directory — deliberately beside the database rather than in the cache, since a snapshot
    /// is the last copy of something that cannot be downloaded again.
    /// </summary>
    public string BackupPath { get; set; } = "";

    /// <summary>How many snapshots to keep. Older ones are pruned after each new snapshot.</summary>
    public int BackupRetentionCount { get; set; } = 10;
}

/// <summary>
/// Settings for the local generative model (Phi-4-mini-instruct via OnnxRuntimeGenAI).
/// </summary>
public class GenerationSettings
{
    /// <summary>Location under the models directory when <see cref="ModelsPath"/> is empty.</summary>
    public const string DefaultRelativeModelsPath = "Models/Generative/Phi-4-mini-instruct";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Empty means <see cref="DefaultRelativeModelsPath"/> under the models directory — beside the
    /// program. Nearly 5 GB of weights land here, which is the reason they stay with the build
    /// rather than being copied into every user's profile.
    /// </summary>
    public string ModelsPath { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public string RepoBaseUrl { get; set; } =
        "https://huggingface.co/microsoft/Phi-4-mini-instruct-onnx/resolve/a64b5309e58f6ac22cacdf9d143ab7455d8b9f5b/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4";
    public List<ModelFileSpec> Files { get; set; } =
    [
        new() { FileName = "genai_config.json",      ExpectedBytes = 1520 },
        new() { FileName = "config.json",             ExpectedBytes = 2500 },
        new() { FileName = "configuration_phi3.py",   ExpectedBytes = 10900 },
        new() { FileName = "tokenizer.json",          ExpectedBytes = 15500000 },
        new() { FileName = "tokenizer_config.json",   ExpectedBytes = 2960 },
        new() { FileName = "vocab.json",              ExpectedBytes = 3910000 },
        new() { FileName = "merges.txt",              ExpectedBytes = 2420000 },
        new() { FileName = "added_tokens.json",       ExpectedBytes = 249 },
        new() { FileName = "special_tokens_map.json", ExpectedBytes = 587 },
        new() { FileName = "model.onnx",              ExpectedBytes = 52100000 },
        new() { FileName = "model.onnx.data",         ExpectedBytes = 4860000000 }
    ];
    public int MaxNewTokens { get; set; } = 512;
    /// <summary>
    /// Character count above which the middle of the input is truncated before
    /// sending to the model. Head and tail lines are preserved; the rest is dropped.
    /// </summary>
    public int TruncateThreshold { get; set; } = 8_000;
    public int TruncateHeadLines { get; set; } = 8;
    public int TruncateTailLines { get; set; } = 8;
    public float Temperature { get; set; } = 0.7f;

    public string TruncateIfNeeded(string input)
    {
        if (input.Length <= TruncateThreshold) return input;

        var lines = input.Split('\n');
        var keep = TruncateHeadLines + TruncateTailLines;
        if (lines.Length <= keep) return input;

        var omitted = lines.Length - keep;
        return string.Join('\n', lines.Take(TruncateHeadLines))
            + $"\n\n[... {omitted} lines omitted for brevity ...]\n\n"
            + string.Join('\n', lines.TakeLast(TruncateTailLines));
    }
    public float TopP { get; set; } = 0.9f;
}

public class ModelFileSpec
{
    public string FileName { get; set; } = "";
    public long? ExpectedBytes { get; set; }
}

/// <summary>
/// Conflict resolution settings for handling contradictory or duplicate memories.
/// Uses content similarity to determine when memories should be superseded.
/// </summary>
/// <summary>
/// Settings for the CodeIndex compiler-backed analysis module.
/// Per code-understanding-methodology.md: the C# Roslyn provider is enabled by default
/// (Roslyn runs in the same CLR, no external dependency). The TypeScript ClearScript provider
/// requires typescript.js and is disabled until that file is configured.
/// </summary>
public class CodeIndexSettings
{
    /// <summary>Enable the CodeIndex module. When false, all requests fall back to the regex-based CodeContextExtractor.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Enable the C# Roslyn provider (runs native .NET, no extra setup needed).</summary>
    public bool EnableCSharpRoslyn { get; set; } = true;

    /// <summary>
    /// Enable the TypeScript ClearScript/V8 provider. Requires TypeScriptCompilerPath to be set.
    /// Obtain typescript.js from any project that uses TypeScript:
    ///   node -e "console.log(require.resolve('typescript').replace('index.js','typescript.js'))"
    /// </summary>
    public bool EnableTypeScriptV8 { get; set; } = false;

    /// <summary>
    /// Absolute path to typescript.js. When AutoDownloadTypeScript is true this is set
    /// automatically; override only if you want to supply your own copy.
    /// </summary>
    public string? TypeScriptCompilerPath { get; set; }

    /// <summary>
    /// Automatically download typescript.js from unpkg.com when it is not present.
    /// Stored in TypeScriptModelsPath. Mirrors the AutoDownload pattern used for ONNX models.
    /// </summary>
    public bool AutoDownloadTypeScript { get; set; } = true;

    /// <summary>Location under the models directory when <see cref="TypeScriptModelsPath"/> is empty.</summary>
    public const string DefaultRelativeTypeScriptPath = "Models/TypeScript";

    /// <summary>
    /// Folder where typescript.js is cached after download. Empty means
    /// <see cref="DefaultRelativeTypeScriptPath"/> under the models directory, beside the program.
    /// </summary>
    public string TypeScriptModelsPath { get; set; } = "";

    /// <summary>
    /// npm version of TypeScript to download (semver, no leading "v").
    /// See https://unpkg.com/typescript/ for available versions.
    /// </summary>
    public string TypeScriptVersion { get; set; } = "5.5.4";

    /// <summary>
    /// Project roots to pre-register on startup. Each root is indexed with every provider that
    /// can handle it, enabling whole-project cross-file queries from the first request.
    /// </summary>
    public List<string> ProjectRoots { get; set; } = [];

    /// <summary>Enable background file watching and auto-ingestion for the active project.</summary>
    public bool EnableFileWatcher { get; set; } = true;

    /// <summary>File extensions to include in the index (dot-prefixed).</summary>
    public List<string> IndexedExtensions { get; set; } = [".cs", ".ts", ".tsx", ".jsx", ".js"];

    /// <summary>
    /// Folder names that are always ignored during indexing, independent of .gitignore.
    /// A file whose path contains one of these directory segments is skipped by every scan
    /// path (staleness scan, file watcher, workspace discovery, manifest extraction, and the
    /// whole-program compiler enumerations). Configure under "CodeIndex:ExcludePatterns".
    /// </summary>
    public List<string> ExcludePatterns { get; set; } =
        ["node_modules", "bin", "obj", ".git", ".vs", ".vscode", "dist", "build", "out",
         ".next", "coverage", ".expo", ".turbo"];

    /// <summary>Safety cap on files indexed per project.</summary>
    public int MaxFilesPerProject { get; set; } = 2000;
}

public class ConflictSettings
{
    /// <summary>
    /// IMPORTANT: every threshold here is a RAW cosine similarity in [-1, 1].
    ///
    /// The previous settings were compared against a (cos+1)/2 mapping, so the configured
    /// supersede threshold of 0.80 actually fired at raw cosine 0.60 — "same topic" rather than
    /// "contradicts" — and archived unrelated memories. Values are not transferable between the
    /// two scales; the old numbers must not be copied forward.
    /// </summary>
    public const string ScaleNote = "raw cosine, not (cos+1)/2";

    /// <summary>
    /// Above this, two memories are treated as restatements of one another and the existing one is
    /// reinforced instead of a duplicate being created.
    /// </summary>
    public double DuplicateSimilarityThreshold { get; set; } = 0.92;

    /// <summary>
    /// Above this, a memory is merely worth <em>comparing</em> against the incoming one. It never
    /// causes a replacement on its own — <c>SupersedeGate</c> makes that decision from the slot,
    /// subject, scope and provenance.
    /// </summary>
    public double CandidateSimilarityThreshold { get; set; } = 0.55;

    /// <summary>Maximum semantic candidates considered per store.</summary>
    public int MaxCandidates { get; set; } = 25;

    /// <summary>
    /// When false, even a legal replacement is recorded as a conflict for confirmation instead of
    /// being applied. Useful while tuning a new slot registry.
    /// </summary>
    public bool AutoSupersedeEnabled { get; set; } = true;
}

/// <summary>
/// Retrieval pipeline tuning. Channel weights feed Reciprocal Rank Fusion, which combines
/// rankings rather than scores, so these are relative importances and need no rescaling when a
/// channel is added or removed.
/// </summary>
public class RetrievalSettings
{
    /// <summary>
    /// Absolute raw-cosine floor, used only when the candidate set is too small to estimate a
    /// similarity distribution (see <see cref="MinSamplesForSemanticDistribution"/>) <em>and</em> the
    /// query is written in words the embedding model knows.
    ///
    /// Deliberately low. This branch is the first days of a companion's life, when a handful of
    /// memories exist and there is no distribution to reason about; a floor of 0.35 put genuine
    /// answers out of reach — "what can the user not eat" scores 0.30 against "the user is allergic
    /// to shellfish" — so a new companion could recall nothing until the corpus grew. Nonsense is
    /// excluded by the known-words test rather than by this number, which is the only thing that
    /// works: gibberish measured 0.45, above the floor either way.
    /// </summary>
    public double MinSemanticSimilarity { get; set; } = 0.25;

    /// <summary>
    /// How many standard deviations above the corpus mean the <em>best</em> match must sit for the
    /// vector channel to contribute at all.
    ///
    /// An absolute cosine threshold cannot do this job. Measured against all-MiniLM-L6-v2, genuine
    /// but differently-worded queries produce gold similarities as low as 0.23, while a pure
    /// gibberish query ("xyznonexistent123") reaches 0.45 — higher than most real matches. Unknown
    /// tokens embed near the corpus centroid, so nonsense lifts <em>every</em> score at once and is
    /// betrayed by the shape of the distribution rather than by any single value: real queries
    /// measured z 3.6–10.6, nonsense 2.3–2.9.
    /// </summary>
    public double MinTopSemanticZScore { get; set; } = 3.2;

    /// <summary>
    /// The same gate for a query whose words the embedding model actually knows (see
    /// <c>IEmbeddingService.IsKnownTerm</c>). Such a query has a genuine position in the semantic
    /// space, so a smaller separation is already evidence; the strict bar above exists only to
    /// contain invented tokens, which embed near the corpus centroid.
    ///
    /// Measured: at the strict bar, "how does he get to the office" failed to reach the memory
    /// recording that the user cycles to work — no shared vocabulary, and the separation sat just
    /// under 3.2 in a 220-memory corpus.
    /// </summary>
    public double MinTopSemanticZScoreForKnownTerms { get; set; } = 2.0;

    /// <summary>
    /// Per-result cut, once the channel has been admitted by the gate above.
    ///
    /// Deliberately much looser than <see cref="MinTopSemanticZScore"/>. The strict gate has already
    /// established that this query means something to this corpus; the job here is only to bound how
    /// many candidates the channel contributes, and rank fusion sorts out the ordering. Set at 1.5
    /// this cost a gold pair its place in the top twenty for no gain in precision.
    /// </summary>
    public double MinSemanticZScore { get; set; } = 1.0;

    /// <summary>
    /// Below this many comparable vectors the mean and standard deviation are not meaningful, so
    /// the absolute floor is used instead.
    /// </summary>
    public int MinSamplesForSemanticDistribution { get; set; } = 10;

    /// <summary>Floor for the lexical channel's score, relative to the best-scoring candidate.</summary>
    public double MinLexicalScore { get; set; } = 0.08;

    /// <summary>
    /// Share of the query's trigrams that must appear in a memory for the typo net to fire. Only
    /// consulted when the term-based pass scored that memory at zero.
    ///
    /// This is an overlap coefficient, not Jaccard. Under Jaccard a short query against a whole
    /// memory scores around 0.05 even on a perfect match — indistinguishable from two unrelated
    /// English sentences, which is why the previous threshold had to be set low enough to admit the
    /// entire store and turn the channel's ranking into noise.
    /// </summary>
    /// <remarks>
    /// Calibrated against realistic typing: "bouldring saturdys" against a memory recording that the
    /// user goes bouldering on Saturdays overlaps at 0.45, while the gibberish query used by the
    /// nonsense-query test overlaps at 0.00. 0.40 sits in that gap with room on both sides.
    /// </remarks>
    public double MinTrigramSimilarity { get; set; } = 0.40;

    /// <summary>Maximum hits contributed by any single channel.</summary>
    public int MaxCandidatesPerChannel { get; set; } = 200;

    public double VectorChannelWeight { get; set; } = 1.0;
    public double LexicalChannelWeight { get; set; } = 1.0;

    /// <summary>Exact structured slot matches are the strongest available evidence.</summary>
    public double SlotChannelWeight { get; set; } = 1.25;

    public double RecencyChannelWeight { get; set; } = 0.4;
    public double LinkChannelWeight { get; set; } = 0.3;

    /// <summary>MMR trade-off; 1.0 is pure relevance.</summary>
    public double DiversityLambda { get; set; } = 0.75;

    /// <summary>Whether retrieval updates access time and strength.</summary>
    public bool ReinforceOnRead { get; set; } = true;
}
