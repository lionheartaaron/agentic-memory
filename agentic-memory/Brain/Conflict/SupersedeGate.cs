using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Slots;

namespace AgenticMemory.Brain.Conflict;

public enum SupersedeDecision
{
    /// <summary>Same slot, same value: reinforce the existing memory instead of duplicating it.</summary>
    Duplicate,

    /// <summary>Legally replaces the existing memory, which is retained as history.</summary>
    Supersede,

    /// <summary>A real contradiction that must not be resolved automatically.</summary>
    Conflict,

    /// <summary>Not a contradiction at all. Both remain active.</summary>
    Coexist,
}

public sealed record SupersedeVerdict(
    SupersedeDecision Decision,
    string Reason,
    ConflictKind? ConflictKind = null);

/// <summary>
/// Decides whether one memory may replace another.
///
/// This replaces similarity thresholding entirely. The old rule archived an existing memory
/// whenever cosine similarity cleared a configured 0.80 — which, because scores were mapped
/// through (cos+1)/2, was raw cosine 0.60. That is "same topic", not "contradicts": "I love pizza"
/// and "I love pasta" clear it comfortably, so storing one food preference deleted another.
///
/// Similarity is now only used to <em>propose</em> candidates. Whether replacement is legal is a
/// deterministic property of the slot, the subject, the scope and the provenance.
/// </summary>
public sealed class SupersedeGate(SlotRegistry slots)
{
    private readonly SlotRegistry _slots = slots;

    public SupersedeVerdict Evaluate(MemoryNodeEntity incoming, MemoryNodeEntity existing)
    {
        if (incoming.Id == existing.Id)
            return new(SupersedeDecision.Coexist, "same memory");

        // 1. Tenancy. Should be unreachable — candidates are already scope-filtered — but this is
        //    the one boundary worth checking twice.
        if (!string.Equals(incoming.UserId, existing.UserId, StringComparison.Ordinal))
            return new(SupersedeDecision.Coexist, "different user");

        // 2. Subject. "The user's favourite colour" and "Aria's favourite colour" are near-identical
        //    vectors; without this axis one archives the other.
        if (!string.Equals(incoming.SubjectRef, existing.SubjectRef, StringComparison.OrdinalIgnoreCase))
            return new(SupersedeDecision.Coexist, "different subject");

        // 3. Persona is immutable: conversation must never talk a companion out of their own identity.
        if (existing.Type == MemoryType.Persona && incoming.Type != MemoryType.Persona)
            return new(SupersedeDecision.Conflict, "cannot overwrite persona from conversation",
                ConflictKind.ImmutableViolation);

        // 4. Both must assert the same structured slot. Unslotted free text always coexists.
        var predicate = SlotRegistry.Normalize(incoming.Predicate);
        if (predicate is null || !string.Equals(predicate, SlotRegistry.Normalize(existing.Predicate), StringComparison.Ordinal))
            return new(SupersedeDecision.Coexist, "no shared structured slot");

        var slot = _slots.Resolve(predicate);

        // 5. Same slot, same value: a restatement, not a change.
        if (incoming.ValueKey is not null &&
            string.Equals(incoming.ValueKey, existing.ValueKey, StringComparison.Ordinal))
            return new(SupersedeDecision.Duplicate, "same slot and value");

        // 6. Multi-valued slots never supersede. A second pet does not delete the first.
        if (slot.Cardinality == SlotCardinality.MultiValued)
            return new(SupersedeDecision.Coexist, $"'{predicate}' holds multiple values");

        // 7. Scope must not narrow. This is the rule that matters most with several companions: a
        //    private conversation with one of them must never archive a memory all of them share,
        //    because that erases knowledge from every other companion.
        if (existing.Visibility == MemoryVisibility.Global && incoming.Visibility == MemoryVisibility.Scoped)
            return new(SupersedeDecision.Conflict,
                "a companion-scoped memory cannot replace one shared by all companions",
                ConflictKind.CrossScopeContradiction);

        if (existing.Visibility == MemoryVisibility.Scoped && incoming.Visibility == MemoryVisibility.Scoped &&
            !existing.CompanionIds.ToHashSet(StringComparer.Ordinal).SetEquals(incoming.CompanionIds))
            return new(SupersedeDecision.Conflict, "different companion scopes",
                ConflictKind.CrossScopeContradiction);

        // 8. Provenance must not weaken: an inference must never silently overwrite something the
        //    user stated outright.
        if (incoming.Source.TrustRank() < existing.Source.TrustRank())
            return new(SupersedeDecision.Conflict,
                $"{incoming.Source} cannot overwrite {existing.Source}",
                ConflictKind.ProvenanceDowngrade);

        // 9. Slot policy.
        return slot.Policy switch
        {
            ConflictPolicy.Immutable =>
                new(SupersedeDecision.Conflict, $"'{predicate}' is immutable", ConflictKind.ImmutableViolation),

            ConflictPolicy.EscalateToUser =>
                new(SupersedeDecision.Conflict, $"'{predicate}' change should be confirmed",
                    slot.Cardinality == SlotCardinality.SingularSoft
                        ? ConflictKind.SoftPreferenceChange
                        : ConflictKind.ValueReplaced),

            _ => slot.Cardinality == SlotCardinality.SingularSoft
                ? new(SupersedeDecision.Conflict, $"'{predicate}' change should be confirmed",
                    ConflictKind.SoftPreferenceChange)
                : new(SupersedeDecision.Supersede, $"'{predicate}' is singular and latest wins"),
        };
    }
}
