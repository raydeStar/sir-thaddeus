using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Thaddeus.Runtime.Chat;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Endpoints for reading per-turn trace files written by
/// <see cref="TurnTraceWriter"/>. The traces are the canonical answer to
/// "why did the assistant produce that response?" — they capture the
/// footman decision, every tool call, search-provider diagnostics, and the
/// final assembled text, all keyed by the assistant message id.
///
/// <para>Used by the Settings → Logs UI for human inspection and by future
/// E2E suites that need to assert on the routing/tooling shape of a turn
/// without scraping WebSocket frames.</para>
/// </summary>
public static class TurnsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapTurnsApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/turns", (int? limit, TurnTraceWriter writer) =>
        {
            var capped = limit is null or < 1 ? 50 : Math.Min(limit.Value, 500);
            var entries = ListRecent(writer.Root, capped);
            return Results.Json(new TurnTraceListResponse(entries), JsonOptions);
        }).WithName("ListTurnTraces");

        app.MapGet("/api/turns/{messageId}/trace", (string messageId, TurnTraceWriter writer) =>
        {
            if (!IsSafeFileName(messageId))
                return Results.BadRequest(new { error = "invalid messageId" });

            var path = Path.Combine(writer.Root, messageId + ".jsonl");
            if (!File.Exists(path))
                return Results.NotFound(new { error = "trace not found", messageId });

            var events = ReadTrace(path);
            return Results.Json(new TurnTraceResponse(messageId, events), JsonOptions);
        }).WithName("GetTurnTrace");
    }

    private static IReadOnlyList<TurnTraceSummary> ListRecent(string root, int limit)
    {
        if (!Directory.Exists(root)) return Array.Empty<TurnTraceSummary>();

        // Sort newest first by last-write time so the Settings UI surfaces
        // the turns most likely to be the subject of a fresh complaint.
        var files = new DirectoryInfo(root)
            .EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(limit);

        var summaries = new List<TurnTraceSummary>();
        foreach (var file in files)
        {
            var messageId = Path.GetFileNameWithoutExtension(file.Name);
            // Read just enough of the file to surface a one-line preview
            // for the list view. Avoids loading every trace into memory.
            var (threadId, lastEventType, eventCount) = PeekTrace(file.FullName);
            summaries.Add(new TurnTraceSummary(
                MessageId: messageId,
                ThreadId: threadId,
                ModifiedAt: file.LastWriteTimeUtc,
                SizeBytes: file.Length,
                EventCount: eventCount,
                LastEventType: lastEventType));
        }
        return summaries;
    }

    private static (string? threadId, string? lastEventType, int count) PeekTrace(string path)
    {
        try
        {
            string? threadId = null;
            string? lastEventType = null;
            var count = 0;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                count++;
                if (threadId is null || lastEventType is null)
                {
                    var node = JsonNode.Parse(line);
                    lastEventType = node?["type"]?.GetValue<string>() ?? lastEventType;
                    if (threadId is null)
                    {
                        var payloadThreadId = node?["payload"]?["threadId"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(payloadThreadId)) threadId = payloadThreadId;
                    }
                }
                else
                {
                    // Track the *last* event type so the list shows what the
                    // turn ended on (Complete vs. an in-progress ToolStarted).
                    var node = JsonNode.Parse(line);
                    lastEventType = node?["type"]?.GetValue<string>() ?? lastEventType;
                }
            }
            return (threadId, lastEventType, count);
        }
        catch
        {
            return (null, null, 0);
        }
    }

    private static IReadOnlyList<JsonNode?> ReadTrace(string path)
    {
        var events = new List<JsonNode?>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    events.Add(JsonNode.Parse(line));
                }
                catch (JsonException)
                {
                    // Skip malformed lines rather than fail the whole trace
                    // read — a half-written line during a crash should not
                    // hide the rest of the turn.
                }
            }
        }
        catch
        {
            // If the file vanished or became unreadable between the existence
            // check and the read, return what we have.
        }
        return events;
    }

    private static bool IsSafeFileName(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 128) return false;
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                return false;
        }
        return true;
    }
}

public sealed record TurnTraceSummary(
    string MessageId,
    string? ThreadId,
    DateTimeOffset ModifiedAt,
    long SizeBytes,
    int EventCount,
    string? LastEventType);

public sealed record TurnTraceListResponse(IReadOnlyList<TurnTraceSummary> Turns);

public sealed record TurnTraceResponse(string MessageId, IReadOnlyList<JsonNode?> Events);
