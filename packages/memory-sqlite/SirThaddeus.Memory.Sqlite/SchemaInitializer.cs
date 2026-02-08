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
            profile_id  TEXT,
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
            profile_id  TEXT,
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
        """,

        // ── Profile Cards ────────────────────────────────────────────
        // Lightweight identity cards for the user ("me") and other
        // people they mention (wife, son, coworker, etc.).
        // profile_json holds the structured fields as a JSON blob so
        // the schema doesn't need a migration for every new field.
        """
        CREATE TABLE IF NOT EXISTS profile_cards (
            profile_id    TEXT PRIMARY KEY,
            kind          TEXT NOT NULL DEFAULT 'user',
            display_name  TEXT NOT NULL,
            relationship  TEXT,
            aliases       TEXT,
            profile_json  TEXT NOT NULL DEFAULT '{}',
            updated_at    TEXT NOT NULL,
            is_deleted    INTEGER NOT NULL DEFAULT 0
        )
        """,

        // ── Memory Nuggets ───────────────────────────────────────────
        // Short, atomic, composable personal facts:
        //   "User often asks for help with math homework."
        //   "User prefers blunt, practical feedback."
        //
        // Retrieved by tag + score, not free-text search. Scored via
        // pin_level, weight, recency, and use_count — no embeddings
        // required in V1.
        """
        CREATE TABLE IF NOT EXISTS memory_nuggets (
            nugget_id     TEXT PRIMARY KEY,
            text          TEXT NOT NULL,
            tags          TEXT,
            weight        REAL NOT NULL DEFAULT 0.65,
            pin_level     INTEGER NOT NULL DEFAULT 0,
            sensitivity   TEXT NOT NULL DEFAULT 'low',
            use_count     INTEGER NOT NULL DEFAULT 0,
            last_used_at  TEXT,
            created_at    TEXT NOT NULL,
            is_deleted    INTEGER NOT NULL DEFAULT 0,
            embedding     BLOB
        )
        """
    ];

    // ── Migrations ────────────────────────────────────────────────────
    // ALTER TABLE statements to add columns to existing databases.
    // Each is wrapped in a try/catch because SQLite throws if the
    // column already exists and there is no IF NOT EXISTS for ALTER.
    private static readonly string[] Migrations =
    [
        "ALTER TABLE memory_facts ADD COLUMN profile_id TEXT",
        "ALTER TABLE memory_events ADD COLUMN profile_id TEXT"
    ];

    /// <summary>
    /// Ensures all required tables and indexes exist in the database.
    /// Also runs forward-only migrations for schema evolution.
    /// </summary>
    public static async Task InitializeAsync(
        SqliteConnection connection, CancellationToken ct = default)
    {
        // Create tables / indexes / triggers
        foreach (var sql in Statements)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Apply migrations (idempotent — ignores "duplicate column" errors)
        foreach (var migration in Migrations)
        {
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = migration;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException)
            {
                // Column already exists — expected for repeat startups
            }
        }
    }
}
