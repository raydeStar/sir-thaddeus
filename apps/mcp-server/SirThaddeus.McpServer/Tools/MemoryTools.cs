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
        "suitable for system prompt injection, plus metadata counts and citations.")]
    public static async Task<string> MemoryRetrieve(
        [Description("The user's query to find relevant memories for")]
        string query,
        [Description("Optional conversation ID for scoping")]
        string? conversationId = null,
        [Description("Optional mode hint: chat, planning, or technical")]
        string? mode = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Respond(error: "Query is required.");

        var retriever = Backend.Value.Retriever;
        if (retriever is null)
            return Respond(error: "Memory system not configured.");

        try
        {
            var context = new RetrievalContext
            {
                ConversationId = conversationId,
                Mode           = mode
            };

            var pack = await retriever.BuildMemoryPackAsync(
                query, context, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                facts      = pack.Facts.Count,
                events     = pack.Events.Count,
                chunks     = pack.Chunks.Count,
                notes      = pack.Notes,
                citations  = pack.Citations,
                packText   = pack.PackText,
                hasContent = pack.HasContent
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
        "Each fact is a subject-predicate-object triple. Returns the " +
        "number of facts stored.")]
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
            var toStore = facts.Take(10).ToList();
            var now     = DateTimeOffset.UtcNow;

            foreach (var f in toStore)
            {
                if (string.IsNullOrWhiteSpace(f.Subject) ||
                    string.IsNullOrWhiteSpace(f.Predicate) ||
                    string.IsNullOrWhiteSpace(f.Object))
                    continue;

                await store.StoreFactAsync(new MemoryFact
                {
                    MemoryId    = $"f-{Guid.NewGuid():N}",
                    Subject     = f.Subject.Trim(),
                    Predicate   = f.Predicate.Trim(),
                    Object      = f.Object.Trim(),
                    Confidence  = 0.90,
                    Sensitivity = ParseSensitivity(f.Sensitivity),
                    CreatedAt   = now,
                    UpdatedAt   = now,
                    SourceRef   = sourceRef
                }, cancellationToken);
            }

            return JsonResponse(new
            {
                stored  = toStore.Count,
                message = $"Stored {toStore.Count} fact(s) in memory."
            });
        }
        catch (Exception ex)
        {
            return JsonResponse(new { error = $"Failed to store facts: {ex.Message}", stored = 0 });
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

    private static Sensitivity ParseSensitivity(string? value) =>
        (value?.ToLowerInvariant()) switch
        {
            "personal" => Sensitivity.Personal,
            "secret"   => Sensitivity.Secret,
            _          => Sensitivity.Public
        };

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
