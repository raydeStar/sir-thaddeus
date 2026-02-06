using Microsoft.Data.Sqlite;

namespace SirThaddeus.Memory.Sqlite;

/// <summary>
/// SQLite-backed implementation of <see cref="IMemoryStore"/>.
/// Uses FTS5 for BM25 chunk retrieval and keyword matching for
/// structured facts/events.
///
/// Thread-safe for concurrent reads. Schema is initialized lazily
/// on first access via <see cref="EnsureSchemaAsync"/>.
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IDisposable
{
    private readonly string _connectionString;
    private SqliteConnection? _connection;
    private bool _schemaReady;
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <param name="dbPath">Full path to the SQLite database file.</param>
    public SqliteMemoryStore(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        _connectionString = $"Data Source={dbPath}";
    }

    // ─────────────────────────────────────────────────────────────────
    // IMemoryStore implementation
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        if (_schemaReady) return;

        // Get the connection FIRST (has its own lock), then check
        // schema under a separate lock acquisition. This avoids the
        // nested-semaphore deadlock that would occur if we held _lock
        // and then called GetConnectionAsync (which also acquires _lock).
        var conn = await GetConnectionAsync(ct);

        await _lock.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await SchemaInitializer.InitializeAsync(conn, ct);
            _schemaReady = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreCandidate<MemoryFact>>> SearchFactsAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        var conn     = await GetConnectionAsync(ct);
        var keywords = TokenizeQuery(query);
        if (keywords.Count == 0)
            return [];

        // Build WHERE: any keyword matches any searchable field
        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        for (var i = 0; i < keywords.Count; i++)
        {
            var p = $"@kw{i}";
            conditions.Add(
                $"(subject LIKE {p} OR predicate LIKE {p} OR object LIKE {p})");
            cmd.Parameters.AddWithValue(p, $"%{keywords[i]}%");
        }

        cmd.CommandText = $"""
            SELECT memory_id, subject, predicate, object, confidence,
                   sensitivity, created_at, updated_at, source_ref
            FROM   memory_facts
            WHERE  is_deleted = 0
              AND  sensitivity != 'secret'
              AND  ({string.Join(" OR ", conditions)})
            LIMIT  @limit
            """;
        cmd.Parameters.AddWithValue("@limit", maxResults);

        var results = new List<StoreCandidate<MemoryFact>>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var fact = new MemoryFact
            {
                MemoryId    = reader.GetString(0),
                Subject     = reader.GetString(1),
                Predicate   = reader.GetString(2),
                Object      = reader.GetString(3),
                Confidence  = reader.GetDouble(4),
                Sensitivity = ParseSensitivity(reader.GetString(5)),
                CreatedAt   = ParseTimestamp(reader.GetString(6)),
                UpdatedAt   = ParseTimestamp(reader.GetString(7)),
                SourceRef   = reader.IsDBNull(8) ? null : reader.GetString(8)
            };

            var matchCount = CountKeywordMatches(keywords,
                fact.Subject, fact.Predicate, fact.Object);
            var lexScore = (double)matchCount / keywords.Count;

            results.Add(new StoreCandidate<MemoryFact>(fact, lexScore));
        }

        return results.OrderByDescending(c => c.LexicalScore).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreCandidate<MemoryEvent>>> SearchEventsAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        var conn     = await GetConnectionAsync(ct);
        var keywords = TokenizeQuery(query);
        if (keywords.Count == 0)
            return [];

        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();

        for (var i = 0; i < keywords.Count; i++)
        {
            var p = $"@kw{i}";
            conditions.Add(
                $"(type LIKE {p} OR title LIKE {p} OR COALESCE(summary,'') LIKE {p})");
            cmd.Parameters.AddWithValue(p, $"%{keywords[i]}%");
        }

        cmd.CommandText = $"""
            SELECT event_id, type, title, summary, when_iso,
                   confidence, sensitivity, source_ref
            FROM   memory_events
            WHERE  is_deleted = 0
              AND  sensitivity != 'secret'
              AND  ({string.Join(" OR ", conditions)})
            LIMIT  @limit
            """;
        cmd.Parameters.AddWithValue("@limit", maxResults);

        var results = new List<StoreCandidate<MemoryEvent>>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var evt = new MemoryEvent
            {
                EventId     = reader.GetString(0),
                Type        = reader.GetString(1),
                Title       = reader.GetString(2),
                Summary     = reader.IsDBNull(3) ? null : reader.GetString(3),
                WhenIso     = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Confidence  = reader.GetDouble(5),
                Sensitivity = ParseSensitivity(reader.GetString(6)),
                SourceRef   = reader.IsDBNull(7) ? null : reader.GetString(7)
            };

            var matchCount = CountKeywordMatches(keywords,
                evt.Type, evt.Title, evt.Summary ?? "");
            var lexScore = (double)matchCount / keywords.Count;

            results.Add(new StoreCandidate<MemoryEvent>(evt, lexScore));
        }

        return results.OrderByDescending(c => c.LexicalScore).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreCandidate<MemoryChunk>>> SearchChunksAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        // Build FTS5 query from tokenized keywords
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.chunk_id, c.source_type, c.source_ref, c.text,
                   c.when_iso, c.sensitivity, c.embedding,
                   fts.rank AS bm25_score
            FROM   memory_chunks_fts fts
            JOIN   memory_chunks c ON c.rowid = fts.rowid
            WHERE  memory_chunks_fts MATCH @query
              AND  c.is_deleted = 0
              AND  c.sensitivity != 'secret'
            ORDER  BY fts.rank
            LIMIT  @limit
            """;
        cmd.Parameters.AddWithValue("@query",  ftsQuery);
        cmd.Parameters.AddWithValue("@limit",  maxResults);

        var raw = new List<(MemoryChunk Chunk, double RawBm25)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var chunk = new MemoryChunk
            {
                ChunkId     = reader.GetString(0),
                SourceType  = reader.GetString(1),
                SourceRef   = reader.IsDBNull(2) ? null : reader.GetString(2),
                Text        = reader.GetString(3),
                WhenIso     = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Sensitivity = ParseSensitivity(reader.GetString(5)),
                Embedding   = reader.IsDBNull(6) ? null : ParseEmbedding((byte[])reader[6])
            };

            raw.Add((chunk, reader.GetDouble(7)));
        }

        if (raw.Count == 0)
            return [];

        // Normalize BM25 scores to [0, 1].
        // bm25() returns negative values: more negative = better match.
        var best  = raw.Min(r => r.RawBm25);   // most negative = best
        var worst = raw.Max(r => r.RawBm25);    // least negative = worst
        var range = worst - best;

        return raw.Select(r =>
        {
            var normalized = range > 1e-10
                ? (worst - r.RawBm25) / range
                : 1.0;

            return new StoreCandidate<MemoryChunk>(r.Chunk, normalized);
        }).ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    // Write Operations (upsert semantics — idempotent by design)
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_facts
                (memory_id, subject, predicate, object, confidence,
                 sensitivity, created_at, updated_at, source_ref, is_deleted)
            VALUES (@id, @subj, @pred, @obj, @conf,
                    @sens, @created, @updated, @src, 0)
            """;
        cmd.Parameters.AddWithValue("@id",      fact.MemoryId);
        cmd.Parameters.AddWithValue("@subj",    fact.Subject);
        cmd.Parameters.AddWithValue("@pred",    fact.Predicate);
        cmd.Parameters.AddWithValue("@obj",     fact.Object);
        cmd.Parameters.AddWithValue("@conf",    fact.Confidence);
        cmd.Parameters.AddWithValue("@sens",    fact.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@created", fact.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", fact.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@src",     (object?)fact.SourceRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task StoreEventAsync(MemoryEvent evt, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_events
                (event_id, type, title, summary, when_iso,
                 confidence, sensitivity, source_ref, is_deleted)
            VALUES (@id, @type, @title, @summary, @when,
                    @conf, @sens, @src, 0)
            """;
        cmd.Parameters.AddWithValue("@id",      evt.EventId);
        cmd.Parameters.AddWithValue("@type",    evt.Type);
        cmd.Parameters.AddWithValue("@title",   evt.Title);
        cmd.Parameters.AddWithValue("@summary", (object?)evt.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@when",    evt.WhenIso.HasValue
            ? evt.WhenIso.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@conf",    evt.Confidence);
        cmd.Parameters.AddWithValue("@sens",    evt.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@src",     (object?)evt.SourceRef ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task StoreChunkAsync(MemoryChunk chunk, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_chunks
                (chunk_id, source_type, source_ref, text,
                 when_iso, sensitivity, is_deleted, embedding)
            VALUES (@id, @srcType, @srcRef, @text,
                    @when, @sens, 0, @emb)
            """;
        cmd.Parameters.AddWithValue("@id",      chunk.ChunkId);
        cmd.Parameters.AddWithValue("@srcType", chunk.SourceType);
        cmd.Parameters.AddWithValue("@srcRef",  (object?)chunk.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@text",    chunk.Text);
        cmd.Parameters.AddWithValue("@when",    chunk.WhenIso.HasValue
            ? chunk.WhenIso.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sens",    chunk.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@emb",     chunk.Embedding is not null
            ? SerializeEmbedding(chunk.Embedding) : (object)DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Browse Operations (paginated listing for UI browsers)
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<(IReadOnlyList<MemoryFact> Items, int TotalCount)> ListFactsAsync(
        string? filter, int skip, int take, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // ── Count ──
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = hasFilter
            ? """
              SELECT COUNT(*) FROM memory_facts
              WHERE is_deleted = 0
                AND (subject LIKE @f OR predicate LIKE @f OR object LIKE @f)
              """
            : "SELECT COUNT(*) FROM memory_facts WHERE is_deleted = 0";

        if (hasFilter)
            countCmd.Parameters.AddWithValue("@f", $"%{filter}%");

        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // ── Page ──
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasFilter
            ? """
              SELECT memory_id, subject, predicate, object, confidence,
                     sensitivity, created_at, updated_at, source_ref
              FROM   memory_facts
              WHERE  is_deleted = 0
                AND  (subject LIKE @f OR predicate LIKE @f OR object LIKE @f)
              ORDER  BY updated_at DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT memory_id, subject, predicate, object, confidence,
                     sensitivity, created_at, updated_at, source_ref
              FROM   memory_facts
              WHERE  is_deleted = 0
              ORDER  BY updated_at DESC
              LIMIT  @take OFFSET @skip
              """;

        if (hasFilter)
            cmd.Parameters.AddWithValue("@f", $"%{filter}%");
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);

        var items = new List<MemoryFact>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MemoryFact
            {
                MemoryId    = reader.GetString(0),
                Subject     = reader.GetString(1),
                Predicate   = reader.GetString(2),
                Object      = reader.GetString(3),
                Confidence  = reader.GetDouble(4),
                Sensitivity = ParseSensitivity(reader.GetString(5)),
                CreatedAt   = ParseTimestamp(reader.GetString(6)),
                UpdatedAt   = ParseTimestamp(reader.GetString(7)),
                SourceRef   = reader.IsDBNull(8) ? null : reader.GetString(8)
            });
        }

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<MemoryEvent> Items, int TotalCount)> ListEventsAsync(
        string? filter, int skip, int take, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // ── Count ──
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = hasFilter
            ? """
              SELECT COUNT(*) FROM memory_events
              WHERE is_deleted = 0
                AND (type LIKE @f OR title LIKE @f OR COALESCE(summary,'') LIKE @f)
              """
            : "SELECT COUNT(*) FROM memory_events WHERE is_deleted = 0";

        if (hasFilter)
            countCmd.Parameters.AddWithValue("@f", $"%{filter}%");

        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // ── Page ──
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasFilter
            ? """
              SELECT event_id, type, title, summary, when_iso,
                     confidence, sensitivity, source_ref
              FROM   memory_events
              WHERE  is_deleted = 0
                AND  (type LIKE @f OR title LIKE @f OR COALESCE(summary,'') LIKE @f)
              ORDER  BY when_iso DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT event_id, type, title, summary, when_iso,
                     confidence, sensitivity, source_ref
              FROM   memory_events
              WHERE  is_deleted = 0
              ORDER  BY when_iso DESC
              LIMIT  @take OFFSET @skip
              """;

        if (hasFilter)
            cmd.Parameters.AddWithValue("@f", $"%{filter}%");
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);

        var items = new List<MemoryEvent>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MemoryEvent
            {
                EventId     = reader.GetString(0),
                Type        = reader.GetString(1),
                Title       = reader.GetString(2),
                Summary     = reader.IsDBNull(3) ? null : reader.GetString(3),
                WhenIso     = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Confidence  = reader.GetDouble(5),
                Sensitivity = ParseSensitivity(reader.GetString(6)),
                SourceRef   = reader.IsDBNull(7) ? null : reader.GetString(7)
            });
        }

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<MemoryChunk> Items, int TotalCount)> ListChunksAsync(
        string? filter, int skip, int take, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // ── Count ──
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = hasFilter
            ? """
              SELECT COUNT(*) FROM memory_chunks
              WHERE is_deleted = 0
                AND (text LIKE @f OR source_type LIKE @f OR COALESCE(source_ref,'') LIKE @f)
              """
            : "SELECT COUNT(*) FROM memory_chunks WHERE is_deleted = 0";

        if (hasFilter)
            countCmd.Parameters.AddWithValue("@f", $"%{filter}%");

        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // ── Page ──
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasFilter
            ? """
              SELECT chunk_id, source_type, source_ref, text,
                     when_iso, sensitivity
              FROM   memory_chunks
              WHERE  is_deleted = 0
                AND  (text LIKE @f OR source_type LIKE @f OR COALESCE(source_ref,'') LIKE @f)
              ORDER  BY when_iso DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT chunk_id, source_type, source_ref, text,
                     when_iso, sensitivity
              FROM   memory_chunks
              WHERE  is_deleted = 0
              ORDER  BY when_iso DESC
              LIMIT  @take OFFSET @skip
              """;

        if (hasFilter)
            cmd.Parameters.AddWithValue("@f", $"%{filter}%");
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);

        var items = new List<MemoryChunk>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(new MemoryChunk
            {
                ChunkId     = reader.GetString(0),
                SourceType  = reader.GetString(1),
                SourceRef   = reader.IsDBNull(2) ? null : reader.GetString(2),
                Text        = reader.GetString(3),
                WhenIso     = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Sensitivity = ParseSensitivity(reader.GetString(5)),
                // Skip embedding for browse — not needed in the UI
                Embedding   = null
            });
        }

        return (items, total);
    }

    // ─────────────────────────────────────────────────────────────────
    // Delete Operations (soft-delete — idempotent)
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task DeleteFactAsync(string memoryId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory_facts SET is_deleted = 1 WHERE memory_id = @id";
        cmd.Parameters.AddWithValue("@id", memoryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory_events SET is_deleted = 1 WHERE event_id = @id";
        cmd.Parameters.AddWithValue("@id", eventId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteChunkAsync(string chunkId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory_chunks SET is_deleted = 1 WHERE chunk_id = @id";
        cmd.Parameters.AddWithValue("@id", chunkId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes a float[] embedding to a BLOB (float32 array).
    /// </summary>
    private static byte[] SerializeEmbedding(float[] embedding)
    {
        var blob = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, blob, 0, blob.Length);
        return blob;
    }

    // ─────────────────────────────────────────────────────────────────
    // Query Helpers
    // ─────────────────────────────────────────────────────────────────

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "is", "are", "was", "were", "be", "been",
        "being", "have", "has", "had", "do", "does", "did", "will",
        "would", "could", "should", "may", "might", "shall", "can",
        "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "as", "into", "through", "during", "before", "after", "above",
        "below", "between", "out", "off", "over", "under", "again",
        "further", "then", "once", "here", "there", "when", "where",
        "why", "how", "all", "both", "each", "few", "more", "most",
        "other", "some", "such", "no", "nor", "not", "only", "own",
        "same", "so", "than", "too", "very", "just", "because",
        "but", "and", "or", "if", "about", "up", "its", "it",
        "this", "that", "these", "those", "i", "me", "my", "we",
        "you", "your", "he", "him", "his", "she", "her", "they",
        "them", "their", "what", "which", "who", "whom"
    };

    /// <summary>
    /// Splits query into meaningful keywords, removing stop words.
    /// </summary>
    internal static List<string> TokenizeQuery(string query)
    {
        return query
            .ToLowerInvariant()
            .Split([' ', ',', '.', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']'],
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Builds an FTS5 MATCH query. Joins tokens with OR for broad matching.
    /// </summary>
    private static string BuildFtsQuery(string query)
    {
        var tokens = TokenizeQuery(query);
        if (tokens.Count == 0)
            return "";

        // Escape FTS5 special characters and quote each token
        var escaped = tokens.Select(t => $"\"{t.Replace("\"", "")}\"");
        return string.Join(" OR ", escaped);
    }

    /// <summary>
    /// Counts how many query keywords appear in any of the given fields.
    /// </summary>
    private static int CountKeywordMatches(
        List<string> keywords, params string[] fields)
    {
        var combined = string.Join(" ", fields).ToLowerInvariant();
        return keywords.Count(kw =>
            combined.Contains(kw, StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────────────────────────────────────────────
    // Parsing Helpers
    // ─────────────────────────────────────────────────────────────────

    private static Sensitivity ParseSensitivity(string value) =>
        value.ToLowerInvariant() switch
        {
            "personal" => Sensitivity.Personal,
            "secret"   => Sensitivity.Secret,
            _          => Sensitivity.Public
        };

    private static DateTimeOffset ParseTimestamp(string iso) =>
        DateTimeOffset.TryParse(iso, out var dto) ? dto : DateTimeOffset.MinValue;

    /// <summary>
    /// Parses a stored embedding BLOB (float32 array) back to float[].
    /// </summary>
    private static float[]? ParseEmbedding(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length % 4 != 0)
            return null;

        var count  = blob.Length / 4;
        var result = new float[count];
        Buffer.BlockCopy(blob, 0, result, 0, blob.Length);
        return result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Connection Management
    // ─────────────────────────────────────────────────────────────────

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null)
            return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not null)
                return _connection;

            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(ct);

            // WAL mode for better concurrent read performance
            using var pragma = _connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            await pragma.ExecuteNonQueryAsync(ct);

            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
