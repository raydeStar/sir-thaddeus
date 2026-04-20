using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Automations;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>REST endpoints for user-defined automations (Phase 7.2).</summary>
public static class AutomationsApi
{
    public static IEndpointRouteBuilder MapAutomationsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/automations", async (IAutomationStore store, CancellationToken ct) =>
        {
            var items = await store.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new AutomationListResponse(items.ToArray()),
                AutomationsJsonContext.Default.AutomationListResponse);
        });

        app.MapPost("/api/automations", async (HttpContext ctx, IAutomationStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<CreateAutomationRequest>(ctx, AutomationsJsonContext.Default.CreateAutomationRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var item = await store.CreateAsync(
                req.Name ?? string.Empty,
                req.Description ?? string.Empty,
                req.Steps ?? Array.Empty<string>(),
                req.Enabled ?? true,
                ct).ConfigureAwait(false);
            return Results.Json(item, AutomationsJsonContext.Default.Automation, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/automations/{id}", async (string id, IAutomationStore store, CancellationToken ct) =>
        {
            var item = await store.GetAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Json(item, AutomationsJsonContext.Default.Automation);
        });

        app.MapPatch("/api/automations/{id}", async (string id, HttpContext ctx, IAutomationStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<UpdateAutomationRequest>(ctx, AutomationsJsonContext.Default.UpdateAutomationRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var updated = await store.UpdateAsync(id, req.Name, req.Description, req.Steps, req.Enabled, ct)
                .ConfigureAwait(false);
            return updated is null
                ? Results.NotFound()
                : Results.Json(updated, AutomationsJsonContext.Default.Automation);
        });

        app.MapDelete("/api/automations/{id}", async (string id, IAutomationStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Manual trigger. For v1 this records the run in the activity log
        // and stamps LastRunAt. Actually executing the steps against the LLM
        // is wired up later when the live model client lands.
        app.MapPost("/api/automations/{id}/run", async (string id, IAutomationStore store, IActivityLog activity, CancellationToken ct) =>
        {
            var item = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (item is null) return Results.NotFound();
            if (!item.Enabled) return Results.BadRequest(new { error = "disabled" });

            var now = DateTimeOffset.UtcNow;
            var entry = new ActivityEntry(
                Id: InMemoryActivityLog.NewId(),
                Kind: ActivityKind.Automation,
                Summary: item.Name,
                Status: ActivityStatus.Ok,
                StartedAt: now,
                CompletedAt: now,
                ThreadId: null,
                Detail: $"Manual trigger ({item.Steps.Count} step{(item.Steps.Count == 1 ? "" : "s")})");
            activity.Append(entry);

            var updated = await store.RecordRunAsync(id, ct).ConfigureAwait(false);
            return Results.Json(updated, AutomationsJsonContext.Default.Automation);
        });

        return app;
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

public sealed record CreateAutomationRequest(string? Name, string? Description, IReadOnlyList<string>? Steps, bool? Enabled);
public sealed record UpdateAutomationRequest(string? Name, string? Description, IReadOnlyList<string>? Steps, bool? Enabled);
public sealed record AutomationListResponse(IReadOnlyList<Automation> Automations);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Automation))]
[JsonSerializable(typeof(AutomationListResponse))]
[JsonSerializable(typeof(CreateAutomationRequest))]
[JsonSerializable(typeof(UpdateAutomationRequest))]
public partial class AutomationsJsonContext : JsonSerializerContext
{
}
