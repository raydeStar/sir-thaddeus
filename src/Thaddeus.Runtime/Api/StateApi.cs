using System.Text.Json.Serialization;
using Thaddeus.Runtime.Events;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Wires the loopback HTTP API endpoints. Phase 1 only exposes the bare minimum needed
/// for the shell and workspace to come online: state snapshot, health, version,
/// workspace deep-link dispatcher, and a debug-only state-trigger endpoint behind a
/// guard so Playwright can drive transitions without simulating real input.
/// </summary>
public static class StateApi
{
    /// <summary>Registers <c>/api/state</c>, <c>/api/health</c>, and adjacent endpoints on the application.</summary>
    public static void MapRuntimeApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/state", (StateSnapshot snapshot) => Results.Json(snapshot.Get(), StateSnapshotJsonContext.Default.RuntimeStateEvent))
            .WithName("GetRuntimeState");

        app.MapGet("/api/health", (Hosting.RuntimeOptions opts) => Results.Json(new
        {
            status = "ok",
            version = opts.Version,
            pid = opts.Pid,
            startedAt = opts.StartedAt,
        }))
            .WithName("GetHealth");

        // Phase 2.6 stop-all panic button. Cancels any in-flight STT/TTS and drives
        // the state machine through Stopping → Idle. The shell hits this endpoint
        // when the global stop-all shortcut fires; the React workspace can also
        // call it from the Stop control in the input bar.
        app.MapPost("/api/stop-all", (VoiceModeController voice, RuntimeStateMachine machine) =>
        {
            voice.StopAll();
            return Results.Ok(new { applied = true, current = machine.Current.ToString() });
        })
            .WithName("StopAll");

        // Phase 1 debug endpoint — exposed in test mode only. Lets Playwright force
        // the state machine through transitions without hooking real STT/LLM.
        app.MapPost("/api/_debug/state", async (TriggerRequest req, RuntimeStateMachine machine, Hosting.RuntimeOptions opts) =>
        {
            if (!opts.TestMode) return Results.NotFound();
            if (req is null || string.IsNullOrEmpty(req.Trigger)) return Results.BadRequest();
            if (!Enum.TryParse<StateTrigger>(req.Trigger, ignoreCase: true, out var parsed))
                return Results.BadRequest(new { error = $"Unknown trigger '{req.Trigger}'." });

            var ok = machine.TryTransition(parsed, voiceMode: req.VoiceMode);
            await Task.Yield();
            return Results.Ok(new { applied = ok, current = machine.Current.ToString() });
        })
            .WithName("DebugTriggerState");
    }

    /// <summary>Body for the debug state-trigger endpoint.</summary>
    /// <param name="Trigger">Trigger name (case-insensitive, matches <see cref="StateTrigger"/>).</param>
    /// <param name="VoiceMode">Whether to evaluate transitions in voice mode.</param>
    public sealed record TriggerRequest(string Trigger, bool VoiceMode = false);
}

/// <summary>Source-generated JSON context for <see cref="RuntimeStateEvent"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RuntimeStateEvent))]
public partial class StateSnapshotJsonContext : JsonSerializerContext
{
}
