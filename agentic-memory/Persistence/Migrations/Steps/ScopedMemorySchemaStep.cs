using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Storage;
using Microsoft.Extensions.Logging;

namespace AgenticMemory.Persistence.Migrations.Steps;

/// <summary>
/// v2 — brings memories written before scoping up to the scoped schema.
///
/// Most new fields need no work: LiteDB leaves absent members at their property initialiser, so
/// legacy rows land on the safe defaults (user "default", Global visibility, subject "user",
/// Semantic type, UserStated source). Two things do need explicit handling:
///
///   * <c>IsArchived</c> is no longer persisted — it is now a view over <see cref="MemoryState"/>.
///     Left alone, every archived memory would silently resurrect as Active, so the old boolean is
///     read from the raw BSON before the typed mapper ever sees the document.
///   * Embedding dimensions were never recorded, so they are recovered from the stored byte length
///     and the model stamp is left null, which the comparison guard treats as "unknown but usable
///     if the dimension matches".
///
/// This is the one step that is <b>not</b> safe to run twice, and the reason the runner adopts the
/// version recorded by the previous versioning scheme rather than treating those databases as
/// unversioned: on a second pass the archived flag is already gone from the documents, so every
/// archived memory would come back as active.
/// </summary>
public sealed class ScopedMemorySchemaStep : IMigrationStep
{
    public int    Version => 2;
    public string Name    => "scoped-memory-schema";

    public int Apply(MigrationContext context)
    {
        var database = context.Database;
        var raw      = database.GetCollection(LiteDbMemoryRepository.CollectionName);

        if (raw.Count() == 0) return 0;

        // Pass 1 — read the raw BSON before the typed mapper gets near it.
        //
        // Two different reasons a field has to be inspected here rather than on the entity:
        //
        //   * IsArchived no longer exists on the entity at all, so the mapper simply drops it.
        //   * IngestedAt and ValidFrom do exist, but their property initialisers are DateTime.UtcNow.
        //     An absent field therefore deserialises to *now*, not to default — so "if it is default,
        //     fall back to CreatedAt" can never fire, and every pre-scoping memory would claim it
        //     became true at the moment of the upgrade. That is not cosmetic: ValidFrom is what
        //     as-of queries filter on, so an entire history would drop out of any question asked
        //     about a date before the update. Presence has to be read from the document itself.
        var legacy = new Dictionary<Guid, LegacyFields>();
        foreach (var document in raw.FindAll())
        {
            if (!document.TryGetValue("_id", out var idValue)) continue;

            Guid id;
            try { id = idValue.AsGuid; } catch { continue; }

            legacy[id] = new LegacyFields(
                Archived:      document.TryGetValue("IsArchived", out var archived) && archived.IsBoolean
                                   ? archived.AsBoolean
                                   : null,
                HasIngestedAt: document.TryGetValue("IngestedAt", out var ingested) && ingested.IsDateTime,
                HasValidFrom:  document.TryGetValue("ValidFrom",  out var validFrom) && validFrom.IsDateTime);
        }

        // Pass 2 — typed rewrite, so enum representation stays consistent with the mapper.
        var typed    = database.GetCollection<MemoryNodeEntity>(LiteDbMemoryRepository.CollectionName);
        var migrated = 0;
        var batch    = new List<MemoryNodeEntity>();

        foreach (var memory in typed.FindAll())
        {
            var fields = legacy.TryGetValue(memory.Id, out var found) ? found : LegacyFields.Unknown;

            memory.UserId     = MemoryScope.NormalizeUser(memory.UserId);
            memory.SubjectRef = SubjectRefs.Normalize(memory.SubjectRef);

            // State: superseded beats archived, since ValidUntil only ever came from supersede.
            memory.State = memory.ValidUntil.HasValue
                ? MemoryState.Superseded
                : fields.Archived == true ? MemoryState.Archived : MemoryState.Active;

            if (!fields.HasIngestedAt) memory.IngestedAt = memory.CreatedAt;
            if (!fields.HasValidFrom)  memory.ValidFrom  = memory.CreatedAt;
            if (memory.Version    == 0) memory.Version    = 1;
            if (memory.Confidence <= 0) memory.Confidence = 1.0;

            if (memory.EmbeddingBytes is { Length: > 0 } && memory.EmbeddingDim == 0)
                memory.EmbeddingDim = memory.EmbeddingBytes.Length / sizeof(float);

            if (memory.Visibility == MemoryVisibility.Scoped && memory.CompanionIds.Count == 0)
                memory.Visibility = MemoryVisibility.Global;

            // Rebuild derived search fields under the new single definition — in particular
            // SearchText, which did not previously exist and now carries full content.
            MemoryTextIndexer.ApplyTextIndex(memory);

            batch.Add(memory);
            migrated++;

            if (batch.Count >= 500)
            {
                typed.Update(batch);
                batch.Clear();
            }
        }

        if (batch.Count > 0) typed.Update(batch);

        context.Logger?.LogInformation("Scoped-memory migration rewrote {Count} memories", migrated);
        return migrated;
    }

    /// <summary>
    /// What the stored document actually said, as opposed to what the entity defaults to.
    /// <c>Archived</c> is null when the old boolean was not there at all.
    /// </summary>
    private readonly record struct LegacyFields(bool? Archived, bool HasIngestedAt, bool HasValidFrom)
    {
        /// <summary>A document the raw pass could not key — treat every field as absent.</summary>
        public static LegacyFields Unknown => new(Archived: null, HasIngestedAt: false, HasValidFrom: false);
    }
}
