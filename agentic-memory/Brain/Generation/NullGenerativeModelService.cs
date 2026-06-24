using AgenticMemory.Brain.Interfaces;

namespace AgenticMemory.Brain.Generation;

public sealed class NullGenerativeModelService : IGenerativeModelService
{
    public static readonly NullGenerativeModelService Instance = new();

    public bool IsAvailable => false;

    public string Generate(string systemPrompt, string userPrompt) =>
        throw new InvalidOperationException("Generative model service is not available.");

    public IAsyncEnumerable<string> GenerateStreamingAsync(
        string systemPrompt, string userPrompt, CancellationToken ct = default) =>
        throw new InvalidOperationException("Generative model service is not available.");

    public void Dispose() { }
}
