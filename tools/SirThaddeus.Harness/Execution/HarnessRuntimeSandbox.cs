using Microsoft.Data.Sqlite;
using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Harness.Models;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Harness.Execution;

internal sealed class HarnessRuntimeSandbox : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string RootDirectory { get; }
    public string SettingsPath { get; }
    public string AuditPath { get; }
    public AppSettings Settings { get; }
    public IReadOnlyDictionary<string, string> Environment { get; }

    private HarnessRuntimeSandbox(
        string rootDirectory,
        string settingsPath,
        string auditPath,
        AppSettings settings,
        IReadOnlyDictionary<string, string> environment)
    {
        RootDirectory = rootDirectory;
        SettingsPath = settingsPath;
        AuditPath = auditPath;
        Settings = settings;
        Environment = environment;
    }

    public static HarnessRuntimeSandbox Create(AppSettings baseSettings, HarnessTestCase test)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);
        ArgumentNullException.ThrowIfNull(test);

        var sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "SirThaddeus.Harness",
            $"{SanitizePathSegment(test.Id)}-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(sandboxRoot, "data");
        var knowledgeDirectory = Path.Combine(sandboxRoot, "knowledge-store");
        var profilesDirectory = Path.Combine(sandboxRoot, "profiles");

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(knowledgeDirectory);
        Directory.CreateDirectory(profilesDirectory);

        CopyDirectory(
            SettingsManager.ResolvePersonalityProfilesDirectory(baseSettings),
            profilesDirectory);

        var sandboxSettings = baseSettings with
        {
            Memory = baseSettings.Memory with
            {
                DbPath = Path.Combine(dataDirectory, "memory.db")
            },
            Weather = baseSettings.Weather with
            {
                PlaceMemoryPath = Path.Combine(dataDirectory, "weather-places.json")
            },
            PersonalityProfilesDir = profilesDirectory,
            KnowledgeStore = baseSettings.KnowledgeStore with
            {
                Roots = CreateSandboxRoots(baseSettings.KnowledgeStore.Roots, knowledgeDirectory)
            }
        };

        CopyIfExists(
            RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPath(baseSettings.Memory.DbPath),
            sandboxSettings.Memory.DbPath,
            includeSqliteSidecars: true);
        StripDeepMemoryChunks(sandboxSettings.Memory.DbPath);
        CopyIfExists(
            RuntimeMcpEnvironmentBuilder.ResolveWeatherPlaceMemoryPath(baseSettings.Weather.PlaceMemoryPath),
            sandboxSettings.Weather.PlaceMemoryPath,
            includeSqliteSidecars: false);

        var settingsPath = Path.Combine(sandboxRoot, "settings.json");
        var auditPath = Path.Combine(dataDirectory, "audit.jsonl");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(sandboxSettings, JsonOptions));

        var environment = RuntimeMcpEnvironmentBuilder.Build(sandboxSettings);
        environment["ST_SETTINGS_PATH"] = settingsPath;
        environment["ST_AUDIT_PATH"] = auditPath;
        environment["ST_CHAT_HISTORY_PATH"] = Path.Combine(dataDirectory, "chat-history.json");
        environment["ST_BRIEFING_HISTORY_PATH"] = Path.Combine(dataDirectory, "briefing-history.json");

        return new HarnessRuntimeSandbox(
            sandboxRoot,
            settingsPath,
            auditPath,
            sandboxSettings,
            environment);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootDirectory))
                Directory.Delete(RootDirectory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup. Sandbox directories live under temp.
        }
    }

    private static IReadOnlyList<KnowledgeStoreRootConfig> CreateSandboxRoots(
        IReadOnlyList<KnowledgeStoreRootConfig> roots,
        string knowledgeDirectory)
    {
        if (roots.Count == 0)
            return roots;

        var sandboxedRoots = new List<KnowledgeStoreRootConfig>(roots.Count);
        foreach (var root in roots)
        {
            if (string.Equals(root.AccessLevel, "KnowledgeReadWrite", StringComparison.OrdinalIgnoreCase))
            {
                var isolatedPath = Path.Combine(knowledgeDirectory, SanitizePathSegment(root.Id));
                Directory.CreateDirectory(isolatedPath);
                sandboxedRoots.Add(root with { AbsolutePath = isolatedPath, ConfirmWrites = false });
                continue;
            }

            sandboxedRoots.Add(root with { AbsolutePath = Path.GetFullPath(root.AbsolutePath) });
        }

        return sandboxedRoots;
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "sandbox";

        var invalid = Path.GetInvalidFileNameChars();
        var buffer = value
            .Trim()
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray();
        var sanitized = new string(buffer).Trim('-', '.', ' ');
        return string.IsNullOrWhiteSpace(sanitized) ? "sandbox" : sanitized;
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
            return;

        Directory.CreateDirectory(destinationDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, filePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var destinationFolder = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationFolder))
                Directory.CreateDirectory(destinationFolder);

            File.Copy(filePath, destinationPath, overwrite: true);
        }
    }

    private static void CopyIfExists(
        string sourcePath,
        string destinationPath,
        bool includeSqliteSidecars)
    {
        if (!File.Exists(sourcePath))
            return;

        var destinationFolder = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(destinationFolder))
            Directory.CreateDirectory(destinationFolder);

        File.Copy(sourcePath, destinationPath, overwrite: true);

        if (!includeSqliteSidecars)
            return;

        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecarSource = sourcePath + suffix;
            var sidecarDestination = destinationPath + suffix;
            if (File.Exists(sidecarSource))
                File.Copy(sidecarSource, sidecarDestination, overwrite: true);
        }
    }

    private static void StripDeepMemoryChunks(string dbPath)
    {
        if (!File.Exists(dbPath))
            return;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_chunks;";

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
            // Some seed databases may predate the chunk table. In that case,
            // leave the copied DB untouched rather than failing the harness run.
        }
    }
}