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
        string query, float[]? queryEmbedding, int maxResults, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
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
            SELECT memory_id, profile_id, subject, predicate, object,
                   confidence, weight, sensitivity, source_turn_id, source_hash,
                   dedupe_key, origin, created_at, updated_at, source_ref,
                   embedding, embedding_model, embedding_dims
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
            var fact = ReadFact(reader);
            var matchCount = CountKeywordMatches(keywords,
                fact.Subject, fact.Predicate, fact.Object);
            var lexScore = (double)matchCount / keywords.Count;
            var simScore = (queryEmbedding is not null && fact.Embedding is not null)
                ? DotProduct(queryEmbedding, fact.Embedding)
                : 0.0;

            results.Add(new StoreCandidate<MemoryFact>(fact, lexScore, simScore));
        }

        return results.OrderByDescending(c => c.LexicalScore * 0.4 + c.SimilarityScore * 0.6).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreCandidate<MemoryEvent>>> SearchEventsAsync(
        string query, float[]? queryEmbedding, int maxResults, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
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
            SELECT event_id, profile_id, type, title, summary, when_iso,
                   confidence, weight, sensitivity, source_turn_id, source_hash,
                   dedupe_key, origin, created_at, updated_at, source_ref,
                   embedding, embedding_model, embedding_dims
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
            var evt = ReadEvent(reader);
            var matchCount = CountKeywordMatches(keywords,
                evt.Type, evt.Title, evt.Summary ?? "");
            var lexScore = (double)matchCount / keywords.Count;
            var simScore = (queryEmbedding is not null && evt.Embedding is not null)
                ? DotProduct(queryEmbedding, evt.Embedding)
                : 0.0;

            results.Add(new StoreCandidate<MemoryEvent>(evt, lexScore, simScore));
        }

        return results.OrderByDescending(c => c.LexicalScore * 0.4 + c.SimilarityScore * 0.6).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoreCandidate<MemoryChunk>>> SearchChunksAsync(
        string query, float[]? queryEmbedding, int maxResults, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);

        // Build FTS5 query from tokenized keywords
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrEmpty(ftsQuery))
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.chunk_id, c.source_type, c.source_ref, c.text,
                   c.when_iso, c.sensitivity, c.embedding, c.embedding_model, c.embedding_dims,
                   fts.rank AS bm25_score
            FROM   memory_chunks_fts fts
            JOIN   memory_chunks c ON c.rowid = fts.rowid
            WHERE  memory_chunks_fts MATCH @query
              AND  c.is_deleted = 0
              AND  c.sensitivity != 'secret'
            ORDER  BY fts.rank
            LIMIT  @limit
            """;
        cmd.Parameters.AddWithValue("@query", ftsQuery);
        cmd.Parameters.AddWithValue("@limit", maxResults);

        var raw = new List<(MemoryChunk Chunk, double RawBm25)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var chunk = new MemoryChunk
            {
                ChunkId = reader.GetString(0),
                SourceType = reader.GetString(1),
                SourceRef = reader.IsDBNull(2) ? null : reader.GetString(2),
                Text = reader.GetString(3),
                WhenIso = reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                Sensitivity = ParseSensitivity(reader.GetString(5)),
                Embedding = reader.IsDBNull(6) ? null : ParseEmbedding((byte[])reader[6]),
                EmbeddingModel = reader.IsDBNull(7) ? null : reader.GetString(7),
                EmbeddingDims = reader.IsDBNull(8) ? null : reader.GetInt32(8)
            };

            raw.Add((chunk, reader.GetDouble(9)));
        }

        if (raw.Count == 0)
            return [];

        // Normalize BM25 scores to [0, 1].
        // bm25() returns negative values: more negative = better match.
        var best = raw.Min(r => r.RawBm25);   // most negative = best
        var worst = raw.Max(r => r.RawBm25);    // least negative = worst
        var range = worst - best;

        return raw.Select(r =>
        {
            var normalized = range > 1e-10
                ? (worst - r.RawBm25) / range
                : 1.0;

            var simScore = (queryEmbedding is not null && r.Chunk.Embedding is not null)
                ? DotProduct(queryEmbedding, r.Chunk.Embedding)
                : 0.0;

            return new StoreCandidate<MemoryChunk>(r.Chunk, normalized, simScore);
        })
        .OrderByDescending(c => c.LexicalScore * 0.4 + c.SimilarityScore * 0.6)
        .ToList();
    }

    // ─────────────────────────────────────────────────────────────────
    // Lookup Operations (conflict / duplicate detection)
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryFact>> FindMatchingFactsAsync(
        string subject, string predicate, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT memory_id, profile_id, subject, predicate, object,
                   confidence, weight, sensitivity, source_turn_id, source_hash,
                   dedupe_key, origin, created_at, updated_at, source_ref,
                   embedding, embedding_model, embedding_dims
            FROM   memory_facts
            WHERE  is_deleted = 0
              AND  LOWER(subject)   = LOWER(@subj)
              AND  LOWER(predicate) = LOWER(@pred)
            ORDER  BY updated_at DESC
            """;
        cmd.Parameters.AddWithValue("@subj", subject);
        cmd.Parameters.AddWithValue("@pred", predicate);

        var items = new List<MemoryFact>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadFact(reader));
        return items;
    }

    /// <inheritdoc />
    public async Task<MemoryFact?> FindFactByIdAsync(
        string memoryId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT memory_id, profile_id, subject, predicate, object,
                   confidence, weight, sensitivity, source_turn_id, source_hash,
                   dedupe_key, origin, created_at, updated_at, source_ref,
                   embedding, embedding_model, embedding_dims
            FROM   memory_facts
            WHERE  is_deleted = 0 AND memory_id = @id
            LIMIT  1
            """;
        cmd.Parameters.AddWithValue("@id", memoryId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadFact(reader) : null;
    }

    // ─────────────────────────────────────────────────────────────────
    // Write Operations (upsert semantics — idempotent by design)
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        string targetId = fact.MemoryId;

        if (!string.IsNullOrEmpty(fact.DedupeKey))
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = "SELECT memory_id FROM memory_facts WHERE dedupe_key = @dk LIMIT 1";
            findCmd.Parameters.AddWithValue("@dk", fact.DedupeKey);
            var existingId = (string?)await findCmd.ExecuteScalarAsync(ct);
            if (existingId != null) targetId = existingId;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_facts
                (memory_id, profile_id, subject, predicate, object,
                 confidence, weight, sensitivity, source_turn_id, source_hash,
                 dedupe_key, origin, created_at, updated_at,
                 source_ref, is_deleted, embedding, embedding_model, embedding_dims)
            VALUES (@id, @profileId, @subj, @pred, @obj,
                    @conf, @weight, @sens, @turnId, @hash,
                    @dedupe, @origin, @created, @updated,
                    @src, 0, @emb, @embModel, @embDims)
            """;
        cmd.Parameters.AddWithValue("@id", targetId);
        cmd.Parameters.AddWithValue("@profileId", (object?)fact.ProfileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@subj", fact.Subject);
        cmd.Parameters.AddWithValue("@pred", fact.Predicate);
        cmd.Parameters.AddWithValue("@obj", fact.Object);
        cmd.Parameters.AddWithValue("@conf", fact.Confidence);
        cmd.Parameters.AddWithValue("@weight", fact.Weight);
        cmd.Parameters.AddWithValue("@sens", fact.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@turnId", (object?)fact.SourceTurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", (object?)fact.SourceHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dedupe", (object?)fact.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", (object?)fact.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", fact.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", fact.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@src", (object?)fact.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb", fact.Embedding is not null ? SerializeEmbedding(fact.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@embModel", (object?)fact.EmbeddingModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embDims", (object?)fact.EmbeddingDims ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task StoreEventAsync(MemoryEvent evt, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        string targetId = evt.EventId;

        if (!string.IsNullOrEmpty(evt.DedupeKey))
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = "SELECT event_id FROM memory_events WHERE dedupe_key = @dk LIMIT 1";
            findCmd.Parameters.AddWithValue("@dk", evt.DedupeKey);
            var existingId = (string?)await findCmd.ExecuteScalarAsync(ct);
            if (existingId != null) targetId = existingId;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_events
                (event_id, profile_id, type, title, summary, when_iso,
                 confidence, weight, sensitivity, source_turn_id, source_hash,
                 dedupe_key, origin, created_at, updated_at,
                 source_ref, is_deleted, embedding, embedding_model, embedding_dims)
            VALUES (@id, @profileId, @type, @title, @summary, @when,
                    @conf, @weight, @sens, @turnId, @hash,
                    @dedupe, @origin, @created, @updated,
                    @src, 0, @emb, @embModel, @embDims)
            """;
        cmd.Parameters.AddWithValue("@id", targetId);
        cmd.Parameters.AddWithValue("@profileId", (object?)evt.ProfileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@type", evt.Type);
        cmd.Parameters.AddWithValue("@title", evt.Title);
        cmd.Parameters.AddWithValue("@summary", (object?)evt.Summary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@when", evt.WhenIso.HasValue
            ? evt.WhenIso.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", evt.Confidence);
        cmd.Parameters.AddWithValue("@weight", evt.Weight);
        cmd.Parameters.AddWithValue("@sens", evt.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@turnId", (object?)evt.SourceTurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", (object?)evt.SourceHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dedupe", (object?)evt.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", (object?)evt.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created", evt.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", evt.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@src", (object?)evt.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@emb", evt.Embedding is not null ? SerializeEmbedding(evt.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@embModel", (object?)evt.EmbeddingModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embDims", (object?)evt.EmbeddingDims ?? DBNull.Value);
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
                 when_iso, sensitivity, is_deleted, embedding, embedding_model, embedding_dims)
            VALUES (@id, @srcType, @srcRef, @text,
                    @when, @sens, 0, @emb, @embModel, @embDims)
            """;
        cmd.Parameters.AddWithValue("@id", chunk.ChunkId);
        cmd.Parameters.AddWithValue("@srcType", chunk.SourceType);
        cmd.Parameters.AddWithValue("@srcRef", (object?)chunk.SourceRef ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@text", chunk.Text);
        cmd.Parameters.AddWithValue("@when", chunk.WhenIso.HasValue
            ? chunk.WhenIso.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@sens", chunk.Sensitivity.ToString().ToLowerInvariant());
        cmd.Parameters.AddWithValue("@emb", chunk.Embedding is not null
            ? SerializeEmbedding(chunk.Embedding) : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@embModel", (object?)chunk.EmbeddingModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embDims", (object?)chunk.EmbeddingDims ?? DBNull.Value);
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
              SELECT memory_id, profile_id, subject, predicate, object,
                     confidence, weight, sensitivity, source_turn_id, source_hash,
                     dedupe_key, origin, created_at, updated_at, source_ref
              FROM   memory_facts
              WHERE  is_deleted = 0
                AND  (subject LIKE @f OR predicate LIKE @f OR object LIKE @f)
              ORDER  BY updated_at DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT memory_id, profile_id, subject, predicate, object,
                     confidence, weight, sensitivity, source_turn_id, source_hash,
                     dedupe_key, origin, created_at, updated_at, source_ref
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
            items.Add(ReadFactBrowse(reader));

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
              SELECT event_id, profile_id, type, title, summary, when_iso,
                     confidence, weight, sensitivity, source_turn_id, source_hash,
                     dedupe_key, origin, created_at, updated_at, source_ref
              FROM   memory_events
              WHERE  is_deleted = 0
                AND  (type LIKE @f OR title LIKE @f OR COALESCE(summary,'') LIKE @f)
              ORDER  BY when_iso DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT event_id, profile_id, type, title, summary, when_iso,
                     confidence, weight, sensitivity, source_turn_id, source_hash,
                     dedupe_key, origin, created_at, updated_at, source_ref
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
            items.Add(ReadEventBrowse(reader));

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
            items.Add(ReadChunkBrowse(reader));

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
    // Profile Card Operations
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ProfileCard?> GetUserProfileAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT profile_id, kind, display_name, relationship,
                   aliases, profile_json, updated_at
            FROM   profile_cards
            WHERE  is_deleted = 0 AND kind = 'user'
            LIMIT  1
            """;

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return ReadProfileCard(reader);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileCard>> ListProfilesAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT profile_id, kind, display_name, relationship,
                   aliases, profile_json, updated_at
            FROM   profile_cards
            WHERE  is_deleted = 0
            ORDER  BY kind ASC, display_name ASC
            """;

        var items = new List<ProfileCard>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadProfileCard(reader));
        return items;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProfileCard>> SearchPersonProfilesAsync(
        string query, int maxResults, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var keywords = TokenizeQuery(query);
        if (keywords.Count == 0) return [];

        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();
        for (var i = 0; i < keywords.Count; i++)
        {
            var p = $"@kw{i}";
            conditions.Add(
                $"(display_name LIKE {p} OR COALESCE(relationship,'') LIKE {p} OR COALESCE(aliases,'') LIKE {p})");
            cmd.Parameters.AddWithValue(p, $"%{keywords[i]}%");
        }

        cmd.CommandText = $"""
            SELECT profile_id, kind, display_name, relationship,
                   aliases, profile_json, updated_at
            FROM   profile_cards
            WHERE  is_deleted = 0
              AND  kind != 'user'
              AND  ({string.Join(" OR ", conditions)})
            LIMIT  @limit
            """;
        cmd.Parameters.AddWithValue("@limit", maxResults);

        var items = new List<ProfileCard>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadProfileCard(reader));
        return items;
    }

    /// <inheritdoc />
    public async Task StoreProfileAsync(ProfileCard profile, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO profile_cards
                (profile_id, kind, display_name, relationship,
                 aliases, profile_json, updated_at, is_deleted)
            VALUES (@id, @kind, @name, @rel,
                    @aliases, @json, @updated, 0)
            """;
        cmd.Parameters.AddWithValue("@id", profile.ProfileId);
        cmd.Parameters.AddWithValue("@kind", profile.Kind);
        cmd.Parameters.AddWithValue("@name", profile.DisplayName);
        cmd.Parameters.AddWithValue("@rel", (object?)profile.Relationship ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@aliases", (object?)profile.Aliases ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@json", profile.ProfileJson);
        cmd.Parameters.AddWithValue("@updated", profile.UpdatedAt.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteProfileAsync(string profileId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE profile_cards SET is_deleted = 1 WHERE profile_id = @id";
        cmd.Parameters.AddWithValue("@id", profileId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Memory Nugget Operations
    // ─────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryNugget>> GetGreetingNuggetsAsync(
        int maxResults = 2, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();

        // Only low-sensitivity nuggets with greeting-relevant tags.
        // Scored in-app rather than SQL (the table is small enough).
        cmd.CommandText = """
            SELECT nugget_id, text, tags, weight, pin_level,
                   sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                   use_count, last_used_at, created_at, updated_at,
                   embedding, embedding_model, embedding_dims, chunk_citation
            FROM   memory_nuggets
            WHERE  is_deleted = 0
              AND  sensitivity = 'low'
              AND  (   tags LIKE '%;identity;%'
                    OR tags LIKE '%;preference;%'
                    OR tags LIKE '%;active_project;%'
                    OR tags LIKE '%;routine;%')
            """;

        var candidates = new List<MemoryNugget>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            candidates.Add(ReadNugget(reader));

        // Score and take top N
        return candidates
            .Select(n => (Nugget: n, Score: ScoreNugget(n)))
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => x.Nugget)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryNugget>> SearchNuggetsAsync(
        string query, int maxResults = 5, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var keywords = TokenizeQuery(query);
        if (keywords.Count == 0) return [];

        using var cmd = conn.CreateCommand();
        var conditions = new List<string>();
        for (var i = 0; i < keywords.Count; i++)
        {
            var p = $"@kw{i}";
            conditions.Add($"(text LIKE {p} OR COALESCE(tags,'') LIKE {p})");
            cmd.Parameters.AddWithValue(p, $"%{keywords[i]}%");
        }

        cmd.CommandText = $"""
            SELECT nugget_id, text, tags, weight, pin_level,
                   sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                   use_count, last_used_at, created_at, updated_at,
                   embedding, embedding_model, embedding_dims, chunk_citation
            FROM   memory_nuggets
            WHERE  is_deleted = 0
              AND  sensitivity != 'high'
              AND  ({string.Join(" OR ", conditions)})
            """;

        var candidates = new List<MemoryNugget>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            candidates.Add(ReadNugget(reader));

        // Combine keyword match ratio with nugget score
        return candidates
            .Select(n =>
            {
                var text = $"{n.Text} {n.Tags}".ToLowerInvariant();
                var hitRatio = (double)keywords.Count(kw =>
                    text.Contains(kw, StringComparison.OrdinalIgnoreCase)) / keywords.Count;
                var score = hitRatio * 0.4 + ScoreNugget(n) * 0.6;
                return (Nugget: n, Score: score);
            })
            .OrderByDescending(x => x.Score)
            .Take(maxResults)
            .Select(x => x.Nugget)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<(IReadOnlyList<MemoryNugget> Items, int TotalCount)> ListNuggetsAsync(
        string? filter, int skip, int take, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var hasFilter = !string.IsNullOrWhiteSpace(filter);

        // ── Count ──
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = hasFilter
            ? """
              SELECT COUNT(*) FROM memory_nuggets
              WHERE is_deleted = 0
                AND (text LIKE @f OR COALESCE(tags,'') LIKE @f)
              """
            : "SELECT COUNT(*) FROM memory_nuggets WHERE is_deleted = 0";
        if (hasFilter)
            countCmd.Parameters.AddWithValue("@f", $"%{filter}%");

        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // ── Page ──
        using var cmd = conn.CreateCommand();
        cmd.CommandText = hasFilter
            ? """
              SELECT nugget_id, text, tags, weight, pin_level,
                     sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                     use_count, last_used_at, created_at, updated_at,
                     chunk_citation
              FROM   memory_nuggets
              WHERE  is_deleted = 0
                AND  (text LIKE @f OR COALESCE(tags,'') LIKE @f)
              ORDER  BY created_at DESC
              LIMIT  @take OFFSET @skip
              """
            : """
              SELECT nugget_id, text, tags, weight, pin_level,
                     sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                     use_count, last_used_at, created_at, updated_at,
                     chunk_citation
              FROM   memory_nuggets
              WHERE  is_deleted = 0
              ORDER  BY created_at DESC
              LIMIT  @take OFFSET @skip
              """;
        if (hasFilter)
            cmd.Parameters.AddWithValue("@f", $"%{filter}%");
        cmd.Parameters.AddWithValue("@take", take);
        cmd.Parameters.AddWithValue("@skip", skip);

        var items = new List<MemoryNugget>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadNuggetBrowse(reader));

        return (items, total);
    }

    /// <inheritdoc />
    public async Task<MemoryNugget?> FindNuggetByIdAsync(
        string nuggetId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nugget_id, text, tags, weight, pin_level,
                   sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                   use_count, last_used_at, created_at, updated_at,
                   chunk_citation
            FROM   memory_nuggets
            WHERE  is_deleted = 0 AND nugget_id = @id
            LIMIT  1
            """;
        cmd.Parameters.AddWithValue("@id", nuggetId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadNuggetBrowse(reader) : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryNugget>> ListPinnedNuggetsAsync(
        int maxResults, CancellationToken ct = default)
    {
        if (maxResults < 1) return Array.Empty<MemoryNugget>();

        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT nugget_id, text, tags, weight, pin_level,
                   sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                   use_count, last_used_at, created_at, updated_at,
                   chunk_citation
            FROM   memory_nuggets
            WHERE  is_deleted = 0 AND pin_level >= 1
            ORDER  BY pin_level DESC, use_count DESC, updated_at DESC
            LIMIT  @take
            """;
        cmd.Parameters.AddWithValue("@take", maxResults);

        var items = new List<MemoryNugget>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(ReadNuggetBrowse(reader));
        return items;
    }

    /// <inheritdoc />
    public async Task<MemoryNugget?> SetNuggetPinLevelAsync(
        string nuggetId, int pinLevel, CancellationToken ct = default)
    {
        var normalizedPin = Math.Clamp(pinLevel, 0, 2);
        var conn = await GetConnectionAsync(ct);
        using var update = conn.CreateCommand();
        update.CommandText = """
            UPDATE memory_nuggets
            SET    pin_level = @pin,
                   updated_at = @updated
            WHERE  is_deleted = 0 AND nugget_id = @id
            """;
        update.Parameters.AddWithValue("@pin", normalizedPin);
        update.Parameters.AddWithValue("@updated", DateTimeOffset.UtcNow.ToString("o"));
        update.Parameters.AddWithValue("@id", nuggetId);

        var changed = await update.ExecuteNonQueryAsync(ct);
        return changed == 0
            ? null
            : await FindNuggetByIdAsync(nuggetId, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task StoreNuggetAsync(MemoryNugget nugget, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        string targetId = nugget.NuggetId;

        if (!string.IsNullOrEmpty(nugget.DedupeKey))
        {
            using var findCmd = conn.CreateCommand();
            findCmd.CommandText = "SELECT nugget_id FROM memory_nuggets WHERE dedupe_key = @dk LIMIT 1";
            findCmd.Parameters.AddWithValue("@dk", nugget.DedupeKey);
            var existingId = (string?)await findCmd.ExecuteScalarAsync(ct);
            if (existingId != null) targetId = existingId;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO memory_nuggets
                (nugget_id, text, tags, weight, pin_level,
                 sensitivity, source_turn_id, source_hash, dedupe_key, origin,
                 use_count, last_used_at, created_at, updated_at, is_deleted,
                 embedding, embedding_model, embedding_dims, chunk_citation)
            VALUES (@id, @text, @tags, @weight, @pin,
                    @sens, @turnId, @hash, @dedupe, @origin,
                    @useCount, @lastUsed, @created, @updated, 0,
                    @emb, @embModel, @embDims, @citation)
            """;
        cmd.Parameters.AddWithValue("@id", targetId);
        cmd.Parameters.AddWithValue("@text", nugget.Text);
        cmd.Parameters.AddWithValue("@tags", (object?)nugget.Tags ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@weight", nugget.Weight);
        cmd.Parameters.AddWithValue("@pin", nugget.PinLevel);
        cmd.Parameters.AddWithValue("@sens", nugget.Sensitivity);
        cmd.Parameters.AddWithValue("@turnId", (object?)nugget.SourceTurnId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", (object?)nugget.SourceHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dedupe", (object?)nugget.DedupeKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@origin", (object?)nugget.Origin ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@useCount", nugget.UseCount);
        cmd.Parameters.AddWithValue("@lastUsed", nugget.LastUsedAt.HasValue
            ? nugget.LastUsedAt.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@created", nugget.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@updated", nugget.UpdatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@emb", nugget.Embedding is not null ? SerializeEmbedding(nugget.Embedding) : DBNull.Value);
        cmd.Parameters.AddWithValue("@embModel", (object?)nugget.EmbeddingModel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@embDims", (object?)nugget.EmbeddingDims ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@citation", (object?)nugget.ChunkCitation ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task TouchNuggetAsync(string nuggetId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE memory_nuggets
            SET    use_count    = use_count + 1,
                   last_used_at = @now
            WHERE  nugget_id    = @id
              AND  is_deleted   = 0
            """;
        cmd.Parameters.AddWithValue("@id", nuggetId);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task DeleteNuggetAsync(string nuggetId, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE memory_nuggets SET is_deleted = 1 WHERE nugget_id = @id";
        cmd.Parameters.AddWithValue("@id", nuggetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────
    // Row Readers — one per entity. Each assumes the standard SELECT
    // column order defined in the queries above.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a <see cref="MemoryFact"/> from the standard 15-column
    /// SELECT for memory_facts.
    /// </summary>
    private static MemoryFact ReadFact(SqliteDataReader r) => new()
    {
        MemoryId = r.GetString(0),
        ProfileId = r.IsDBNull(1) ? null : r.GetString(1),
        Subject = r.GetString(2),
        Predicate = r.GetString(3),
        Object = r.GetString(4),
        Confidence = r.GetDouble(5),
        Weight = r.GetDouble(6),
        Sensitivity = ParseSensitivity(r.GetString(7)),
        SourceTurnId = r.IsDBNull(8) ? null : r.GetString(8),
        SourceHash = r.IsDBNull(9) ? null : r.GetString(9),
        DedupeKey = r.IsDBNull(10) ? null : r.GetString(10),
        Origin = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = ParseTimestamp(r.GetString(12)),
        UpdatedAt = ParseTimestamp(r.GetString(13)),
        SourceRef = r.IsDBNull(14) ? null : r.GetString(14),
        Embedding = r.IsDBNull(15) ? null : ParseEmbedding((byte[])r[15]),
        EmbeddingModel = r.IsDBNull(16) ? null : r.GetString(16),
        EmbeddingDims = r.IsDBNull(17) ? null : r.GetInt32(17)
    };

    private static MemoryFact ReadFactBrowse(SqliteDataReader r) => new()
    {
        MemoryId = r.GetString(0),
        ProfileId = r.IsDBNull(1) ? null : r.GetString(1),
        Subject = r.GetString(2),
        Predicate = r.GetString(3),
        Object = r.GetString(4),
        Confidence = r.GetDouble(5),
        Weight = r.GetDouble(6),
        Sensitivity = ParseSensitivity(r.GetString(7)),
        SourceTurnId = r.IsDBNull(8) ? null : r.GetString(8),
        SourceHash = r.IsDBNull(9) ? null : r.GetString(9),
        DedupeKey = r.IsDBNull(10) ? null : r.GetString(10),
        Origin = r.IsDBNull(11) ? null : r.GetString(11),
        CreatedAt = ParseTimestamp(r.GetString(12)),
        UpdatedAt = ParseTimestamp(r.GetString(13)),
        SourceRef = r.IsDBNull(14) ? null : r.GetString(14),
        Embedding = null
    };

    /// <summary>
    /// Reads a <see cref="MemoryEvent"/> from the standard 16-column
    /// SELECT for memory_events.
    /// </summary>
    private static MemoryEvent ReadEvent(SqliteDataReader r) => new()
    {
        EventId = r.GetString(0),
        ProfileId = r.IsDBNull(1) ? null : r.GetString(1),
        Type = r.GetString(2),
        Title = r.GetString(3),
        Summary = r.IsDBNull(4) ? null : r.GetString(4),
        WhenIso = r.IsDBNull(5) ? null : ParseTimestamp(r.GetString(5)),
        Confidence = r.GetDouble(6),
        Weight = r.GetDouble(7),
        Sensitivity = ParseSensitivity(r.GetString(8)),
        SourceTurnId = r.IsDBNull(9) ? null : r.GetString(9),
        SourceHash = r.IsDBNull(10) ? null : r.GetString(10),
        DedupeKey = r.IsDBNull(11) ? null : r.GetString(11),
        Origin = r.IsDBNull(12) ? null : r.GetString(12),
        CreatedAt = ParseTimestamp(r.GetString(13)),
        UpdatedAt = ParseTimestamp(r.GetString(14)),
        SourceRef = r.IsDBNull(15) ? null : r.GetString(15),
        Embedding = r.IsDBNull(16) ? null : ParseEmbedding((byte[])r[16]),
        EmbeddingModel = r.IsDBNull(17) ? null : r.GetString(17),
        EmbeddingDims = r.IsDBNull(18) ? null : r.GetInt32(18)
    };

    private static MemoryEvent ReadEventBrowse(SqliteDataReader r) => new()
    {
        EventId = r.GetString(0),
        ProfileId = r.IsDBNull(1) ? null : r.GetString(1),
        Type = r.GetString(2),
        Title = r.GetString(3),
        Summary = r.IsDBNull(4) ? null : r.GetString(4),
        WhenIso = r.IsDBNull(5) ? null : ParseTimestamp(r.GetString(5)),
        Confidence = r.GetDouble(6),
        Weight = r.GetDouble(7),
        Sensitivity = ParseSensitivity(r.GetString(8)),
        SourceTurnId = r.IsDBNull(9) ? null : r.GetString(9),
        SourceHash = r.IsDBNull(10) ? null : r.GetString(10),
        DedupeKey = r.IsDBNull(11) ? null : r.GetString(11),
        Origin = r.IsDBNull(12) ? null : r.GetString(12),
        CreatedAt = ParseTimestamp(r.GetString(13)),
        UpdatedAt = ParseTimestamp(r.GetString(14)),
        SourceRef = r.IsDBNull(15) ? null : r.GetString(15),
        Embedding = null
    };

    /// <summary>
    /// Reads a <see cref="MemoryChunk"/> from the browse 6-column
    /// SELECT: chunk_id, source_type, source_ref, text, when_iso,
    /// sensitivity.  Embedding is omitted for browse queries.
    /// </summary>
    private static MemoryChunk ReadChunkBrowse(SqliteDataReader r) => new()
    {
        ChunkId = r.GetString(0),
        SourceType = r.GetString(1),
        SourceRef = r.IsDBNull(2) ? null : r.GetString(2),
        Text = r.GetString(3),
        WhenIso = r.IsDBNull(4) ? null : ParseTimestamp(r.GetString(4)),
        Sensitivity = ParseSensitivity(r.GetString(5)),
        Embedding = null
    };

    private static ProfileCard ReadProfileCard(SqliteDataReader r) => new()
    {
        ProfileId = r.GetString(0),
        Kind = r.GetString(1),
        DisplayName = r.GetString(2),
        Relationship = r.IsDBNull(3) ? null : r.GetString(3),
        Aliases = r.IsDBNull(4) ? null : r.GetString(4),
        ProfileJson = r.GetString(5),
        UpdatedAt = ParseTimestamp(r.GetString(6))
    };

    private static MemoryNugget ReadNugget(SqliteDataReader r) => new()
    {
        NuggetId = r.GetString(0),
        Text = r.GetString(1),
        Tags = r.IsDBNull(2) ? null : r.GetString(2),
        Weight = r.GetDouble(3),
        PinLevel = r.GetInt32(4),
        Sensitivity = r.GetString(5),
        SourceTurnId = r.IsDBNull(6) ? null : r.GetString(6),
        SourceHash = r.IsDBNull(7) ? null : r.GetString(7),
        DedupeKey = r.IsDBNull(8) ? null : r.GetString(8),
        Origin = r.IsDBNull(9) ? null : r.GetString(9),
        UseCount = r.GetInt32(10),
        LastUsedAt = r.IsDBNull(11) ? null : ParseTimestamp(r.GetString(11)),
        CreatedAt = ParseTimestamp(r.GetString(12)),
        UpdatedAt = ParseTimestamp(r.GetString(13)),
        Embedding = r.IsDBNull(14) ? null : ParseEmbedding((byte[])r[14]),
        EmbeddingModel = r.IsDBNull(15) ? null : r.GetString(15),
        EmbeddingDims = r.IsDBNull(16) ? null : r.GetInt32(16),
        ChunkCitation = r.IsDBNull(17) ? null : r.GetString(17)
    };

    private static MemoryNugget ReadNuggetBrowse(SqliteDataReader r) => new()
    {
        NuggetId = r.GetString(0),
        Text = r.GetString(1),
        Tags = r.IsDBNull(2) ? null : r.GetString(2),
        Weight = r.GetDouble(3),
        PinLevel = r.GetInt32(4),
        Sensitivity = r.GetString(5),
        SourceTurnId = r.IsDBNull(6) ? null : r.GetString(6),
        SourceHash = r.IsDBNull(7) ? null : r.GetString(7),
        DedupeKey = r.IsDBNull(8) ? null : r.GetString(8),
        Origin = r.IsDBNull(9) ? null : r.GetString(9),
        UseCount = r.GetInt32(10),
        LastUsedAt = r.IsDBNull(11) ? null : ParseTimestamp(r.GetString(11)),
        CreatedAt = ParseTimestamp(r.GetString(12)),
        UpdatedAt = ParseTimestamp(r.GetString(13)),
        Embedding = null,
        ChunkCitation = r.IsDBNull(14) ? null : r.GetString(14)
    };

    /// <summary>
    /// Scores a nugget for ranking. Formula:
    /// <c>0.55*pinLevel + 0.25*weight + 0.15*recencyBoost + 0.05*useCountBoost</c>
    /// </summary>
    private static double ScoreNugget(MemoryNugget n)
    {
        var pinNorm = Math.Min(n.PinLevel, 2) / 2.0;
        var recency = n.LastUsedAt.HasValue
            ? Math.Exp(-(DateTimeOffset.UtcNow - n.LastUsedAt.Value).TotalDays / 14.0)
            : 0.1; // never used → low recency
        var useBoost = Math.Log(1 + n.UseCount) / 5.0;

        return 0.55 * pinNorm
             + 0.25 * n.Weight
             + 0.15 * recency
             + 0.05 * useBoost;
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

    private static Sensitivity ParseSensitivity(string? value) =>
        MemoryParsing.ParseSensitivity(value);

    private static DateTimeOffset ParseTimestamp(string iso) =>
        DateTimeOffset.TryParse(iso, out var dto) ? dto : DateTimeOffset.MinValue;

    /// <summary>
    /// Parses a stored embedding BLOB (float32 array) back to float[].
    /// </summary>
    private static float[]? ParseEmbedding(byte[] blob)
    {
        if (blob.Length == 0 || blob.Length % 4 != 0)
            return null;

        var count = blob.Length / 4;
        var result = new float[count];
        Buffer.BlockCopy(blob, 0, result, 0, blob.Length);
        return result;
    }

    /// <summary>
    /// Computes the dot product of two float arrays.
    /// Used for cosine similarity (assuming normalized embeddings).
    /// </summary>
    private static double DotProduct(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }
        return dot;
    }

    // ─────────────────────────────────────────────────────────────────
    // Connection Management
    // ─────────────────────────────────────────────────────────────────

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_connection is not null && _schemaReady)
            return _connection;

        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not null && _schemaReady)
                return _connection;

            if (_connection is null)
            {
                _connection = new SqliteConnection(_connectionString);
                await _connection.OpenAsync(ct);

                // WAL mode for better concurrent read performance
                using var pragma = _connection.CreateCommand();
                pragma.CommandText = "PRAGMA journal_mode=WAL";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            // Auto-run schema + migrations on first connection to guarantee
            // all columns exist before any query can execute. This prevents
            // "no such column" errors when the DB was created by an older version.
            if (!_schemaReady)
            {
                await SchemaInitializer.InitializeAsync(_connection, ct);
                _schemaReady = true;
            }

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
