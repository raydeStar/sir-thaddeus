using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    private static void MapRunEndpoints(
        WebApplication app,
        ConcurrentDictionary<string, RunState> runs,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        ApiPermissionGate? permissionGate,
        Action<AppSettings> persistSettings)
    {
        app.MapPost("/api/session/clear", () =>
        {
            permissionGate?.ClearSessionGrants();
            return Results.Ok();
        });

        app.MapPost("/api/chat", (ChatRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.BadRequest("Prompt is required.");
            }

            var runId = $"run_{Guid.NewGuid():N}"[..16];
            var state = new RunState(runId);
            runs[runId] = state;

            _ = Task.Run(async () =>
            {
                using var runContext = RunExecutionContext.Enter(runId);
                try
                {
                    var orchestrator = buildOrchestrator(getSettings());

                    if (request.Messages is { Count: > 0 })
                    {
                        orchestrator.SeedHistory(
                            request.Messages.Select(m => (m.Role, m.Content)));
                    }

                    var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
                        ? request.SessionId
                        : request.ConversationId;
                    var response = await orchestrator.ProcessAsync(
                        request.Prompt,
                        conversationId,
                        state.CancellationToken);
                    state.Append(RuntimeEventTypes.TokenDelta, new TokenDeltaPayload(response.Text, 0));
                    state.Append(RuntimeEventTypes.RunCompleted, new RunCompletedPayload(response.Text, 0, ToBriefingDto(response.DeepDiveBriefing)));
                }
                catch (OperationCanceledException)
                {
                    state.Append(RuntimeEventTypes.RunFailed, new RunFailedPayload("Cancelled", true));
                }
                catch (Exception ex)
                {
                    state.Append(RuntimeEventTypes.RunFailed, new RunFailedPayload(ex.Message, false));
                }
                finally
                {
                    state.Complete();
                }
            }, CancellationToken.None);

            return Results.Json(new ChatStartResponse(runId, DateTimeOffset.UtcNow), JsonOptions);
        });

        app.MapPost("/api/runs/{runId}/cancel", (string runId) =>
        {
            if (!runs.TryGetValue(runId, out var state))
            {
                return Results.NotFound();
            }

            state.Cancel();
            return Results.Json(new CancelRunResponse(runId, true), JsonOptions);
        });

        app.MapPost("/api/permissions/{requestId}/decision", (string requestId, PermissionDecisionRequest request) =>
        {
            if (permissionGate is null)
            {
                return Results.NotFound();
            }

            var applied = permissionGate.TryApplyDecision(requestId, request.Approved, request.RememberForSession, request.PersistAsAlways);

            if (applied && request.Approved && request.PersistAsAlways)
            {
                var toolGroup = permissionGate.GetLastResolvedGroup(requestId);
                if (toolGroup is not null)
                {
                    var currentSettings = getSettings();
                    var perms = currentSettings.Mcp.Permissions;
                    var updatedPerms = toolGroup switch
                    {
                        "screen" => perms with { Screen = "always" },
                        "files" => perms with { Files = "always" },
                        "system" => perms with { System = "always" },
                        "web" => perms with { Web = "always" },
                        "memoryRead" => perms with { MemoryRead = "always" },
                        "memoryWrite" => perms with { MemoryWrite = "always" },
                        _ => perms
                    };
                    if (!ReferenceEquals(perms, updatedPerms))
                    {
                        var updatedSettings = currentSettings with
                        {
                            Mcp = currentSettings.Mcp with { Permissions = updatedPerms }
                        };
                        persistSettings(updatedSettings);
                    }
                }
            }

            return Results.Json(new PermissionDecisionResponse(requestId, applied), JsonOptions);
        });

        app.MapGet("/api/runs/{runId}/events", async (string runId, HttpContext context, CancellationToken ct) =>
        {
            if (!runs.TryGetValue(runId, out var state))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.ContentType = "text/event-stream";

            await foreach (var evt in state.StreamEventsAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        });
    }
}
