namespace AgenticMemory.Brain.Interfaces;

/// <summary>
/// Generic key-value store for persisting arbitrary string values.
/// </summary>
public interface IKeyValueStore
{
    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}
