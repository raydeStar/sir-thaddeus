using System;
using System.IO;
using Microsoft.Data.Sqlite;
using SirThaddeus.Memory.Sqlite;

namespace PopulateTestMemory;

/// <summary>
/// Quick utility to populate the memory database with test data.
/// Run this before testing memory retrieval in the desktop runtime.
///
/// Usage:
///   dotnet run --project tools/PopulateTestMemory.csproj
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dbPath = Path.Combine(localAppData, "SirThaddeus", "memory.db");
        var dbDir = Path.GetDirectoryName(dbPath);

        if (!string.IsNullOrEmpty(dbDir) && !Directory.Exists(dbDir))
            Directory.CreateDirectory(dbDir);

        Console.WriteLine($"Initializing memory database at: {dbPath}");

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Initialize schema
        SchemaInitializer.InitializeAsync(connection).GetAwaiter().GetResult();
        Console.WriteLine("✓ Schema initialized");

        // Insert test facts
        InsertTestFacts(connection);
        Console.WriteLine("✓ Inserted test facts");

        // Insert test events
        InsertTestEvents(connection);
        Console.WriteLine("✓ Inserted test events");

        // Insert test chunks
        InsertTestChunks(connection);
        Console.WriteLine("✓ Inserted test chunks");

        Console.WriteLine("\n✓ Test data populated successfully!");
        Console.WriteLine($"Database location: {dbPath}");
        Console.WriteLine("\nNow run the desktop runtime and ask questions like:");
        Console.WriteLine("  - \"What do I prefer?\"");
        Console.WriteLine("  - \"When is my deadline?\"");
        Console.WriteLine("  - \"What did we discuss about the UI?\"");
    }

    static void InsertTestFacts(SqliteConnection conn)
    {
        var facts = new[]
        {
            ("f1", "user", "prefers", "dark mode", "public", "conv-42"),
            ("f2", "user", "likes", "coffee", "public", "conv-42"),
            ("f3", "project", "uses", "csharp", "public", "conv-50"),
            ("f4", "user", "feels", "anxious about deadlines", "personal", "conv-60"),
            ("f5", "user", "knows", "sqlite", "public", "conv-50")
        };

        foreach (var (id, subj, pred, obj, sens, src) in facts)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO memory_facts
                    (memory_id, subject, predicate, object, confidence, sensitivity,
                     created_at, updated_at, source_ref, is_deleted)
                VALUES (@id, @subj, @pred, @obj, 0.95, @sens,
                        datetime('now'), datetime('now'), @src, 0)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@subj", subj);
            cmd.Parameters.AddWithValue("@pred", pred);
            cmd.Parameters.AddWithValue("@obj", obj);
            cmd.Parameters.AddWithValue("@sens", sens);
            cmd.Parameters.AddWithValue("@src", src);
            cmd.ExecuteNonQuery();
        }
    }

    static void InsertTestEvents(SqliteConnection conn)
    {
        var events = new[]
        {
            ("e1", "deadline", "Project Alpha launch", "Beta freeze Feb 10", "2026-02-10T09:00:00Z", "public", "conv-50"),
            ("e2", "meeting", "Team sync", "Discuss memory system", "2026-02-07T14:00:00Z", "public", "conv-50"),
            ("e3", "reminder", "Buy groceries", null, "2026-02-08T18:00:00Z", "personal", "conv-60")
        };

        foreach (var (id, type, title, summary, whenIso, sens, src) in events)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO memory_events
                    (event_id, type, title, summary, when_iso, confidence, sensitivity,
                     source_ref, is_deleted)
                VALUES (@id, @type, @title, @summary, @when, 1.0, @sens, @src, 0)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@summary", summary ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@when", whenIso);
            cmd.Parameters.AddWithValue("@sens", sens);
            cmd.Parameters.AddWithValue("@src", src);
            cmd.ExecuteNonQuery();
        }
    }

    static void InsertTestChunks(SqliteConnection conn)
    {
        var chunks = new[]
        {
            ("c1", "conversation", "conv-42", "We agreed on a dark theme with blue accents for the UI.", "2026-02-04T10:00:00Z", "public"),
            ("c2", "conversation", "conv-50", "The memory system uses SQLite FTS5 for BM25 retrieval and optional embeddings for semantic reranking.", "2026-02-05T15:30:00Z", "public"),
            ("c3", "doc", "doc-1", "User preferences: prefers dark mode, likes coffee, uses Windows 11.", "2026-02-03T08:00:00Z", "public"),
            ("c4", "conversation", "conv-60", "I'm feeling stressed about the upcoming deadline next week.", "2026-02-06T09:00:00Z", "personal")
        };

        foreach (var (id, srcType, srcRef, text, whenIso, sens) in chunks)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO memory_chunks
                    (chunk_id, source_type, source_ref, text, when_iso, sensitivity, is_deleted)
                VALUES (@id, @srcType, @srcRef, @text, @when, @sens, 0)
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@srcType", srcType);
            cmd.Parameters.AddWithValue("@srcRef", srcRef ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@text", text);
            cmd.Parameters.AddWithValue("@when", whenIso);
            cmd.Parameters.AddWithValue("@sens", sens);
            cmd.ExecuteNonQuery();

            // Also insert into FTS5 index (normally done by trigger, but we're inserting directly)
            using var ftsCmd = conn.CreateCommand();
            ftsCmd.CommandText = """
                INSERT OR REPLACE INTO memory_chunks_fts(rowid, text)
                SELECT rowid, text FROM memory_chunks WHERE chunk_id = @id
                """;
            ftsCmd.Parameters.AddWithValue("@id", id);
            ftsCmd.ExecuteNonQuery();
        }
    }
}
