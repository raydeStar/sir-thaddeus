using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.AuditLog;
using Thaddeus.Runtime.Routines;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// REST endpoints for Routines — the local-first accountability feature that
/// replaced the Automations section. The surface is intentionally small:
/// <list type="bullet">
///   <item>CRUD for routine definitions.</item>
///   <item>Lifecycle endpoints for a single run (start / patch / complete / discard).</item>
///   <item>History list per routine.</item>
/// </list>
/// No scheduling, no background execution, no outbound network calls. Every
/// mutation is initiated by the UI in response to a direct user action.
/// </summary>
public static class RoutinesApi
{
    public static IEndpointRouteBuilder MapRoutinesApi(this IEndpointRouteBuilder app)
    {
        // ── Routines ──────────────────────────────────────────────────────

        app.MapGet("/api/routines", async (IRoutineStore store, CancellationToken ct) =>
        {
            var items = await store.ListRoutinesAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new RoutineListResponse(items.ToArray()),
                RoutinesJsonContext.Default.RoutineListResponse);
        });

        app.MapGet("/api/routines/{id}", async (string id, IRoutineStore store, CancellationToken ct) =>
        {
            var item = await store.GetRoutineAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Json(item, RoutinesJsonContext.Default.Routine);
        });

        app.MapPost("/api/routines", async (HttpContext ctx, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, RoutinesJsonContext.Default.CreateRoutineRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var items = (req.ChecklistItems ?? Array.Empty<RoutineChecklistItemInput>())
                .Select(MapItemInput)
                .ToArray();

            var created = await store.CreateRoutineAsync(
                req.Name ?? string.Empty,
                req.Description ?? string.Empty,
                items,
                req.PromptTemplate,
                req.Enabled ?? true,
                ct).ConfigureAwait(false);

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_UPDATED",
                Target = created.Id,
                Details = new() { ["source"] = "create" },
            });

