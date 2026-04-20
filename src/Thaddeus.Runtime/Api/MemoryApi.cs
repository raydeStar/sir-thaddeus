using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Thaddeus.Runtime.Memory;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>REST endpoints for user-curated memos (Phase 7.1).</summary>
public static class MemoryApi
{
    public static IEndpointRouteBuilder MapMemoryApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/memos", async (IMemoStore store, CancellationToken ct) =>
        {
            var memos = await store.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new MemoListResponse(memos.ToArray()),
                MemoryJsonContext.Default.MemoListResponse);
        });

        app.MapPost("/api/memos", async (HttpContext ctx, IMemoStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<CreateMemoRequest>(ctx, MemoryJsonContext.Default.CreateMemoRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var memo = await store.CreateAsync(
                req.Title ?? string.Empty,
                req.Body ?? string.Empty,
                req.Tags,
                req.Pinned ?? false,
                ct).ConfigureAwait(false);
            return Results.Json(memo, MemoryJsonContext.Default.Memo, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/memos/{id}", async (string id, IMemoStore store, CancellationToken ct) =>
        {
            var memo = await store.GetAsync(id, ct).ConfigureAwait(false);
            return memo is null
                ? Results.NotFound()
                : Results.Json(memo, MemoryJsonContext.Default.Memo);
        });

        app.MapPatch("/api/memos/{id}", async (string id, HttpContext ctx, IMemoStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<UpdateMemoRequest>(ctx, MemoryJsonContext.Default.UpdateMemoRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var updated = await store.UpdateAsync(id, req.Title, req.Body, req.Tags, req.Pinned, ct)
                .ConfigureAwait(false);
            return updated is null
                ? Results.NotFound()
                : Results.Json(updated, MemoryJsonContext.Default.Memo);
        });

        app.MapDelete("/api/memos/{id}", async (string id, IMemoStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return ok ? Results.NoContent() : Results.NotFound();
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

public sealed record CreateMemoRequest(string? Title, string? Body, IReadOnlyList<string>? Tags, bool? Pinned);
public sealed record UpdateMemoRequest(string? Title, string? Body, IReadOnlyList<string>? Tags, bool? Pinned);
public sealed record MemoListResponse(IReadOnlyList<Memo> Memos);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Memo))]
[JsonSerializable(typeof(MemoListResponse))]
[JsonSerializable(typeof(CreateMemoRequest))]
[JsonSerializable(typeof(UpdateMemoRequest))]
public partial class MemoryJsonContext : JsonSerializerContext
{
}
