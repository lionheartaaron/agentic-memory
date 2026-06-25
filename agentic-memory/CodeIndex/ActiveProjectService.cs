using AgenticMemory.Brain.Interfaces;

namespace AgenticMemory.CodeIndex;

/// <summary>
/// Singleton tracking the single project currently being watched and indexed.
/// Persisted in the KV store so the last active project resumes on restart.
/// </summary>
public sealed class ActiveProjectService
{
    private readonly IKeyValueStore _kv;
    private string? _activeProjectId;
    private const string KvKey = "codeindex.activeProjectId";

    public ActiveProjectService(IKeyValueStore kv) => _kv = kv;

    public string? ActiveProjectId => _activeProjectId;

    /// <summary>Fires whenever the active project changes. Null means deactivated.</summary>
    public event Action<string?>? ActiveProjectChanged;

    public void SetActive(string? projectId)
    {
        _activeProjectId = projectId;
        if (string.IsNullOrEmpty(projectId))
            _kv.Delete(KvKey);
        else
            _kv.Set(KvKey, projectId);
        ActiveProjectChanged?.Invoke(projectId);
    }

    /// <summary>Restore the persisted active project on startup — does NOT fire the event.</summary>
    public void Load()
    {
        var stored = _kv.Get(KvKey);
        if (!string.IsNullOrEmpty(stored))
            _activeProjectId = stored;
    }
}
