namespace AgenticMemory.Brain.Interfaces;

public interface IGenerativeModelService : IDisposable
{
    bool IsAvailable { get; }
    string Generate(string systemPrompt, string userPrompt);
    IAsyncEnumerable<string> GenerateStreamingAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}
