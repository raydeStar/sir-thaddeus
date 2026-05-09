using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    private static readonly string[] HarnessMemoryTables =
    [
        "memory_chunks",
        "memory_facts",
        "memory_events",
        "memory_nuggets",
        "profile_cards"
    ];

    /// <summary>
    /// Endpoint used by the harness to swap per-test state (allowed_tools,
    /// stub overrides, memory tables, chat history) without restarting the
    /// runtime process. Only intended for in-process test runs — the harness
    /// is the sole caller. No-op outside ST_HARNESS_RUN_ACTIVE=true to keep
    /// production hosts from accidentally exposing it.
    /// </summary>
    private static void MapHarnessEndpoints(
        WebApplication app,
        Func<AppSettings> getSettings,
        ApiPermissionGate? permissionGate)
    {
        app.MapPost("/api/harness/reset", (HarnessResetRequest request) =>
        {
            if (!IsHarnessReuseEnabled())
            {
                return Results.NotFound();
            }

            var allowedToolsApplied = ApplyAllowedToolsOverride(request.AllowedTools);
            var (cleared, set) = ApplyStubOverrides(request.StubOverrides);

            permissionGate?.ClearSessionGrants();

            int memoryRows = 0;
            if (request.ClearMemoryData)
                memoryRows = TruncateHarnessMemoryTables(getSettings().Memory.DbPath);

            if (request.ClearChatHistory)
                ResetHarnessHistoryFiles();

            return Results.Json(
                new HarnessResetResponse(
                    Ok: true,
                    MemoryRowsDeleted: memoryRows,
                    StubVarsCleared: cleared,
                    StubVarsSet: set,
                    AllowedToolsApplied: allowedToolsApplied),
                JsonOptions);
        });
    }

    private static bool IsHarnessReuseEnabled()
        => string.Equals(
            Environment.GetEnvironmentVariable("ST_HARNESS_RUN_ACTIVE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static string? ApplyAllowedToolsOverride(string? allowedTools)
    {
        if (allowedTools is null)
        {
            // Caller chose not to mutate; leave whatever was set.
            return Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        }

        if (allowedTools.Length == 0)
        {
            Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", null);
            return null;
        }

        Environment.SetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS", allowedTools);
        return allowedTools;
    }

    private static (int Cleared, int Set) ApplyStubOverrides(IReadOnlyDictionary<string, string?>? overrides)
    {
        // Always clear ALL existing ST_STUB_* vars so a previous test's stubs
        // never bleed into the next one. Then set the requested ones.
        var cleared = 0;
        foreach (var key in EnvironmentStubKeys())
        {
            Environment.SetEnvironmentVariable(key, null);
            cleared++;
        }

        if (overrides is null || overrides.Count == 0)
            return (cleared, 0);

        var set = 0;
        foreach (var (toolName, value) in overrides)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            var key = $"ST_STUB_{toolName.Trim().ToUpperInvariant().Replace("-", "_")}";
            if (string.IsNullOrWhiteSpace(value))
            {
                Environment.SetEnvironmentVariable(key, null);
                continue;
            }

            Environment.SetEnvironmentVariable(key, value);
            set++;
        }

        return (cleared, set);
    }

    private static IEnumerable<string> EnvironmentStubKeys()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("ST_STUB_", StringComparison.OrdinalIgnoreCase))
                yield return key;
        }
    }

    private static int TruncateHarnessMemoryTables(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            return 0;

        var totalDeleted = 0;
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            foreach (var table in HarnessMemoryTables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"DELETE FROM {table};";
                try
                {
                    totalDeleted += command.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // Some seed databases may predate a table; ignore.
                }
            }
        }
        catch (SqliteException)
        {
            // Database file may be locked by an active reader; best-effort only.
        }

        return totalDeleted;
    }

    private static void ResetHarnessHistoryFiles()
    {
        TryWriteJsonArray(Environment.GetEnvironmentVariable("ST_CHAT_HISTORY_PATH"));
        TryWriteJsonArray(Environment.GetEnvironmentVariable("ST_BRIEFING_HISTORY_PATH"));
    }

    private static void TryWriteJsonArray(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, "[]");
        }
        catch
        {
            // Best-effort: history file may be held open elsewhere.
        }
    }
}
