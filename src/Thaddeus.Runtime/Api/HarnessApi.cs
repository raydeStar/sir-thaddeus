using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.Contracts;
using SirThaddeus.LlmClient;
using SirThaddeus.RuntimeHost.Harness;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Memory;
using Thaddeus.Runtime.Tools;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Harness control-plane endpoint for the v2 hybrid runtime. Mirrors the
/// shape of the v1 headless host's <c>/api/harness/reset</c> so the same
/// CLI harness can drive either runtime.
///
/// The endpoint returns 404 unless <c>ST_HARNESS_RUN_ACTIVE=true</c>, so
/// production hosts cannot accidentally expose it.
///
/// Memory-store shape differs between v1 (sqlite tables) and v2
/// (JSON-backed memos). When <c>ClearMemoryData</c> is set, this
/// endpoint calls <see cref="IMemoStore.WipeAllAsync"/> so the next
/// test starts with no memos.
/// </summary>
public static class HarnessApi
{
    public static IEndpointRouteBuilder MapHarnessApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/harness/reset", async (
            HarnessResetRequest request,
            ToolPermissionGate gate,
            IMemoStore memos,
            CancellationToken ct) =>
        {
            if (!HarnessControlPlane.IsHarnessReuseEnabled())
                return Results.NotFound();

            var allowedToolsApplied = HarnessControlPlane.ApplyAllowedToolsOverride(request.AllowedTools);
            var (cleared, set) = HarnessControlPlane.ApplyStubOverrides(request.StubOverrides);

            gate.ClearSessionGrants();

            if (request.ClearChatHistory)
                HarnessControlPlane.ResetHistoryFiles();

            var memoryRows = 0;
            if (request.ClearMemoryData)
                memoryRows = await memos.WipeAllAsync(ct).ConfigureAwait(false);

            return Results.Json(
                new HarnessResetResponse(
                    Ok: true,
                    MemoryRowsDeleted: memoryRows,
                    StubVarsCleared: cleared,
                    StubVarsSet: set,
                    AllowedToolsApplied: allowedToolsApplied),
                HarnessJsonContext.Default.HarnessResetResponse);
        });

        app.MapGet("/api/harness/llm-usage", (LlmRuntimeRegistry registry) =>
        {
            if (!HarnessControlPlane.IsHarnessReuseEnabled())
                return Results.NotFound();

            return Results.Json(
                registry.GetUsageSnapshot(),
                HarnessJsonContext.Default.LlmUsageSnapshot);
        });

        return app;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HarnessResetRequest))]
[JsonSerializable(typeof(HarnessResetResponse))]
[JsonSerializable(typeof(LlmUsageSnapshot))]
public partial class HarnessJsonContext : JsonSerializerContext
{
}
