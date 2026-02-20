using System.IO;
using System.Text.Json;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.AuditLog;
using SirThaddeus.DesktopRuntime.ViewModels;

namespace SirThaddeus.DesktopRuntime.Services;

// ─────────────────────────────────────────────────────────────────────────
// Disk persistence for chat sessions and briefing history.
//
// Two JSON files under %LOCALAPPDATA%\SirThaddeus:
//   chat-history.json    — array of ChatSessionSnapshot
//   briefing-history.json — array of BriefingHistoryEntry
//
// Follows the same resilience pattern as FileDialogueStatePersistence:
// if a file is corrupt or missing we start fresh rather than crash.
// ─────────────────────────────────────────────────────────────────────────

public sealed class ChatHistoryPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented     = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _chatHistoryPath;
    private readonly string _briefingHistoryPath;
    private readonly IAuditLogger _audit;

    public ChatHistoryPersistence(string dataDirectory, IAuditLogger audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new ArgumentException("Data directory must not be empty.", nameof(dataDirectory));

        if (!Directory.Exists(dataDirectory))
            Directory.CreateDirectory(dataDirectory);

        _chatHistoryPath     = Path.Combine(dataDirectory, "chat-history.json");
        _briefingHistoryPath = Path.Combine(dataDirectory, "briefing-history.json");
        _audit = audit;
    }

    // ────────────────────────────────────────────
    //  Chat History
    // ────────────────────────────────────────────

    public IReadOnlyList<ChatSessionSnapshot> LoadChatHistory()
    {
        return LoadJsonList<ChatSessionSnapshot>(_chatHistoryPath, "CHAT_HISTORY");
    }

    public void SaveChatHistory(IEnumerable<ChatSessionSnapshot> sessions)
    {
        SaveJsonList(_chatHistoryPath, sessions, "CHAT_HISTORY");
    }

    // ────────────────────────────────────────────
    //  Briefing History
    // ────────────────────────────────────────────

    public IReadOnlyList<BriefingHistoryEntryDto> LoadBriefingHistory()
    {
        return LoadJsonList<BriefingHistoryEntryDto>(_briefingHistoryPath, "BRIEFING_HISTORY");
    }

    public void SaveBriefingHistory(IEnumerable<BriefingHistoryEntry> entries)
    {
        var dtos = entries.Select(e => new BriefingHistoryEntryDto
        {
            Title      = e.Title,
            Confidence = e.Confidence,
            StatusLine = e.StatusLine,
            Timestamp  = e.Timestamp,
            Briefing   = e.Briefing
        }).ToList();

        SaveJsonList(_briefingHistoryPath, dtos, "BRIEFING_HISTORY");
    }

    // ────────────────────────────────────────────
    //  Generic helpers
    // ────────────────────────────────────────────

    private IReadOnlyList<T> LoadJsonList<T>(string path, string logLabel)
    {
        try
        {
            if (!File.Exists(path))
                return [];

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            return JsonSerializer.Deserialize<List<T>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "runtime",
                Action = $"{logLabel}_LOAD_FAILED",
                Result = "error",
                Details = new Dictionary<string, object> { ["error"] = ex.Message }
            });
            return [];
        }
    }

    private void SaveJsonList<T>(string path, IEnumerable<T> items, string logLabel)
    {
        try
        {
            var json = JsonSerializer.Serialize(items.ToList(), JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor  = "runtime",
                Action = $"{logLabel}_SAVE_FAILED",
                Result = "error",
                Details = new Dictionary<string, object> { ["error"] = ex.Message }
            });
        }
    }
}

/// <summary>
/// Flat DTO for JSON round-tripping BriefingHistoryEntry.
/// The record constructor on BriefingHistoryEntry has required positional params,
/// so this DTO keeps deserialization clean.
/// </summary>
public sealed class BriefingHistoryEntryDto
{
    public string Title { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string StatusLine { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public DeepDiveBriefing? Briefing { get; set; }
}
