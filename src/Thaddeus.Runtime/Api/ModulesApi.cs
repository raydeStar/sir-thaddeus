using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Thaddeus.Runtime.Modules;

namespace Thaddeus.Runtime.Api;

public static class ModulesApi
{
    public static IEndpointRouteBuilder MapModulesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/modules", async (ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var list = await modules.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(new ModuleListResponse(list), ModulesJsonContext.Default.ModuleListResponse);
        });

        app.MapGet("/api/modules/{moduleId}", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var detail = await modules.GetDetailAsync(moduleId, ct).ConfigureAwait(false);
            return detail is null
                ? Results.NotFound()
                : Results.Json(detail, ModulesJsonContext.Default.ModuleDetailDto);
        });

        app.MapGet("/api/modules/{moduleId}/permissions", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var permissions = await modules.GetPermissionsAsync(moduleId, ct).ConfigureAwait(false);
            return permissions is null
                ? Results.NotFound()
                : Results.Json(permissions);
        });

        app.MapGet("/api/modules/{moduleId}/tools", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var tools = await modules.ListToolsAsync(moduleId, ct).ConfigureAwait(false);
            return tools is null
                ? Results.NotFound()
                : Results.Json(tools, ModulesJsonContext.Default.IReadOnlyListModuleToolDto);
        });

        app.MapGet("/api/modules/{moduleId}/audit", async (string moduleId, int? limit, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var events = await modules.GetAuditEventsAsync(moduleId, limit ?? 20, ct).ConfigureAwait(false);
            return events is null
                ? Results.NotFound()
                : Results.Json(events, ModulesJsonContext.Default.IReadOnlyListModuleAuditEventDto);
        });

        app.MapPost("/api/modules/{moduleId}/approve", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
            await DetailMutation(moduleId, modules.ApproveAsync, ct).ConfigureAwait(false));

        app.MapPost("/api/modules/{moduleId}/deny", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
            await DetailMutation(moduleId, modules.DenyAsync, ct).ConfigureAwait(false));

        app.MapPost("/api/modules/{moduleId}/disable", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
            await DetailMutation(moduleId, modules.DisableAsync, ct).ConfigureAwait(false));

        app.MapPost("/api/modules/{moduleId}/enable", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
            await DetailMutation(moduleId, modules.EnableAsync, ct).ConfigureAwait(false));

        app.MapPost("/api/modules/{moduleId}/status", async (string moduleId, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            var status = await modules.CheckStatusAsync(moduleId, ct).ConfigureAwait(false);
            return status is null
                ? Results.NotFound()
                : Results.Json(status, ModulesJsonContext.Default.ModuleStatusResponse);
        });

        app.MapPost("/api/modules/{moduleId}/tools/{toolName}/invoke",
            async (string moduleId, string toolName, HttpContext ctx, ModuleRuntimeService modules, CancellationToken ct) =>
        {
            ModuleInvokeRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    req = await JsonSerializer
                        .DeserializeAsync(ctx.Request.Body, ModulesJsonContext.Default.ModuleInvokeRequest, ct)
                        .ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "invalid_json" });
                }
            }

            try
            {
                var result = await modules.InvokeToolAsync(moduleId, toolName, req?.Arguments, ct).ConfigureAwait(false);
                return Results.Json(result, ModulesJsonContext.Default.ModuleInvokeResponse);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ModuleRuntimeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }

    private static async Task<IResult> DetailMutation(
        string moduleId,
        Func<string, CancellationToken, Task<ModuleDetailDto?>> action,
        CancellationToken ct)
    {
        var detail = await action(moduleId, ct).ConfigureAwait(false);
        return detail is null
            ? Results.NotFound()
            : Results.Json(detail, ModulesJsonContext.Default.ModuleDetailDto);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModuleListResponse))]
[JsonSerializable(typeof(ModuleSummaryDto))]
[JsonSerializable(typeof(ModuleDetailDto))]
[JsonSerializable(typeof(ModuleToolDto))]
[JsonSerializable(typeof(ModuleAuditEventDto))]
[JsonSerializable(typeof(ModuleInvokeRequest))]
[JsonSerializable(typeof(ModuleInvokeResponse))]
[JsonSerializable(typeof(ModuleStatusResponse))]
[JsonSerializable(typeof(IReadOnlyList<ModuleToolDto>))]
[JsonSerializable(typeof(IReadOnlyList<ModuleAuditEventDto>))]
public partial class ModulesJsonContext : JsonSerializerContext
{
}
