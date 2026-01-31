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
            var sessionOptions = new SessionOptions
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

    private string PreprocessText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // Normalize whitespace
        text = WhitespaceRegex().Replace(text, " ");

        // Trim and limit length (rough character limit before tokenization)
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
