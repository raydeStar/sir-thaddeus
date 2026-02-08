using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.LlmClient;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// MCP tools for memory retrieval and storage.
///
/// Reads configuration from environment variables set by the desktop runtime:
///   ST_MEMORY_DB_PATH       — Full path to the SQLite memory database
///   ST_LLM_BASEURL          — OpenAI-compatible base URL (any provider)
///   ST_LLM_EMBEDDINGS_MODEL — Model name for /v1/embeddings (optional)
///
/// Invariants:
///   I3 — MemoryStore writes require the user to explicitly ask to remember.
///   I4 — Results are returned to the agent for audit logging.
///   T1 — Bounded output sizes.
///   T2 — Writes use upsert (idempotent, retry-safe).
/// </summary>
[McpServerToolType]
public static class MemoryTools
{
    // Lazy singletons: initialized once from env vars, reused across calls.
    private static readonly Lazy<(SqliteMemoryStore? Store, MemoryRetriever? Retriever)>
        Backend = new(CreateBackend);

    // ─────────────────────────────────────────────────────────────────
    // Retrieval
    // ─────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Retrieves relevant memory context (facts, events, conversation chunks) " +
        "for a given query. Returns JSON with a pre-formatted packText block " +
        "suitable for system prompt injection, plus metadata counts and citations. " +
        "Pass mode='greet' on the first message of a new conversation to get a " +
        "lightweight greeting context (profile + 1-2 nuggets, no deep retrieval).")]
    public static async Task<string> MemoryRetrieve(
        [Description("The user's query to find relevant memories for")]
        string query,
        [Description("Optional conversation ID for scoping")]
        string? conversationId = null,
        [Description("Optional mode hint: 'greet' for cold-start greeting, " +
            "or 'chat'/'planning'/'technical' for normal retrieval")]
        string? mode = null,
        [Description("Active profile ID from runtime. Populated automatically.")]
        string? activeProfileId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Respond(error: "Query is required.");

        var store     = Backend.Value.Store;
        var retriever = Backend.Value.Retriever;
        if (store is null || retriever is null)
            return Respond(error: "Memory system not configured.");

        try
        {
            var isGreet = string.Equals(mode, "greet", StringComparison.OrdinalIgnoreCase);

            // ── Shallow layer: Profile Card + Nuggets ────────────────
            // Use the active profile if one is selected in Settings.
            // Priority: tool arg > env var > legacy fallback.
            var userProfile = await ResolveActiveProfileAsync(
                store, activeProfileId, cancellationToken);

            IReadOnlyList<MemoryNugget> nuggets;
            if (isGreet)
            {
                // Greeting: only safe/low-sensitivity nuggets (max 2)
                nuggets = await store.GetGreetingNuggetsAsync(2, cancellationToken);
            }
            else
            {
                // Normal: keyword-match nuggets relevant to the query (max 5)
                nuggets = await store.SearchNuggetsAsync(query, 5, cancellationToken);
            }

            // ── Other-person profile (only in normal mode) ───────────
            ProfileCard? otherProfile = null;
            if (!isGreet)
            {
                var personHits = await store.SearchPersonProfilesAsync(
                    query, 1, cancellationToken);
                otherProfile = personHits.Count > 0 ? personHits[0] : null;
            }

            // ── Build the shallow context block ──────────────────────
            var shallowBlock = ShallowMemoryContextBuilder.Build(
                userProfile, otherProfile, nuggets);

            // ── Touch nuggets so use_count / last_used_at update ─────
            foreach (var n in nuggets)
                await store.TouchNuggetAsync(n.NuggetId, cancellationToken);

            // ── Deep retrieval (skipped in greet mode) ───────────────
            string deepPackText = "";
            int deepFacts = 0, deepEvents = 0, deepChunks = 0;
            IReadOnlyList<string> citations = [];

            if (!isGreet)
            {
                var context = new RetrievalContext
                {
                    ConversationId = conversationId,
                    Mode           = mode
                };

                var pack = await retriever.BuildMemoryPackAsync(
                    query, context, cancellationToken);

                deepPackText = pack.PackText;
                deepFacts    = pack.Facts.Count;
                deepEvents   = pack.Events.Count;
                deepChunks   = pack.Chunks.Count;
                citations    = pack.Citations;
            }

            // ── Combine: shallow block FIRST, then deep ──────────────
            var fullText = string.IsNullOrEmpty(deepPackText)
                ? shallowBlock
                : shallowBlock + "\n" + deepPackText;

            var hasContent = !string.IsNullOrWhiteSpace(shallowBlock)
                          || deepFacts > 0 || deepEvents > 0 || deepChunks > 0;

            // ── Onboarding detection ──────────────────────────────────
            // If no active profile was resolved (either none selected in
            // Settings or none exists), signal the orchestrator to run
            // the "get to know you" flow on entry.
            var onboardingNeeded = userProfile is null;

            return JsonSerializer.Serialize(new
            {
                facts      = deepFacts,
                events     = deepEvents,
                chunks     = deepChunks,
                nuggets    = nuggets.Count,
                hasProfile = userProfile is not null,
                onboardingNeeded,
                notes      = "",
                citations,
                packText   = fullText,
                hasContent
            }, SerializerOpts);
        }
        catch (OperationCanceledException)
        {
            return Respond(error: "Memory retrieval cancelled.");
        }
        catch (Exception ex)
        {
            return Respond(error: $"Memory retrieval failed: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Storage
    // ─────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Stores one or more facts the user asked you to remember. " +
        "Call this when the user says things like 'remember that ...', " +
        "'note that ...', 'I like ...', 'my favorite ... is ...'. " +
        "Each fact is a subject-predicate-object triple. " +
        "Checks for duplicates (skipped) and conflicts (returned for " +
        "user confirmation). If conflicts are returned, present them " +
        "to the user and call memory_update_fact with the user's choice.")]
    public static async Task<string> MemoryStoreFacts(
        [Description("JSON array of facts. Each item: " +
            "{\"subject\":\"user\",\"predicate\":\"likes\",\"object\":\"cats\"}. " +
            "Keep predicates short and consistent (likes, prefers, works_on, knows, etc.)")]
        string factsJson,
        [Description("Optional source reference (e.g. conversation ID)")]
        string? sourceRef = null,
        CancellationToken cancellationToken = default)
    {
        var store = Backend.Value.Store;
        if (store is null)
            return JsonResponse(new { error = "Memory system not configured.", stored = 0 });

        try
        {
            var facts = JsonSerializer.Deserialize<List<FactInput>>(factsJson, JsonReadOpts);
            if (facts is null or { Count: 0 })
                return JsonResponse(new { error = "No facts provided.", stored = 0 });

            // Cap at 10 facts per call to prevent abuse
            var toStore   = facts.Take(10).ToList();
            var now       = DateTimeOffset.UtcNow;
            var stored      = 0;
            var skipped     = 0;
            var replaced    = 0; // Auto-resolved updates + antonym replacements

            foreach (var f in toStore)
            {
                if (string.IsNullOrWhiteSpace(f.Subject) ||
                    string.IsNullOrWhiteSpace(f.Predicate) ||
                    string.IsNullOrWhiteSpace(f.Object))
                    continue;

                var subj = f.Subject.Trim();
                var pred = f.Predicate.Trim();
                var obj  = f.Object.Trim();

                // ── Check for existing facts with same (subject, predicate)
                var existing = await store.FindMatchingFactsAsync(subj, pred, cancellationToken);

                if (existing.Count > 0)
                {
                    // Exact duplicate: same object (case-insensitive) → skip
                    var exactDupe = existing.FirstOrDefault(e =>
                        string.Equals(e.Object, obj, StringComparison.OrdinalIgnoreCase));

                    if (exactDupe is not null)
                    {
                        skipped++;
                        continue;
                    }

                    // Single-valued predicates (name_is, lives_in, etc.)
                    // can only hold one value at a time. Auto-resolve by
                    // updating in place — the user explicitly stated the
                    // new value, so the old one is simply outdated.
                    // No confirmation prompt; just update and inform.
                    if (IsSingleValuedPredicate(pred))
                    {
                        var outdated = existing[0];
                        await store.StoreFactAsync(new MemoryFact
                        {
                            MemoryId    = outdated.MemoryId,
                            ProfileId   = outdated.ProfileId,
                            Subject     = subj,
                            Predicate   = pred,
                            Object      = obj,
                            Confidence  = 0.95,
                            Sensitivity = ParseSensitivity(f.Sensitivity),
                            CreatedAt   = outdated.CreatedAt,
                            UpdatedAt   = now,
                            SourceRef   = sourceRef ?? outdated.SourceRef
                        }, cancellationToken);

                        replaced++;
                        continue;
                    }
                }

                // ── Check for antonym predicates on the same object ────
                // "user hates pie" supersedes "user likes pie" — the old
                // fact is soft-deleted automatically since the user's new
                // statement is the explicit override.
                var antonyms = GetAntonymPredicates(pred);
                var superseded = 0;
                foreach (var antonym in antonyms)
                {
                    var contradicting = await store.FindMatchingFactsAsync(
                        subj, antonym, cancellationToken);

                    foreach (var old in contradicting.Where(e =>
                        string.Equals(e.Object, obj, StringComparison.OrdinalIgnoreCase)))
                    {
                        await store.DeleteFactAsync(old.MemoryId, cancellationToken);
                        superseded++;
                    }
                }

                // ── Store the new fact ──────────────────────────────────
                // Stamp with the active profile so this fact belongs
                // to a specific user (Mark, Rebecca, etc.).
                var activeProfileId = Environment.GetEnvironmentVariable("ST_ACTIVE_PROFILE_ID");

                await store.StoreFactAsync(new MemoryFact
                {
                    MemoryId    = $"f-{Guid.NewGuid():N}",
                    ProfileId   = string.IsNullOrWhiteSpace(activeProfileId) ? null : activeProfileId,
                    Subject     = subj,
                    Predicate   = pred,
                    Object      = obj,
                    Confidence  = superseded > 0 ? 0.95 : 0.90,
                    Sensitivity = ParseSensitivity(f.Sensitivity),
                    CreatedAt   = now,
                    UpdatedAt   = now,
                    SourceRef   = sourceRef
                }, cancellationToken);
                stored++;
                replaced += superseded;
            }

            // ── Auto-create profile if a name fact was stored ────────
            // When the user tells us their name for the first time and
            // no profile card exists yet, create one automatically so
            // subsequent conversations greet them properly.
            string? autoCreatedProfileId = null;
            if (stored > 0)
            {
                autoCreatedProfileId = await TryAutoCreateProfileAsync(
                    store, toStore, cancellationToken);
            }

            // Build response message
            var parts = new List<string>();
            if (stored > 0)    parts.Add($"Stored {stored} fact(s).");
            if (replaced > 0)  parts.Add($"Updated {replaced} existing fact(s).");
            if (skipped > 0)   parts.Add($"Skipped {skipped} duplicate(s).");
            if (autoCreatedProfileId is not null)
                parts.Add("Created a new profile card for this user.");

            return JsonResponse(new
            {
                stored,
                replaced,
                skipped,
                autoCreatedProfileId,
                message = string.Join(" ", parts)
            });
        }
        catch (Exception ex)
        {
            return JsonResponse(new { error = $"Failed to store facts: {ex.Message}", stored = 0 });
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Conflict Resolution
    // ─────────────────────────────────────────────────────────────────

    [McpServerTool, Description(
        "Updates an existing memory fact after the user confirms a conflict " +
        "resolution. Call this when memory_store_facts reported a conflict " +
        "and the user chose to update the old value. Pass the existing " +
        "memory ID and the new object value.")]
    public static async Task<string> MemoryUpdateFact(
        [Description("The memory_id of the existing fact to update (from the conflict response)")]
        string memoryId,
        [Description("The new object value to replace the old one")]
        string newObject,
        [Description("Optional source reference (e.g. conversation ID)")]
        string? sourceRef = null,
        CancellationToken cancellationToken = default)
    {
        var store = Backend.Value.Store;
        if (store is null)
            return JsonResponse(new { error = "Memory system not configured." });

        if (string.IsNullOrWhiteSpace(memoryId) || string.IsNullOrWhiteSpace(newObject))
            return JsonResponse(new { error = "Both memoryId and newObject are required." });

        try
        {
            // Read the existing fact to preserve subject/predicate/sensitivity
            var existing = await store.FindFactByIdAsync(memoryId, cancellationToken);
            if (existing is null)
                return JsonResponse(new { error = $"No fact found with id '{memoryId}'.", updated = false });

            var now = DateTimeOffset.UtcNow;
            await store.StoreFactAsync(new MemoryFact
            {
                MemoryId    = existing.MemoryId,
                ProfileId   = existing.ProfileId,   // Preserve ownership
                Subject     = existing.Subject,
                Predicate   = existing.Predicate,
                Object      = newObject.Trim(),
                Confidence  = 0.95, // Higher confidence — user explicitly confirmed
                Sensitivity = existing.Sensitivity,
                CreatedAt   = existing.CreatedAt,
                UpdatedAt   = now,
                SourceRef   = sourceRef ?? existing.SourceRef
            }, cancellationToken);

            return JsonResponse(new
            {
                updated = true,
                memoryId,
                oldObject = existing.Object,
                newObject = newObject.Trim(),
                message = $"Updated: {existing.Subject} {existing.Predicate} " +
                          $"'{existing.Object}' → '{newObject.Trim()}'."
            });
        }
        catch (Exception ex)
        {
            return JsonResponse(new { error = $"Failed to update fact: {ex.Message}", updated = false });
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Factory
    // ─────────────────────────────────────────────────────────────────

    private static (SqliteMemoryStore? Store, MemoryRetriever? Retriever) CreateBackend()
    {
        try
        {
            var dbPath = Environment.GetEnvironmentVariable("ST_MEMORY_DB_PATH");
            if (string.IsNullOrEmpty(dbPath))
                dbPath = GetDefaultDbPath();

            // Ensure the directory exists so SQLite can create the file
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var store = new SqliteMemoryStore(dbPath);
            store.EnsureSchemaAsync(CancellationToken.None)
                 .GetAwaiter().GetResult();

            // Embeddings: optional, fail-safe.
            IEmbeddingClient? embeddings = null;
            var baseUrl = Environment.GetEnvironmentVariable("ST_LLM_BASEURL");
            if (!string.IsNullOrEmpty(baseUrl))
            {
                var model = Environment.GetEnvironmentVariable("ST_LLM_EMBEDDINGS_MODEL");
                if (string.IsNullOrEmpty(model))
                    model = "local-model";

                embeddings = new OpenAiEmbeddingClient(baseUrl, model);
            }

            return (store, new MemoryRetriever(store, embeddings));
        }
        catch
        {
            return (null, null);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // DTOs + Helpers
    // ─────────────────────────────────────────────────────────────────

    private sealed record FactInput
    {
        public string Subject    { get; init; } = "";
        public string Predicate  { get; init; } = "";
        public string Object     { get; init; } = "";
        public string? Sensitivity { get; init; }
    }

    /// <summary>
    /// Returns predicates that are semantic opposites of the given one.
    /// "user hates pie" contradicts "user likes pie" — if one is stored,
    /// the other should be soft-deleted. Returns an empty array if no
    /// antonyms are defined.
    /// </summary>
    private static string[] GetAntonymPredicates(string predicate)
    {
        var p = predicate.ToLowerInvariant().Trim();

        // Each entry maps a predicate to its antonyms. Both directions
        // are listed explicitly for clarity and O(1) lookup.
        return p switch
        {
            "likes"      => ["dislikes", "hates"],
            "loves"      => ["dislikes", "hates"],
            "dislikes"   => ["likes", "loves"],
            "hates"      => ["likes", "loves"],

            "prefers"    => ["avoids", "dislikes"],
            "avoids"     => ["prefers", "likes"],

            "supports"   => ["opposes"],
            "opposes"    => ["supports"],

            "uses"       => ["avoids", "stopped_using"],
            "stopped_using" => ["uses"],

            _            => []
        };
    }

    /// <summary>
    /// Determines whether a predicate can only hold one value at a time.
    /// "prefers dark mode" and "prefers light mode" conflict — you can
    /// only prefer one. But "likes cats" and "likes dogs" don't conflict —
    /// you can like many things.
    /// </summary>
    private static bool IsSingleValuedPredicate(string predicate)
    {
        var p = predicate.ToLowerInvariant().Trim();

        // Exact matches for common single-valued predicates
        ReadOnlySpan<string> singleValued =
        [
            // Identity / demographics
            "name_is", "is_named", "goes_by",
            "lives_in", "lives_at", "located_in",
            "born_on", "birthday_is", "age_is",

            // Preferences (exclusive — only ONE value at a time)
            // NOTE: "prefers" is intentionally NOT here. You can
            // prefer many things (pizza, dark mode, Python). Only
            // "favorite_X" and "preferred_X" are exclusive because
            // they name a specific category ("favorite_color").
            "favorite_is", "default_is",

            // Employment / role
            "works_at", "works_for", "employed_at",
            "job_is", "role_is", "title_is",
            "occupation_is",

            // State (only one value at a time)
            "is", "is_a", "currently_is",
            "status_is", "state_is",
            "timezone_is", "language_is",
            "email_is", "phone_is"
        ];

        foreach (var sv in singleValued)
        {
            if (p == sv) return true;
        }

        // Prefix patterns — "favorite_*" predicates are single-valued
        if (p.StartsWith("favorite_") || p.StartsWith("preferred_"))
            return true;

        return false;
    }

    // ─────────────────────────────────────────────────────────────────
    // Profile Auto-Creation
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Name-related predicates that signal the user is telling us their
    /// name. Used by <see cref="TryAutoCreateProfileAsync"/> to detect
    /// when a profile card should be auto-created.
    /// </summary>
    private static readonly HashSet<string> NamePredicates = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "is_called", "is_named", "called", "named",
        "goes_by", "preferred_name", "first_name", "full_name"
    };

    /// <summary>
    /// Checks the just-stored facts for a name predicate on subject
    /// "user". If found and no user profile exists, creates a new
    /// <see cref="ProfileCard"/> automatically. Returns the new
    /// profile_id or null if no card was created.
    /// </summary>
    private static async Task<string?> TryAutoCreateProfileAsync(
        SqliteMemoryStore store, List<FactInput> facts, CancellationToken ct)
    {
        // Look for a name fact aimed at "user"
        var nameFact = facts.FirstOrDefault(f =>
            string.Equals(f.Subject?.Trim(), "user", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(f.Predicate) &&
            NamePredicates.Contains(f.Predicate.Trim()) &&
            !string.IsNullOrWhiteSpace(f.Object));

        if (nameFact is null)
            return null;

        // Only create if no user profile exists yet
        var existingProfile = await store.GetUserProfileAsync(ct);
        if (existingProfile is not null)
            return null;

        var profileId = $"p-{Guid.NewGuid():N}";
        var displayName = nameFact.Object!.Trim();

        await store.StoreProfileAsync(new ProfileCard
        {
            ProfileId   = profileId,
            Kind        = "user",
            DisplayName = displayName,
            ProfileJson = "{}",
            UpdatedAt   = DateTimeOffset.UtcNow
        }, ct);

        return profileId;
    }

    /// <summary>
    /// Resolves the active user profile using a three-tier priority:
    ///
    ///   1. <paramref name="overrideProfileId"/> — passed in the tool call
    ///      args by the orchestrator. Handles runtime profile changes
    ///      without needing to restart the MCP server process.
    ///
    ///   2. <c>ST_ACTIVE_PROFILE_ID</c> env var — set at MCP server
    ///      startup from <c>AppSettings.ActiveProfileId</c>.
    ///
    ///   3. Legacy fallback — env var not set at all (first run before
    ///      the Settings tab existed). Falls back to the default
    ///      <c>GetUserProfileAsync</c> lookup.
    ///
    /// When the resolved value is an empty string, the user explicitly
    /// chose "(No profile selected)" — returns null so onboarding
    /// kicks in instead of silently loading a profile they didn't ask for.
    /// </summary>
    private static async Task<ProfileCard?> ResolveActiveProfileAsync(
        SqliteMemoryStore store, string? overrideProfileId, CancellationToken ct)
    {
        // Priority 1: explicit override from tool call args
        var activeId = overrideProfileId;

        // Priority 2: env var set at MCP server startup
        activeId ??= Environment.GetEnvironmentVariable("ST_ACTIVE_PROFILE_ID");

        // Not configured at all (env var doesn't exist) → legacy fallback
        if (activeId is null)
            return await store.GetUserProfileAsync(ct);

        // Configured but empty → user explicitly chose "no profile"
        if (string.IsNullOrWhiteSpace(activeId))
            return null;

        // Configured with an ID → load that specific profile
        var all = await store.ListProfilesAsync(ct);
        return all.FirstOrDefault(p => p.ProfileId == activeId);
    }

    private static Sensitivity ParseSensitivity(string? value) =>
        MemoryParsing.ParseSensitivity(value);

    private static readonly JsonSerializerOptions SerializerOpts = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions JsonReadOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static string GetDefaultDbPath()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "memory.db");
    }

    private static string Respond(string error) =>
        JsonSerializer.Serialize(new
        {
            error,
            packText   = "",
            hasContent = false
        }, SerializerOpts);

    private static string JsonResponse(object value) =>
        JsonSerializer.Serialize(value, SerializerOpts);
}
