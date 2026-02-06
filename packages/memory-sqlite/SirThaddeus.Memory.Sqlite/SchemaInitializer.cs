using Microsoft.Data.Sqlite;

namespace SirThaddeus.Memory.Sqlite;

/// <summary>
/// Creates the memory database tables if they don't exist.
/// Idempotent — safe to call on every startup.
/// </summary>
public static class SchemaInitializer
{
    // Each statement is executed individually because SQLite's
    // command processor is more reliable with single statements.
    private static readonly string[] Statements =
    [
        // ── Structured facts ─────────────────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS memory_facts (
            memory_id   TEXT PRIMARY KEY,
            subject     TEXT NOT NULL,
            predicate   TEXT NOT NULL,
            object      TEXT NOT NULL,
            confidence  REAL NOT NULL DEFAULT 1.0,
            sensitivity TEXT NOT NULL DEFAULT 'public',
            created_at  TEXT NOT NULL,
            updated_at  TEXT NOT NULL,
            source_ref  TEXT,
            is_deleted  INTEGER NOT NULL DEFAULT 0
        )
        """,

        // ── Structured events ────────────────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS memory_events (
            event_id    TEXT PRIMARY KEY,
            type        TEXT NOT NULL,
            title       TEXT NOT NULL,
            summary     TEXT,
            when_iso    TEXT,
            confidence  REAL NOT NULL DEFAULT 1.0,
            sensitivity TEXT NOT NULL DEFAULT 'public',
            source_ref  TEXT,
            is_deleted  INTEGER NOT NULL DEFAULT 0
        )
        """,

        // ── Chunks (conversation / document fragments) ───────────────
        """
        CREATE TABLE IF NOT EXISTS memory_chunks (
            chunk_id    TEXT PRIMARY KEY,
            source_type TEXT NOT NULL DEFAULT 'doc',
            source_ref  TEXT,
            text        TEXT NOT NULL,
            when_iso    TEXT,
            sensitivity TEXT NOT NULL DEFAULT 'public',
            is_deleted  INTEGER NOT NULL DEFAULT 0,
            embedding   BLOB
        )
        """,

        // ── FTS5 index for BM25 chunk retrieval ─────────────────────
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS memory_chunks_fts USING fts5(
            text,
            content='memory_chunks',
            content_rowid='rowid'
        )
        """,

        // ── Triggers to keep FTS index in sync ──────────────────────
        """
        CREATE TRIGGER IF NOT EXISTS memory_chunks_ai
            AFTER INSERT ON memory_chunks BEGIN
            INSERT INTO memory_chunks_fts(rowid, text)
                VALUES (new.rowid, new.text);
        END
        """,

        """
        CREATE TRIGGER IF NOT EXISTS memory_chunks_ad
            AFTER DELETE ON memory_chunks BEGIN
            INSERT INTO memory_chunks_fts(memory_chunks_fts, rowid, text)
                VALUES ('delete', old.rowid, old.text);
        END
        """,

        """
        CREATE TRIGGER IF NOT EXISTS memory_chunks_au
            AFTER UPDATE ON memory_chunks BEGIN
            INSERT INTO memory_chunks_fts(memory_chunks_fts, rowid, text)
                VALUES ('delete', old.rowid, old.text);
            INSERT INTO memory_chunks_fts(rowid, text)
                VALUES (new.rowid, new.text);
        END
        """
    ];

    /// <summary>
    /// Ensures all required tables and indexes exist in the database.
    /// </summary>
    public static async Task InitializeAsync(
        SqliteConnection connection, CancellationToken ct = default)
    {
        foreach (var sql in Statements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
