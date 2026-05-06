using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.Hosting;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Phase 5 endpoints. Reads from the <see cref="IActivityLog"/> ring buffer and
/// reports basic runtime diagnostics. Mutations to the log come from producers
/// (chat turns, voice turns) — there is no public POST/PATCH surface here.
/// </summary>
public static class ActivityApi
{
    /// <summary>Registers the <c>/api/activity</c> and <c>/api/diagnostics</c> endpoints.</summary>
    public static void MapActivityApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/activity", (int? limit, IActivityLog log) =>
        {
            var capped = limit is null or < 1 ? 50 : Math.Min(limit.Value, 500);
            var entries = log.List(capped);
            return Results.Json(
                new ActivityListResponse(entries),
                ActivityJsonContext.Default.ActivityListResponse);
        })
            .WithName("ListActivity");

        app.MapGet("/api/activity/{id}", (string id, IActivityLog log) =>
        {
            var entry = log.Get(id);
            return entry is null
                ? Results.NotFound()
                : Results.Json(entry, ActivityJsonContext.Default.ActivityEntry);
        })
            .WithName("GetActivity");

        app.MapGet("/api/diagnostics", async (
            RuntimeOptions options,
            RuntimeStateMachine machine,
            IThreadStore threads,
            VoiceRuntimeStatusService voiceStatus,
            CancellationToken ct) =>
        {
            var startedAt = options.StartedAt;
            var uptime = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;
            var rootDir = threads is JsonFileThreadStore js ? js.RootDirectory : "";
            var thrCount = string.IsNullOrEmpty(rootDir) ? -1 : CountFiles(rootDir);

            var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var voice = await voiceStatus.GetStatusAsync(ensureHost: false, ct).ConfigureAwait(false);

            var resp = new DiagnosticsResponse(
                UptimeSeconds: uptime,
                State: machine.Current.ToString(),
                ThreadCount: thrCount,
                ThreadStoreRoot: rootDir,
                VoiceAvailable: voice.InputAvailable,
                Voice: voice,
                Pid: options.Pid,
                BuildVersion: version);

            return Results.Json(resp, ActivityJsonContext.Default.DiagnosticsResponse);
        })
            .WithName("GetDiagnostics");
    }

    private static int CountFiles(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.json").Count()
                : 0;
        }
        catch
        {
            return -1;
        }
    }
}

/// <summary>Wrapper for GET /api/activity.</summary>
public sealed record ActivityListResponse(IReadOnlyList<ActivityEntry> Entries);

/// <summary>Response for GET /api/diagnostics.</summary>
public sealed record DiagnosticsResponse(
    double UptimeSeconds,
    string State,
    int ThreadCount,
    string ThreadStoreRoot,
    bool VoiceAvailable,
    VoiceRuntimeStatus Voice,
    int Pid,
    string BuildVersion);

/// <summary>Source-generated JSON context for activity payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ActivityEntry))]
[JsonSerializable(typeof(ActivityListResponse))]
[JsonSerializable(typeof(DiagnosticsResponse))]
[JsonSerializable(typeof(VoiceRuntimeStatus))]
public partial class ActivityJsonContext : JsonSerializerContext
{
}