            return Results.Json(created, RoutinesJsonContext.Default.Routine, statusCode: StatusCodes.Status201Created);
        });

        app.MapPatch("/api/routines/{id}", async (string id, HttpContext ctx, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, RoutinesJsonContext.Default.UpdateRoutineRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            IReadOnlyList<RoutineChecklistItem>? items = req.ChecklistItems is null
                ? null
                : req.ChecklistItems.Select(MapItemInput).ToArray();

            var updated = await store.UpdateRoutineAsync(
                id,
                req.Name,
                req.Description,
                items,
                req.PromptTemplate,
                req.Enabled,
                ct).ConfigureAwait(false);

            if (updated is null) return Results.NotFound();

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_UPDATED",
                Target = id,
                Details = new() { ["source"] = "update" },
            });

            return Results.Json(updated, RoutinesJsonContext.Default.Routine);
        });

        app.MapDelete("/api/routines/{id}", async (string id, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var ok = await store.DeleteRoutineAsync(id, ct).ConfigureAwait(false);
            if (!ok) return Results.NotFound();

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_DELETED",
                Target = id,
            });
            return Results.NoContent();
        });

        // ── Runs ──────────────────────────────────────────────────────────

        app.MapPost("/api/routines/{id}/runs", async (string id, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var run = await store.StartRunAsync(id, ct).ConfigureAwait(false);
            if (run is null) return Results.NotFound();

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_STARTED",
                Target = id,
                Details = new() { ["runId"] = run.Id },
            });

            return Results.Json(run, RoutinesJsonContext.Default.RoutineRun, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/routines/{id}/runs", async (string id, IRoutineStore store, CancellationToken ct) =>
        {
            var routine = await store.GetRoutineAsync(id, ct).ConfigureAwait(false);
            if (routine is null) return Results.NotFound();
            var runs = await store.ListRunsAsync(id, ct).ConfigureAwait(false);
            return Results.Json(
                new RoutineRunListResponse(runs.ToArray()),
                RoutinesJsonContext.Default.RoutineRunListResponse);
        });

        app.MapGet("/api/routine-runs/{runId}", async (string runId, IRoutineStore store, CancellationToken ct) =>
        {
            var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            return run is null ? Results.NotFound() : Results.Json(run, RoutinesJsonContext.Default.RoutineRun);
        });

        app.MapPatch("/api/routine-runs/{runId}", async (string runId, HttpContext ctx, IRoutineStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, RoutinesJsonContext.Default.UpdateRoutineRunRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            IReadOnlyDictionary<string, bool>? updates = null;
            if (req.ItemUpdates is { Count: > 0 })
            {
                var dict = new Dictionary<string, bool>(req.ItemUpdates.Count, StringComparer.Ordinal);
                foreach (var u in req.ItemUpdates)
                {
                    if (string.IsNullOrWhiteSpace(u.ChecklistItemId)) continue;
                    dict[u.ChecklistItemId] = u.IsCompleted;
                }
                updates = dict;
            }

            var updated = await store.UpdateRunAsync(runId, updates, req.UserNote, ct).ConfigureAwait(false);
            return updated is null ? Results.NotFound() : Results.Json(updated, RoutinesJsonContext.Default.RoutineRun);
        });

        app.MapPost("/api/routine-runs/{runId}/complete", async (string runId, HttpContext ctx, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            // Body is optional — allow a bare POST with no JSON to complete
            // without changing the note. Read only if Content-Length hints at
            // actual content.
            CompleteRoutineRunRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                req = await ReadAsync(ctx, RoutinesJsonContext.Default.CompleteRoutineRunRequest, ct);
            }

            var updated = await store.CompleteRunAsync(runId, req?.UserNote, ct).ConfigureAwait(false);
            if (updated is null) return Results.NotFound();

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_COMPLETED",
                Target = updated.RoutineId,
                Details = new()
                {
                    ["runId"] = runId,
                    ["completedItems"] = updated.Items.Count(i => i.IsCompleted),
                    ["totalItems"] = updated.Items.Count,
                },
            });

            return Results.Json(updated, RoutinesJsonContext.Default.RoutineRun);
        });

        app.MapDelete("/api/routine-runs/{runId}", async (string runId, IRoutineStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var run = await store.GetRunAsync(runId, ct).ConfigureAwait(false);
            var ok = await store.DiscardRunAsync(runId, ct).ConfigureAwait(false);
            if (!ok) return Results.NotFound();

            audit.Append(new AuditEvent
            {
                Actor = "user",
                Action = "ROUTINE_CANCELLED",
                Target = run?.RoutineId,
                Details = new() { ["runId"] = runId },
            });

            return Results.NoContent();
        });

        return app;
    }

    private static RoutineChecklistItem MapItemInput(RoutineChecklistItemInput input)
    {
        return new RoutineChecklistItem(
            Id: input.Id ?? string.Empty,
            Text: input.Text ?? string.Empty,
            SortOrder: input.SortOrder ?? 0);
    }

    private static async Task<T?> ReadAsync<T>(HttpContext ctx, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ct).ConfigureAwait(false);
        }
        catch (JsonException) { return null; }
    }
}

public sealed record RoutineChecklistItemInput(string? Id, string? Text, int? SortOrder);

public sealed record CreateRoutineRequest(
    string? Name,
    string? Description,
    IReadOnlyList<RoutineChecklistItemInput>? ChecklistItems,
    string? PromptTemplate,
    bool? Enabled);

public sealed record UpdateRoutineRequest(
    string? Name,
    string? Description,
    IReadOnlyList<RoutineChecklistItemInput>? ChecklistItems,
    string? PromptTemplate,
    bool? Enabled);

public sealed record RoutineRunItemUpdateInput(string ChecklistItemId, bool IsCompleted);

public sealed record UpdateRoutineRunRequest(
    IReadOnlyList<RoutineRunItemUpdateInput>? ItemUpdates,
    string? UserNote);

public sealed record CompleteRoutineRunRequest(string? UserNote);

public sealed record RoutineListResponse(IReadOnlyList<Routine> Routines);
public sealed record RoutineRunListResponse(IReadOnlyList<RoutineRun> Runs);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Routine))]
[JsonSerializable(typeof(RoutineRun))]
[JsonSerializable(typeof(RoutineListResponse))]
[JsonSerializable(typeof(RoutineRunListResponse))]
[JsonSerializable(typeof(CreateRoutineRequest))]
[JsonSerializable(typeof(UpdateRoutineRequest))]
[JsonSerializable(typeof(UpdateRoutineRunRequest))]
[JsonSerializable(typeof(CompleteRoutineRunRequest))]
[JsonSerializable(typeof(RoutineChecklistItemInput))]
[JsonSerializable(typeof(RoutineRunItemUpdateInput))]
public partial class RoutinesJsonContext : JsonSerializerContext
{
}
