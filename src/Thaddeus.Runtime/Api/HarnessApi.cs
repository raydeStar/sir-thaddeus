using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.Contracts;
using SirThaddeus.RuntimeHost.Harness;
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
/// (JSON-backed memos), so the v2 version of <c>ClearMemoryData</c> is
/// currently a no-op — chat history reset and env-var swaps are the
/// portable pieces. We can plug in a memo-store wipe later if the harness
/// starts targeting v2 in earnest.
/// </summary>
public static class HarnessApi
{
    public static IEndpointRouteBuilder MapHarnessApi(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/harness/reset", (
            HarnessResetRequest request,
            ToolPermissionGate gate) =>
        {
            if (!HarnessControlPlane.IsHarnessReuseEnabled())
                return Results.NotFound();

            var allowedToolsApplied = HarnessControlPlane.ApplyAllowedToolsOverride(request.AllowedTools);
            var (cleared, set) = HarnessControlPlane.ApplyStubOverrides(request.StubOverrides);

            gate.ClearSessionGrants();

            if (request.ClearChatHistory)
                HarnessControlPlane.ResetHistoryFiles();

            // ClearMemoryData is intentionally not wired here yet — see the
            // class summary. The harness still receives a 200 with rows=0
            // so it can drive v1 and v2 with the identical request payload.

            return Results.Json(
                new HarnessResetResponse(
                    Ok: true,
                    MemoryRowsDeleted: 0,
                    StubVarsCleared: cleared,
                    StubVarsSet: set,
                    AllowedToolsApplied: allowedToolsApplied),
                HarnessJsonContext.Default.HarnessResetResponse);
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
public partial class HarnessJsonContext : JsonSerializerContext
{
}
