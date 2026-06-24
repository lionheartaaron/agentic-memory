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
}

/// <summary>
/// Server configuration settings
/// </summary>
public class ServerSettings
{
    public int Port { get; set; } = 3377;
    public string BindAddress { get; set; } = "0.0.0.0";
}

/// <summary>
/// Storage configuration settings
/// </summary>
public class StorageSettings
{
    public string DatabasePath { get; set; } = "./Data/agentic-memory.db";
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
    public bool Enabled { get; set; } = true;
    public string ModelsPath { get; set; } = "./Models/Embedding";
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
    /// Enable automatic decay and pruning
    /// </summary>
    public bool DecayEnabled { get; set; } = true;

    /// <summary>
    /// Hours between decay operations
    /// </summary>
    public int DecayIntervalHours { get; set; } = 24;

    /// <summary>
    /// Memories with strength below this value will be pruned
    /// </summary>
    public double PruneThreshold { get; set; } = 0.1;

    /// <summary>
    /// Enable automatic memory consolidation
    /// </summary>
    public bool ConsolidationEnabled { get; set; } = true;

    /// <summary>
    /// Hours between consolidation operations
    /// </summary>
    public int ConsolidationIntervalHours { get; set; } = 24;

    /// <summary>
    /// Similarity threshold for consolidating memories (0.0-1.0)
    /// </summary>
    public double SimilarityThreshold { get; set; } = 0.8;

    /// <summary>
    /// Minutes to wait after startup before running first maintenance task
    /// </summary>
    public int InitialDelayMinutes { get; set; } = 5;
}

/// <summary>
/// Settings for the local generative model (Phi-4-mini-instruct via OnnxRuntimeGenAI).
/// </summary>
public class GenerationSettings
{
    public bool Enabled { get; set; } = true;
    public string ModelsPath { get; set; } = "./Models/Generative/Phi-4-mini-instruct";
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
public class ConflictSettings
{
    /// <summary>
    /// Similarity threshold for detecting duplicate memories (0.0-1.0).
    /// Memories above this threshold are considered duplicates and will reinforce the existing memory.
    /// </summary>
    public double DuplicateSimilarityThreshold { get; set; } = 0.95;

    /// <summary>
    /// Similarity threshold for superseding memories (0.0-1.0).
    /// When a new memory's similarity to an existing memory is above this threshold but below
    /// the duplicate threshold, the old memory is superseded (archived) and replaced by the new one.
    /// This is ideal for AI agents where updated information should replace outdated facts.
    /// </summary>
    public double SupersedeSimilarityThreshold { get; set; } = 0.8;

    /// <summary>
    /// Similarity threshold for detecting related memories that should coexist (0.0-1.0).
    /// Memories above this threshold but below the supersede threshold are stored as related but coexisting.
    /// </summary>
    public double CoexistSimilarityThreshold { get; set; } = 0.6;
}
