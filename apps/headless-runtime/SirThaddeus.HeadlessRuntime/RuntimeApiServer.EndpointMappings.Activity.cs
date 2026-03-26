using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    private static void MapActivityEndpoints(
        WebApplication app,
        IAuditLogger audit,
        Func<AppSettings> getSettings,
        Action<AppSettings> persistSettings,
        ApiPermissionGate? permissionGate)
    {
        var aggregator = new ActivitySummaryAggregator(audit, getSettings, permissionGate);

        app.MapGet("/api/activity/summary", async (string? sessionId, CancellationToken ct) =>
        {
            var summary = await aggregator.BuildSummaryAsync(sessionId, ct);
            return Results.Json(summary, JsonOptions);
        });

        app.MapPut("/api/activity/connections/{connectionId}/approval",
            async (string connectionId, ConnectionApprovalChangeRequest request, CancellationToken ct) =>
        {
            var settings = getSettings();
            var perms = settings.Mcp.Permissions;

            var newPolicy = request.NewApprovalState switch
            {
                ConnectionApprovalStates.AlwaysAllow  => "always",
                ConnectionApprovalStates.PerRequest   => "ask",
                ConnectionApprovalStates.Disabled      => "off",
                ConnectionApprovalStates.Revoked       => "off",
                _ => "ask"
            };

            var updatedPerms = connectionId.ToLowerInvariant() switch
            {
                "screen"      => perms with { Screen = newPolicy },
                "files"       => perms with { Files = newPolicy },
                "system"      => perms with { System = newPolicy },
                "web"         => perms with { Web = newPolicy },
                "memoryread"  => perms with { MemoryRead = newPolicy },
                "memorywrite" => perms with { MemoryWrite = newPolicy },
                _ => null
            };

            if (updatedPerms is null)
            {
                return Results.Json(
                    new ConnectionApprovalChangeResponse(connectionId, request.NewApprovalState, Applied: false),
                    JsonOptions,
                    statusCode: 404);
            }

            var updatedSettings = settings with
            {
                Mcp = settings.Mcp with { Permissions = updatedPerms }
            };

            persistSettings(updatedSettings);

            // Clear session grants when revoking so the change takes effect immediately
            if (newPolicy == "off")
            {
                permissionGate?.ClearSessionGrants();
            }

            return Results.Json(
                new ConnectionApprovalChangeResponse(connectionId, request.NewApprovalState, Applied: true),
                JsonOptions);
        });
    }
}
