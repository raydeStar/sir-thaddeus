using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.RuntimeHost.Harness;

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
    /// Wires the v1 headless host's harness reset endpoint. The mutation
    /// logic (env vars, history files) is shared with v2 via
    /// <see cref="HarnessControlPlane"/>; only the sqlite memory truncate
    /// is v1-specific because v2 uses a different memory store shape.
    /// </summary>
    private static void MapHarnessEndpoints(
        WebApplication app,
        Func<AppSettings> getSettings,
        ApiPermissionGate? permissionGate,
        Action? resetToolBudgets)
    {
        app.MapPost("/api/harness/reset", (HarnessResetRequest request) =>
        {
            if (!HarnessControlPlane.IsHarnessReuseEnabled())
                return Results.NotFound();

            var allowedToolsApplied = HarnessControlPlane.ApplyAllowedToolsOverride(request.AllowedTools);
            var (cleared, set) = HarnessControlPlane.ApplyStubOverrides(request.StubOverrides);

            permissionGate?.ClearSessionGrants();
            resetToolBudgets?.Invoke();

            int memoryRows = 0;
            if (request.ClearMemoryData)
                memoryRows = TruncateHarnessMemoryTables(getSettings().Memory.DbPath);

            if (request.ClearChatHistory)
                HarnessControlPlane.ResetHistoryFiles();

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
}
