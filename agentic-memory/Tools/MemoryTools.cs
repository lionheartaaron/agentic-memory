using System.ComponentModel;
using System.Text;
using AgenticMemory.Brain.Interfaces;
using AgenticMemory.Brain.Models;
using AgenticMemory.Brain.Retrieval;
using AgenticMemory.Brain.Slots;
using AgenticMemory.Brain.Storage;
using AgenticMemory.Configuration;
using ModelContextProtocol.Server;

namespace AgenticMemory.Tools;

/// <summary>
/// MCP tools for the memory system.
///
/// Every tool takes <c>user_id</c> and <c>companion_id</c>. They are the privacy boundary, not a
/// filter: a memory scoped to one companion is invisible to the others, and cross-user access is
/// impossible through this surface.
/// </summary>
[McpServerToolType]
public class MemoryTools(
    IMemoryRepository repository,
    ISearchService searchService,
    IMemoryEventLog eventLog,
    SlotRegistry slots,
    IConflictAwareStorage? conflictStorage = null,
    StorageSettings? storageSettings = null)
{
    private readonly StorageSettings _storageSettings = storageSettings ?? new StorageSettings();

    [McpServerTool(Name = "search_memories")]
    [Description("""
        Search memories by meaning and by text. Call this BEFORE storing anything, to check what is
        already known. Results are restricted to what the given companion is allowed to recall:
        memories shared by all companions, plus that companion's own private ones.
        Returns a confidence level — when it is Low or None, say you do not remember rather than guessing.
        """)]
    public async Task<string> SearchMemories(
        [Description("Natural language query. Concepts, keywords or a question.")] string query,
        [Description("The user whose memories to search. Required for isolation between users.")] string user_id,
        [Description("The companion doing the asking. Omit to search only memories shared by all companions.")] string? companion_id = null,
        [Description("Maximum results (1-100)")] int top_n = 5,
        [Description("Only memories carrying at least one of these tags (exact match)")] string[]? tags = null,
        [Description("Only memories about this subject: 'user', 'companion:<id>', 'person:<name>'")] string? subject = null,
        [Description("Only memories asserting this structured slot, e.g. 'employer'")] string? predicate = null,
        [Description("Also return always-on identity and persona memories")] bool include_core_context = false,
        [Description("Answer as of a past moment (ISO-8601, e.g. '2025-01-01T00:00:00Z') — returns what was true then, including facts since replaced. Use for 'where did I work last year'.")] string? as_of = null,
        [Description("0-1: how strongly to prefer memories this companion has not already brought up. Reorders equally relevant memories; never hides an answer.")] double novelty_bias = 0,
        CancellationToken cancellationToken = default)
    {
        DateTime? asOf = null;
        if (!string.IsNullOrWhiteSpace(as_of))
        {
            if (!DateTime.TryParse(as_of, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var parsed))
                return $"Could not parse as_of '{as_of}'. Use ISO-8601, e.g. '2025-01-01T00:00:00Z'.";

            asOf = parsed;
        }

        var result = await searchService.RetrieveAsync(new RetrievalRequest
        {
            Query              = query,
            Scope              = MemoryScope.For(user_id, companion_id),
            TopN               = Math.Clamp(top_n, 1, 100),
            Tags               = tags,
            SubjectRef         = subject,
            Predicate          = predicate,
            IncludeCoreContext = include_core_context,
            AsOf               = asOf,
            NoveltyBias        = Math.Clamp(novelty_bias, 0, 1),
        }, cancellationToken);

        if (result.Results.Count == 0 && result.CoreContext.Count == 0)
            return $"No memories found. (Searched {result.CandidatesConsidered} memories in scope; confidence: None.)";

        var sb = new StringBuilder();
        sb.AppendLine($"Confidence: {result.Confidence}. Searched {result.CandidatesConsidered} memories in scope.");

        if (asOf is { } instant)
            sb.AppendLine($"Answering as of {instant:u} — this is what was true then, not necessarily now.");

        if (result.IncomparableEmbeddings > 0)
            sb.AppendLine($"Note: {result.IncomparableEmbeddings} memories have vectors from a different embedding model and were skipped. A reindex is needed.");

        if (result.CoreContext.Count > 0)
        {
            sb.AppendLine().AppendLine("## Always known");
            foreach (var c in result.CoreContext)
                sb.AppendLine($"- {c.Memory.Title}: {c.Memory.Summary}");
        }

        if (result.Results.Count > 0)
        {
            sb.AppendLine().AppendLine("## Recalled");
            foreach (var r in result.Results)
                sb.AppendLine(Format(r));
        }

        if (result.Conflicts.Count > 0)
        {
            sb.AppendLine().AppendLine("## Unresolved contradictions, worth asking the user about");
            foreach (var c in result.Conflicts)
                sb.AppendLine($"- [{c.Id}] {c.Description}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Format(ScoredMemory r)
    {
        var scope = r.Memory.Visibility == MemoryVisibility.Global
            ? "shared"
            : $"private to {string.Join("/", r.Memory.CompanionIds)}";

        var slot = r.Memory.Predicate is null ? "" : $" | slot: {r.Memory.Predicate}";

        // Whether this companion has said it before, so she can refer back to it rather than
        // announcing the same fact again.
        var told = r.TimesSurfacedToCompanion switch
        {
            0 => " | you have not mentioned this before",
            1 => " | you have mentioned this once already",
            _ => $" | you have mentioned this {r.TimesSurfacedToCompanion} times already",
        };

        // When a fact was recorded is not a detail — it is the only thing that separates a correction
        // from the thing it corrected. Two memories about one subject routinely tie on relevance, and
        // without a date the reader has no way to tell which one the user said most recently.
        // To the second, because the two memories that most need separating are a statement and the
        // correction that followed it, and those often arrive within the same minute.
        var recorded = $" | recorded {r.Memory.ValidFrom:yyyy-MM-dd HH:mm:ss} UTC";

        var contested = r.IsContradicted
            ? "\n! Another memory in these results contradicts this one. Do not answer from it alone — "
              + "read both, prefer the more recently recorded, and check the contradictions listed below."
            : "";

        return $"""
            **{r.Memory.Title}** (relevance {r.Score:F2}, via {string.Join("+", r.MatchedChannels)})
            ID: {r.Memory.Id}
            {r.Memory.Summary}
            about: {r.Memory.SubjectRef} | {scope}{slot} | source: {r.Memory.Source}{recorded}{told}{contested}
            """;
    }

    [McpServerTool(Name = "store_memory")]
    [Description("""
        Store a memory. Search first — restatements reinforce the existing memory instead of duplicating.

        Scope decides who can recall it. Use visibility 'global' for facts every companion should know
        ("the user is allergic to shellfish"), and 'private' for anything belonging to one relationship
        (an inside joke, something said in confidence to that companion).

        Set 'predicate' whenever the memory asserts a known attribute (employer, city_of_residence,
        relationship_status, favourite_food, allergies...). That is what lets a new value correctly
        replace an old one instead of the two contradicting each other forever. Without a predicate
        memories always coexist, which is safe but leaves stale facts in place.

        Set 'source' honestly: 'companion_inferred' can never overwrite something 'user_stated'.
        """)]
    public async Task<string> StoreMemory(
        [Description("(Required) Short descriptive title, e.g. 'User works at Acme'")] string title,
        [Description("(Required) 1-2 sentence summary of the key information")] string summary,
        [Description("(Required) The user this memory belongs to")] string user_id,
        [Description("Full details and context")] string? content = null,
        [Description("The companion storing this. Required when visibility is 'private'.")] string? companion_id = null,
        [Description("'global' (all companions know) or 'private' (only this companion). Default global.")] string visibility = "global",
        [Description("Who this is about: 'user', 'companion:<id>', 'relationship:<id>', 'person:<name>'. Default 'user'.")] string subject = "user",
        [Description("Structured attribute asserted, e.g. 'employer'. Strongly recommended for facts that can change.")] string? predicate = null,
        [Description("Normalised value for the slot, e.g. 'acme'. Defaults to the summary.")] string? value = null,
        [Description("semantic | identity | preference | persona | episodic | affective | ephemeral")] string type = "semantic",
        [Description("user_stated | companion_inferred | system_derived | imported")] string source = "user_stated",
        [Description("Free-form tags for categorisation. Never used for access control.")] string[]? tags = null,
        [Description("Priority 0.0-1.0. Affects ranking only; memories are never deleted for being unimportant.")] double importance = 0.5,
        [Description("Extraction confidence 0.0-1.0")] double confidence = 1.0,
        [Description("normal | sensitive | restricted")] string sensitivity = "normal",
        [Description("Never age out of the working set")] bool pinned = false,
        [Description("For ephemeral context only: hours until this stops being true")] double? expires_in_hours = null,
        [Description("What the user actually said, preserved verbatim")] string? verbatim_quote = null,
        [Description("Conversation this came from")] string? conversation_id = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary))
            return "Error: title and summary are required.";

        if (string.IsNullOrWhiteSpace(user_id))
            return "Error: user_id is required — it is the isolation boundary between users.";

        var wantsPrivate = visibility.Equals("private", StringComparison.OrdinalIgnoreCase)
                        || visibility.Equals("scoped", StringComparison.OrdinalIgnoreCase);

        if (wantsPrivate && string.IsNullOrWhiteSpace(companion_id))
            return "Error: companion_id is required when visibility is 'private', otherwise no companion could recall it.";

        var entity = new MemoryNodeEntity
        {
            UserId        = user_id,
            Title         = Truncate(title, _storageSettings.MaxTitleLength),
            Summary       = Truncate(summary, _storageSettings.MaxSummaryLength),
            Content       = Truncate(content ?? "", _storageSettings.MaxContentSizeBytes),
            Visibility    = wantsPrivate ? MemoryVisibility.Scoped : MemoryVisibility.Global,
            CompanionIds  = wantsPrivate ? [MemoryScope.NormalizeId(companion_id)!] : [],
            SubjectRef    = SubjectRefs.Normalize(subject),
            Predicate     = SlotRegistry.Normalize(predicate),
            ValueKey      = MemoryTextIndexer.BuildValueKey(value ?? summary),
            Type          = ParseEnum(type, MemoryType.Semantic),
            Source        = ParseSource(source),
            Sensitivity   = ParseEnum(sensitivity, Sensitivity.Normal),
            Tags          = (tags ?? []).Take(_storageSettings.MaxTagsPerMemory).ToList(),
            Importance    = Math.Clamp(importance, 0.0, 1.0),
            Confidence    = Math.Clamp(confidence, 0.0, 1.0),
            IsPinned      = pinned,
            VerbatimQuote = verbatim_quote,
            ConversationId = conversation_id,
            ExpiresAt     = expires_in_hours is > 0 ? DateTime.UtcNow.AddHours(expires_in_hours.Value) : null,
        };

        var scope = MemoryScope.For(user_id, companion_id);

        if (conflictStorage is null)
        {
            await repository.SaveAsync(entity, cancellationToken);
            return $"Memory stored.\nID: {entity.Id}";
        }

        var result = await conflictStorage.StoreAsync(entity, scope, "mcp:store_memory", cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine(result.Message);
        sb.AppendLine($"ID: {result.Memory.Id}");
        sb.AppendLine($"Action: {result.Action}");

        if (result.Conflicts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Both versions were kept. Consider asking the user which is right:");
            foreach (var c in result.Conflicts)
                sb.AppendLine($"- [{c.Id}] {c.Description}");
        }

        if (entity.Predicate is null && result.Action == StoreAction.StoredNew)
            sb.AppendLine("\nTip: no 'predicate' was set, so this memory will coexist with any future contradicting value rather than replacing it.");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "update_memory")]
    [Description("Correct or extend an existing memory in place. Use this rather than storing a near-duplicate.")]
    public async Task<string> UpdateMemory(
        [Description("Memory ID")] Guid id,
        [Description("The user who owns it")] string user_id,
        [Description("Companion context, if the memory is private")] string? companion_id = null,
        [Description("Updated title")] string? title = null,
        [Description("Updated summary")] string? summary = null,
        [Description("Updated content")] string? content = null,
        [Description("Replace all tags")] string[]? tags = null,
        [Description("Pin or unpin")] bool? pinned = null,
        CancellationToken cancellationToken = default)
    {
        var scope = MemoryScope.For(user_id, companion_id);
        var existing = await repository.GetAsync(id, scope, cancellationToken);
        if (existing is null)
            return $"Error: memory {id} not found in this scope.";

        if (title is not null)   existing.Title = Truncate(title, _storageSettings.MaxTitleLength);
        if (summary is not null) existing.Summary = Truncate(summary, _storageSettings.MaxSummaryLength);
        if (content is not null) existing.Content = Truncate(content, _storageSettings.MaxContentSizeBytes);
        if (tags is not null)    existing.Tags = tags.Take(_storageSettings.MaxTagsPerMemory).ToList();
        if (pinned is not null)  existing.IsPinned = pinned.Value;

        try
        {
            // Version-guarded: every search reinforces, so concurrent access is routine.
            await repository.SaveAsync(existing, existing.Version, "mcp:update_memory", cancellationToken);
        }
        catch (MemoryConcurrencyException)
        {
            return "Error: this memory was modified concurrently. Re-read it and retry.";
        }

        return $"Memory updated.\nID: {existing.Id}\nTitle: {existing.Title}";
    }

    [McpServerTool(Name = "get_memory")]
    [Description("Retrieve one memory in full, including provenance and history links.")]
    public async Task<string> GetMemory(
        [Description("Memory ID")] Guid id,
        [Description("The user who owns it")] string user_id,
        [Description("Companion context, if the memory is private")] string? companion_id = null,
        CancellationToken cancellationToken = default)
    {
        var scope  = MemoryScope.For(user_id, companion_id);
        var memory = await repository.GetAsync(id, scope, cancellationToken);
        if (memory is null) return $"Error: memory {id} not found in this scope.";

        await repository.ReinforceAsync(id, cancellationToken);

        var scopeText = memory.Visibility == MemoryVisibility.Global
            ? "shared by all companions"
            : $"private to {string.Join(", ", memory.CompanionIds)}";

        return $"""
            **{memory.Title}**

            ID: {memory.Id}
            {memory.Summary}

            {memory.Content}

            About: {memory.SubjectRef}
            Scope: {scopeText}
            Slot: {memory.Predicate ?? "(none)"} = {memory.ValueKey ?? "(none)"}
            Type: {memory.Type} | Source: {memory.Source} | Confidence: {memory.Confidence:F2}
            State: {memory.State}{(memory.SupersededBy is { } s ? $" (superseded by {s})" : "")}
            Tags: {string.Join(", ", memory.Tags)}
            Learned: {memory.IngestedAt:yyyy-MM-dd HH:mm} UTC{(memory.EventTime is { } e ? $" | Happened: {e:yyyy-MM-dd}" : "")}
            Recalled {memory.AccessCount} times | Strength {memory.GetCurrentStrength():F2}{(memory.IsPinned ? " (pinned)" : "")}
            {(memory.VerbatimQuote is not null ? $"\nThey said: \"{memory.VerbatimQuote}\"" : "")}
            """;
    }

    [McpServerTool(Name = "forget_memory")]
    [Description("""
        Mark a memory as forgotten at the user's request. It stops being recalled immediately but is
        tombstoned rather than destroyed, so it can be restored if this was a mistake. Physical
        deletion happens later, on a retention schedule.
        """)]
    public async Task<string> ForgetMemory(
        [Description("Memory ID")] Guid id,
        [Description("The user who owns it")] string user_id,
        [Description("Companion context, if the memory is private")] string? companion_id = null,
        CancellationToken cancellationToken = default)
    {
        var scope = MemoryScope.For(user_id, companion_id);
        var ok = await repository.ForgetAsync(id, scope, "mcp:forget_memory", cancellationToken);
        return ok
            ? $"Memory {id} will no longer be recalled. It can be restored until it is purged."
            : $"Memory {id} not found in this scope.";
    }

    [McpServerTool(Name = "restore_memory")]
    [Description("Undo a forget, an archive or a supersede, bringing a memory back into recall.")]
    public async Task<string> RestoreMemory(
        [Description("Memory ID")] Guid id,
        [Description("The user who owns it")] string user_id,
        [Description("Companion context, if the memory is private")] string? companion_id = null,
        CancellationToken cancellationToken = default)
    {
        var scope = MemoryScope.For(user_id, companion_id);
        var ok = await repository.RestoreAsync(id, scope, "mcp:restore_memory", cancellationToken);
        return ok ? $"Memory {id} restored." : $"Memory {id} not found in this scope.";
    }

    [McpServerTool(Name = "get_slot_history")]
    [Description("""
        How a structured fact changed over time — every value ever recorded for a (subject, predicate)
        pair, newest first, including superseded ones. Use for "where did I used to work?".
        """)]
    public async Task<string> GetSlotHistory(
        [Description("The user")] string user_id,
        [Description("Attribute, e.g. 'employer' or 'city_of_residence'")] string predicate,
        [Description("Who it is about. Default 'user'.")] string subject = "user",
        [Description("Companion context")] string? companion_id = null,
        CancellationToken cancellationToken = default)
    {
        var scope   = MemoryScope.For(user_id, companion_id);
        var history = await repository.GetBySlotAsync(scope, subject, predicate, includeHistory: true, cancellationToken);

        if (history.Count == 0)
            return $"No memories recorded for '{predicate}' about '{subject}'.";

        var sb = new StringBuilder($"**History of '{predicate}' for {subject}** ({history.Count})\n");

        foreach (var m in history)
        {
            var status = m.State == MemoryState.Active ? "CURRENT" : m.State.ToString().ToUpperInvariant();
            var window = m.ValidUntil.HasValue
                ? $"{m.ValidFrom:yyyy-MM-dd} to {m.ValidUntil:yyyy-MM-dd}"
                : $"since {m.ValidFrom:yyyy-MM-dd}";

            sb.AppendLine($"\n[{status}] {m.Title} ({window})\n  {m.Summary}\n  ID: {m.Id}");
        }

        return sb.ToString();
    }

    [McpServerTool(Name = "get_tag_history")]
    [Description("History of memories carrying a tag, including archived ones. Prefer get_slot_history for structured facts.")]
    public async Task<string> GetTagHistory(
        [Description("The user")] string user_id,
        [Description("Tag to look up")] string tag,
        [Description("Companion context")] string? companion_id = null,
        [Description("Include archived and superseded")] bool include_archived = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "Error: tag is required.";

        var memories = await repository.QueryAsync(MemoryScope.For(user_id, companion_id), new MemoryQueryOptions
        {
            Tags              = [tag],
            IncludeNonCurrent = include_archived,
        }, cancellationToken);

        if (memories.Count == 0) return $"No memories found with tag '{tag}'.";

        var sb = new StringBuilder($"**Memories tagged '{tag}'** ({memories.Count})\n");
        foreach (var m in memories.OrderByDescending(m => m.ValidFrom))
            sb.AppendLine($"\n[{(m.State == MemoryState.Active ? "CURRENT" : m.State.ToString())}] {m.Title}\n  {m.Summary}\n  ID: {m.Id}");

        return sb.ToString();
    }

    [McpServerTool(Name = "list_conflicts")]
    [Description("""
        Contradictions the system refused to resolve on its own, each with both memories in full so
        you can see what you would be choosing between. Raising one naturally is good companion
        behaviour ("wait, I thought you were still at Acme?") and beats silently picking a side.
        Ask the user, then settle it with resolve_conflict.
        """)]
    public async Task<string> ListConflicts(
        [Description("The user")] string user_id,
        [Description("Companion context")] string? companion_id = null,
        [Description("false to include ones already settled, for auditing a past decision")] bool open_only = true,
        CancellationToken cancellationToken = default)
    {
        var scope     = MemoryScope.For(user_id, companion_id);
        var conflicts = await repository.GetConflictDetailsAsync(scope, open_only, cancellationToken);

        if (conflicts.Count == 0)
            return open_only ? "No unresolved contradictions." : "No contradictions on record.";

        var sb = new StringBuilder($"**{conflicts.Count} contradiction(s)**\n");

        foreach (var detail in conflicts)
        {
            var c = detail.Conflict;

            sb.AppendLine($"\n[{c.Id}] {c.Kind} on '{c.Predicate ?? "(unstructured)"}' about {c.SubjectRef}");
            sb.AppendLine($"  {c.Description}");

            if (c.Status != ConflictStatus.Open)
                sb.AppendLine($"  ALREADY {c.Status.ToString().ToUpperInvariant()}" +
                              (c.WinnerId is { } w ? $" in favour of {w}" : "") + ". Cannot be settled again.");

            AppendSide("EXISTING", detail.Existing);
            AppendSide("NEW",      detail.New);

            // Named so the model can quote it straight into resolve_conflict without deciding which
            // of the two ids above goes where.
            if (c.Status == ConflictStatus.Open)
                sb.AppendLine($"  → resolve_conflict(conflict_id: {c.Id}, winner_id: <one of the two ids above>)");
        }

        return sb.ToString();

        void AppendSide(string role, ConflictSide? side)
        {
            if (side is null)
            {
                sb.AppendLine($"  {role}: not visible in this scope");
                return;
            }

            var state = side.State == MemoryState.Active ? "current" : side.State.ToString().ToLowerInvariant();

            sb.AppendLine($"  {role} [{state}] id: {side.Id}");
            sb.AppendLine($"    {side.Title}");
            if (!string.IsNullOrWhiteSpace(side.Summary)) sb.AppendLine($"    {side.Summary}");
            if (!string.IsNullOrWhiteSpace(side.ValueKey)) sb.AppendLine($"    value: {side.ValueKey}");
            sb.AppendLine($"    said {side.ValidFrom:yyyy-MM-dd}, from {side.Source}, confidence {side.Confidence:0.00}");
        }
    }

    [McpServerTool(Name = "resolve_conflict")]
    [Description("""
        Settle a contradiction once the user has clarified. The losing memory becomes history, never
        deleted, and can be brought back with restore_memory. winner_id must be one of the two ids
        list_conflicts gave for this conflict; anything else is refused rather than guessed at.
        """)]
    public async Task<string> ResolveConflict(
        [Description("Conflict ID from list_conflicts")] Guid conflict_id,
        [Description("The user")] string user_id,
        [Description("ID of the memory that is correct, exactly as listed. Omit with dismiss=true if both are fine.")] Guid? winner_id = null,
        [Description("Both memories are valid; just stop flagging it")] bool dismiss = false,
        [Description("Companion context")] string? companion_id = null,
        CancellationToken cancellationToken = default)
    {
        var scope = MemoryScope.For(user_id, companion_id);
        var outcome = await repository.ResolveConflictAsync(
            conflict_id, scope, winner_id, dismiss, "mcp:resolve_conflict", cancellationToken);

        // Each failure says what to do about it, because the model is the thing that has to recover
        // and "false" told it nothing.
        return outcome switch
        {
            ConflictResolution.Resolved =>
                $"Resolved in favour of {winner_id}. The other memory is retained as history and can "
                + "be brought back with restore_memory.",

            ConflictResolution.Dismissed =>
                "Dismissed; both memories remain active and it will not be flagged again.",

            ConflictResolution.NotFound =>
                $"No conflict {conflict_id} for this user. Call list_conflicts for current ids.",

            ConflictResolution.WinnerNotInConflict =>
                $"{winner_id} is not one of the two memories in conflict {conflict_id}. Nothing was "
                + "changed. Call list_conflicts and use one of the two ids it gives for this conflict.",

            ConflictResolution.NoChoice =>
                "Nothing was changed: give winner_id to pick a side, or dismiss=true to keep both.",

            ConflictResolution.AlreadySettled =>
                $"Conflict {conflict_id} was already settled and nothing was changed. Settling it "
                + "twice would supersede the first winner as well, leaving nothing current. If the "
                + "wrong side won, use restore_memory on the one that should have.",

            _ => outcome.ToString(),
        };
    }

    [McpServerTool(Name = "get_memory_history")]
    [Description("""
        The audit trail for one memory: created, updated, superseded, archived, forgotten, restored,
        and by what. Answers "why don't you remember that any more?".
        """)]
    public async Task<string> GetMemoryHistory(
        [Description("Memory ID")] Guid id,
        CancellationToken cancellationToken = default)
    {
        var events = await eventLog.GetForMemoryAsync(id, cancellationToken);
        if (events.Count == 0) return $"No recorded history for memory {id}.";

        var sb = new StringBuilder($"**History of memory {id}**\n");
        foreach (var e in events)
            sb.AppendLine($"{e.Timestamp:yyyy-MM-dd HH:mm:ss} {e.Type,-16} by {e.Actor}" +
                          (e.Detail is null ? "" : $" — {e.Detail}") +
                          (e.RelatedMemoryId is { } r ? $" (related: {r})" : ""));

        return sb.ToString();
    }

    [McpServerTool(Name = "list_slots")]
    [Description("The structured attributes the system understands, and how a change to each is handled.")]
    public string ListSlots()
    {
        var sb = new StringBuilder("**Known slots**\n");
        foreach (var s in slots.All.OrderBy(s => s.Predicate))
            sb.AppendLine($"- {s.Predicate,-22} {s.Cardinality,-13} {s.Policy}{(s.NeverAutoRemove ? " (never auto-removed)" : "")}");

        sb.AppendLine("\nUnlisted predicates coexist and never supersede.");
        return sb.ToString();
    }

    [McpServerTool(Name = "get_stats")]
    [Description("Memory statistics for a user: totals by state, average strength, open contradictions.")]
    public async Task<string> GetStats(
        [Description("The user")] string user_id,
        CancellationToken cancellationToken = default)
    {
        var stats = await repository.GetStatsAsync(MemoryScope.AllFor(user_id), cancellationToken);

        return $"""
            **Memory statistics for '{user_id}'**

            Active:      {stats.ActiveNodes}
            Superseded:  {stats.SupersededNodes}
            Archived:    {stats.ArchivedNodes}
            Forgotten:   {stats.ForgottenNodes} (tombstoned, awaiting purge)
            Total:       {stats.TotalNodes}

            Open contradictions: {stats.OpenConflicts}
            Average strength:    {stats.AverageStrength:F2}
            Database size:       {stats.DatabaseSizeBytes / 1024.0 / 1024.0:F2} MB
            Oldest: {stats.OldestMemory?.ToString("yyyy-MM-dd") ?? "n/a"} | Newest: {stats.NewestMemory?.ToString("yyyy-MM-dd") ?? "n/a"}
            """;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    private static string Truncate(string value, int max) =>
        value.Length > max ? value[..max] : value;

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value?.Replace("_", ""), ignoreCase: true, out var parsed) ? parsed : fallback;

    private static MemorySource ParseSource(string? value) => value?.ToLowerInvariant() switch
    {
        "companion_inferred" or "companioninferred" or "inferred" => MemorySource.CompanionInferred,
        "system_derived" or "systemderived" or "derived"          => MemorySource.SystemDerived,
        "imported"                                                => MemorySource.Imported,
        _                                                         => MemorySource.UserStated,
    };
}
