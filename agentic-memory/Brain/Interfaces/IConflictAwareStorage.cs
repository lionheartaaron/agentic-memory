using AgenticMemory.Brain.Models;

namespace AgenticMemory.Brain.Interfaces;

public interface IConflictAwareStorage
{
    Task<StoreResult> StoreAsync(MemoryNodeEntity entity, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemoryNodeEntity>> GetTagHistoryAsync(
        string tag,
        bool includeArchived = true,
        CancellationToken cancellationToken = default);
}
