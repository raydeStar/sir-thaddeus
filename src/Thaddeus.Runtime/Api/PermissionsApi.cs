using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.AuditLog;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Tools;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// REST endpoints the UI uses to drive the tool-permission modal:
/// <list type="bullet">
///   <item><c>GET /api/permissions/pending</c> — snapshot of outstanding prompts,
///         so the UI can re-sync after a reload.</item>
///   <item><c>POST /api/permissions/respond</c> — submit the user's choice
///         (deny / once / session / always) for a given prompt id, optionally
///         scoped to the single tool ("tool") or the whole group ("group").</item>
///   <item><c>GET /api/permissions/catalog</c> — the static per-tool
///         permission catalog (group policy, per-tool override, effective).</item>
/// </list>
/// The push side lives on the existing <c>/ws</c> channel via the
/// <c>permission.request</c> and <c>permission.resolved</c> event types.
/// </summary>
public static class PermissionsApi
{
    public static IEndpointRouteBuilder MapPermissionsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/permissions/pending", (ToolPermissionGate gate) =>
        {
            return Results.Json(
                new PendingPermissionsResponse(gate.ListPending()),
                PermissionsJsonContext.Default.PendingPermissionsResponse);
        });

        app.MapPost("/api/permissions/respond", async (
            RespondToPermissionRequest? req,
            ToolPermissionGate gate,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Decision))
                return Results.BadRequest(new { error = "id and decision are required" });

            if (!TryParseDecision(req.Decision, out var decision))
                return Results.BadRequest(new { error = $"unknown decision '{req.Decision}'" });

            if (!TryNormalizeScope(req.Scope, out var scope))
                return Results.BadRequest(new { error = $"unknown scope '{req.Scope}'" });

            var pending = gate.ListPending().FirstOrDefault(item =>
                string.Equals(item.Id, req.Id, StringComparison.Ordinal));
            var matched = gate.Respond(req.Id, decision, scope);
            if (matched)
            {
                var latencyMs = pending is null
                    ? -1L
                    : Math.Max(0L, (long)(DateTimeOffset.UtcNow - pending.CreatedAt).TotalMilliseconds);
                await audit.AppendAsync(new AuditEvent
                {
                    Actor = "user",
                    Action = "TOOL_PERMISSION_DECISION",
                    Target = pending?.Tool ?? req.Id,
                    Result = decision == ToolPermissionResponse.Deny ? "denied" : "ok",
                    Details = new Dictionary<string, object>
                    {
                        ["requestId"] = req.Id,
                        ["decision"] = req.Decision.Trim().ToLowerInvariant(),
                        ["scope"] = scope,
                        ["group"] = pending?.Group ?? string.Empty,
                        ["threadId"] = pending?.ThreadId ?? string.Empty,
                        ["turnId"] = pending?.TurnId ?? string.Empty,
                        ["latencyMs"] = latencyMs,
                    },
                }, ct).ConfigureAwait(false);
            }
            return matched
                ? Results.Ok(new { applied = true })
                : Results.NotFound(new { error = "permission request not found (already resolved or cancelled)" });
        });

        app.MapGet("/api/permissions/catalog",
            async (ToolPermissionGate gate, ISettingsStore store, CancellationToken ct) =>
        {
            var doc = await store.GetAsync(ct).ConfigureAwait(false);
            return Results.Json(
                gate.BuildCatalog(doc),
                PermissionsJsonContext.Default.PermissionCatalog);
        });

        return app;
    }

    internal static bool TryNormalizeScope(string? value, out string scope)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "":         scope = "group"; return true; // absent → group (back-compat)
            case "group":    scope = "group"; return true;
            case "tool":     scope = "tool"; return true;
            // Call scope is server-authoritative for one-shot mutations such
            // as Wiki writes. The client echoes it to keep the decision and
            // audit contract explicit; the gate still refuses to cache or
            // persist broader grants for these prompts.
            case "call":     scope = "call"; return true;
            default:         scope = "group"; return false;
        }
    }

    private static bool TryParseDecision(string value, out ToolPermissionResponse decision)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "deny": decision = ToolPermissionResponse.Deny; return true;
            case "once": decision = ToolPermissionResponse.Once; return true;
            case "session": decision = ToolPermissionResponse.Session; return true;
            case "always": decision = ToolPermissionResponse.Always; return true;
            default: decision = default; return false;
        }
    }
}

public sealed record PendingPermissionsResponse(IReadOnlyList<PendingPermission> Requests);

public sealed record RespondToPermissionRequest(string Id, string Decision, string? Scope = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PendingPermissionsResponse))]
[JsonSerializable(typeof(PendingPermission))]
[JsonSerializable(typeof(RespondToPermissionRequest))]
[JsonSerializable(typeof(PermissionCatalog))]
[JsonSerializable(typeof(PermissionCatalogGroup))]
[JsonSerializable(typeof(PermissionCatalogTool))]
public partial class PermissionsJsonContext : JsonSerializerContext
{
}
