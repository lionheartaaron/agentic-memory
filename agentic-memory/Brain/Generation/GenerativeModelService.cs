using System.Runtime.CompilerServices;
using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace AgenticMemory.Brain.Generation;

public sealed class GenerativeModelService : IGenerativeModelService
{
    private readonly GenerationSettings _opts;
    private readonly ILogger<GenerativeModelService>? _logger;
    private readonly Model? _model;
    private readonly Tokenizer? _tokenizer;
    private bool _disposed;

    // ONNX GenAI is not re-entrant: concurrent Generate/Stream calls on the same Model crash.
    // A single semaphore serializes all callers (HTTP endpoint + SummaryWorker background thread).
    private readonly SemaphoreSlim _modelLock = new(1, 1);

    public bool IsAvailable => _model is not null && !_disposed;

    public GenerativeModelService(GenerationSettings opts, ILogger<GenerativeModelService>? logger = null)
    {
        _opts = opts;
        _logger = logger;

        if (!opts.Enabled) return;

        try
        {
            // Model() takes the DIRECTORY containing genai_config.json, not the .onnx file itself.
            _model = new Model(opts.ModelsPath);
            _tokenizer = new Tokenizer(_model);
            _logger?.LogInformation("Generative model service initialized from {Path}", opts.ModelsPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize generative model service from {Path}", opts.ModelsPath);
        }
    }

    public string Generate(string systemPrompt, string userPrompt)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Generative model service is not available.");

        _modelLock.Wait();
        try
        {
            return RunGenerate(systemPrompt, userPrompt);
        }
        finally
        {
            _modelLock.Release();
        }
    }

    private string RunGenerate(string systemPrompt, string userPrompt)
    {
        // Phi-4-mini-instruct chat template
        var prompt = $"<|system|>{systemPrompt}<|end|><|user|>{userPrompt}<|end|><|assistant|>";

        var inputTokens = _tokenizer!.Encode(prompt);
        var inputLength = inputTokens[0].Length;

        // max_length is the total token budget (input + output), computed dynamically
        // so long prompts don't exhaust the budget before any output is generated.
        var maxLength = inputLength + _opts.MaxNewTokens;
        _logger?.LogDebug("Generating: {InputTokens} input tokens, max_length={MaxLength}", inputLength, maxLength);

        using var generatorParams = new GeneratorParams(_model!);
        generatorParams.SetSearchOption("max_length", (double)maxLength);
        generatorParams.SetSearchOption("temperature", (double)_opts.Temperature);
        generatorParams.SetSearchOption("top_p", (double)_opts.TopP);

        using var generator = new Generator(_model!, generatorParams);
        generator.AppendTokenSequences(inputTokens);

        var output = new StringBuilder();
        using var stream = _tokenizer.CreateStream();

        var tokensGenerated = 0;
        while (!generator.IsDone() && tokensGenerated < _opts.MaxNewTokens)
        {
            generator.GenerateNextToken();
            var newToken = generator.GetSequence(0)[^1];
            output.Append(stream.Decode(newToken));
            tokensGenerated++;
        }

        return output.ToString();
    }

    public async IAsyncEnumerable<string> GenerateStreamingAsync(
        string systemPrompt,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Generative model service is not available.");

        await _modelLock.WaitAsync(ct);
        try
        {
            var prompt = $"<|system|>{systemPrompt}<|end|><|user|>{userPrompt}<|end|><|assistant|>";
            var inputTokens = _tokenizer!.Encode(prompt);
            var inputLength = inputTokens[0].Length;

            var maxLength = inputLength + _opts.MaxNewTokens;
            _logger?.LogDebug("Streaming: {InputTokens} input tokens, max_length={MaxLength}", inputLength, maxLength);

            using var generatorParams = new GeneratorParams(_model!);
            generatorParams.SetSearchOption("max_length", (double)maxLength);
            generatorParams.SetSearchOption("temperature", (double)_opts.Temperature);
            generatorParams.SetSearchOption("top_p", (double)_opts.TopP);

            using var generator = new Generator(_model!, generatorParams);
            generator.AppendTokenSequences(inputTokens);

            using var stream = _tokenizer.CreateStream();
            var tokensGenerated = 0;

            while (!generator.IsDone() && tokensGenerated < _opts.MaxNewTokens)
            {
                ct.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                var newToken = generator.GetSequence(0)[^1];
                var text = stream.Decode(newToken);
                tokensGenerated++;

                if (!string.IsNullOrEmpty(text))
                {
                    yield return text;
                    await Task.Yield();
                }
            }
        }
        finally
        {
            _modelLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _modelLock.Dispose();
        _tokenizer?.Dispose();
        _model?.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
