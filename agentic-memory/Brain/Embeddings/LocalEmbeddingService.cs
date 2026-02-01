using System.Text.RegularExpressions;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Search;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace AgenticMemory.Brain.Embeddings;

/// <summary>
/// Local embedding service using SBERT model via ONNX Runtime
/// </summary>
public partial class LocalEmbeddingService : IEmbeddingService
{
    private readonly ILogger<LocalEmbeddingService>? _logger;
    private readonly InferenceSession? _session;
    private readonly Tokenizer? _tokenizer;
    private readonly int _dimensions;
    private readonly int _maxSequenceLength;
    private readonly bool _isAvailable;
    private bool _disposed;

    public int Dimensions => _dimensions;
    public bool IsAvailable => _isAvailable && !_disposed;

    public LocalEmbeddingService(EmbeddingsSettings settings, ILogger<LocalEmbeddingService>? logger = null)
    {
        _logger = logger;
        _dimensions = settings.ModelDimensions;
        _maxSequenceLength = settings.MaxSequenceLength;

        var modelPath = settings.GetModelPath();
        var vocabPath = settings.GetVocabPath();

        if (!File.Exists(modelPath))
        {
            _logger?.LogWarning("ONNX model not found at {ModelPath}. Embedding service will be unavailable.", modelPath);
            _isAvailable = false;
            return;
        }

        if (!File.Exists(vocabPath))
        {
            _logger?.LogWarning("Vocabulary file not found at {VocabPath}. Embedding service will be unavailable.", vocabPath);
            _isAvailable = false;
            return;
        }

        try
        {
            var sessionOptions = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
            };

            _session = new InferenceSession(modelPath, sessionOptions);

            // Load the BERT tokenizer using Microsoft.ML.Tokenizers
            _tokenizer = BertTokenizer.Create(vocabPath);

            _isAvailable = true;
            _logger?.LogInformation("Local embedding service initialized with model at {ModelPath}", modelPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize local embedding service");
            _isAvailable = false;
        }
    }

    public Task<float[]> GetEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Embedding service is not available. Ensure the ONNX model and vocabulary files are present.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Preprocess text
        var processedText = PreprocessText(text);

        // Tokenize
        var tokens = Tokenize(processedText);

        // Run inference
        var embedding = RunInference(tokens);

        return Task.FromResult(embedding);
    }

    /// <summary>
    /// Preprocesses text for embedding generation.
    /// Normalizes to ASCII-safe characters to ensure tokenizer compatibility.
    /// </summary>
    private string PreprocessText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // 0. Remove unpaired surrogates FIRST - Regex will throw ArgumentException on invalid UTF-16
        // Unpaired surrogates (e.g., \uD83D without its pair) are invalid and crash the regex engine
        text = RemoveUnpairedSurrogates(text);

        // 1. Remove ALL non-ASCII characters using regex (most robust approach)
        // This handles emojis, surrogates, CJK, etc. in one simple operation
        text = NonAsciiRegex().Replace(text, "");

        // 2. Normalize URLs to placeholder (preserves semantic meaning without URL noise)
        text = UrlRegex().Replace(text, " [URL] ");

        // 3. Normalize email addresses to placeholder
        text = EmailRegex().Replace(text, " [EMAIL] ");

        // 4. Collapse repeated punctuation (e.g., "!!!" -> "!", "..." -> ".")
        text = RepeatedPunctuationRegex().Replace(text, "$1");

        // 5. Normalize whitespace (collapse multiple spaces, tabs, newlines)
        text = WhitespaceRegex().Replace(text, " ");

        // 6. Trim and limit length (rough character limit before tokenization)
        text = text.Trim();
        if (text.Length > 1024)
        {
            text = text[..1024];
        }

        return text;
    }

    private (long[] inputIds, long[] attentionMask, long[] tokenTypeIds) Tokenize(string text)
    {
        // Use the BERT tokenizer
        var encoding = _tokenizer!.EncodeToIds(text, _maxSequenceLength, out _, out _);
        var tokenIds = encoding.ToArray();


        // Ensure we don't exceed max length
        var length = Math.Min(tokenIds.Length, _maxSequenceLength);

        var inputIds = new long[length];
        var attentionMask = new long[length];
        var tokenTypeIds = new long[length];

        for (int i = 0; i < length; i++)
        {
            inputIds[i] = tokenIds[i];
            attentionMask[i] = 1;
            tokenTypeIds[i] = 0;
        }

        return (inputIds, attentionMask, tokenTypeIds);
    }

    private float[] RunInference((long[] inputIds, long[] attentionMask, long[] tokenTypeIds) tokens)
    {
        var sequenceLength = tokens.inputIds.Length;

        // Create input tensors
        var inputIdsTensor = new DenseTensor<long>(tokens.inputIds, [1, sequenceLength]);
        var attentionMaskTensor = new DenseTensor<long>(tokens.attentionMask, [1, sequenceLength]);
        var tokenTypeIdsTensor = new DenseTensor<long>(tokens.tokenTypeIds, [1, sequenceLength]);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", inputIdsTensor),
            NamedOnnxValue.CreateFromTensor("attention_mask", attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor("token_type_ids", tokenTypeIdsTensor)
        };


        // Run inference
        using var results = _session!.Run(inputs);

        // Get the output (last_hidden_state or sentence_embedding depending on model)
        var outputTensor = results.First().AsTensor<float>();

        // Mean pooling over tokens to get sentence embedding
        var embedding = MeanPooling(outputTensor, tokens.attentionMask, sequenceLength);

        // L2 normalize the embedding
        return VectorMath.Normalize(embedding);
    }

    private static float[] MeanPooling(Tensor<float> hiddenStates, long[] attentionMask, int sequenceLength)
    {
        var hiddenSize = hiddenStates.Dimensions[^1];
        var embedding = new float[hiddenSize];

        float tokenCount = 0;

        for (int t = 0; t < sequenceLength; t++)
        {
            if (attentionMask[t] == 0) continue;

            tokenCount++;
            for (int h = 0; h < hiddenSize; h++)
            {
                embedding[h] += hiddenStates[0, t, h];
            }
        }

        if (tokenCount > 0)
        {
            for (int h = 0; h < hiddenSize; h++)
            {
                embedding[h] /= tokenCount;
            }
        }

        return embedding;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _session?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }

    // Regex patterns for text preprocessing (compiled at build time for performance)

    /// <summary>
    /// Removes unpaired surrogate characters that would cause Regex to throw ArgumentException.
    /// In UTF-16, surrogates must appear in pairs (high surrogate 0xD800-0xDBFF followed by low surrogate 0xDC00-0xDFFF).
    /// An unpaired surrogate is invalid UTF-16 and crashes the .NET Regex engine.
    /// </summary>
    private static string RemoveUnpairedSurrogates(string text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        // Use StringBuilder for efficient character-by-character processing
        var sb = new System.Text.StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (char.IsHighSurrogate(c))
            {
                // High surrogate must be followed by a low surrogate
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // Valid surrogate pair - include both characters
                    sb.Append(c);
                    sb.Append(text[i + 1]);
                    i++; // Skip the low surrogate in next iteration
                }
                // else: Unpaired high surrogate - skip it
            }
            else if (char.IsLowSurrogate(c))
            {
                // Low surrogate without preceding high surrogate - skip it
            }
            else
            {
                // Regular character - include it
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Matches any character outside printable ASCII range (0x20-0x7E) and common whitespace.
    /// This removes emojis, surrogates, CJK characters, extended Latin, and all other non-ASCII.
    /// </summary>
    [GeneratedRegex(@"[^\x20-\x7E\t\n\r]")]
    private static partial Regex NonAsciiRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"https?://[^\s]+|www\.[^\s]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"([!?.,;:])\1+")]
    private static partial Regex RepeatedPunctuationRegex();
}
