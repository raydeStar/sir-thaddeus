using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.Wiki;

namespace Thaddeus.Runtime.Api;

public static class WikiApi
{
    public static IEndpointRouteBuilder MapWikiApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/wiki/roots", async (IWikiStore store, CancellationToken ct) =>
        {
            var roots = await store.ListRootsAsync(ct).ConfigureAwait(false);
            return Results.Json(new WikiRootsResponse(roots), WikiJsonContext.Default.WikiRootsResponse);
        });

        app.MapPost("/api/wiki/roots", async (HttpContext ctx, IWikiStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, WikiJsonContext.Default.CreateWikiRootRequest, ct).ConfigureAwait(false);
            if (req is null) return Results.BadRequest(new WikiErrorResponse("empty_body", "Request body is required."));

            try
            {
                var root = await store.CreateRootAsync(req.Name ?? string.Empty, req.Path, ct).ConfigureAwait(false);
                audit.Append(new AuditEvent { Actor = "user", Action = "WIKI_ROOT_CREATED", Target = root.Id });
                return Results.Json(root, WikiJsonContext.Default.WikiRoot, statusCode: StatusCodes.Status201Created);
            }
            catch (WikiPathException ex)
            {
                return Results.BadRequest(new WikiErrorResponse("invalid_path", ex.Message));
            }
        });

        app.MapGet("/api/wiki/roots/{rootId}/tree", async (string rootId, IWikiStore store, CancellationToken ct) =>
        {
            var tree = await store.GetTreeAsync(rootId, ct).ConfigureAwait(false);
            return tree is null ? Results.NotFound() : Results.Json(tree, WikiJsonContext.Default.WikiTree);
        });

        app.MapPost("/api/wiki/roots/{rootId}/folders", async (string rootId, HttpContext ctx, IWikiStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, WikiJsonContext.Default.CreateWikiFolderRequest, ct).ConfigureAwait(false);
            if (req is null) return Results.BadRequest(new WikiErrorResponse("empty_body", "Request body is required."));

            try
            {
                var folder = await store.CreateFolderAsync(rootId, req.Name ?? string.Empty, req.ParentFolderId, ct).ConfigureAwait(false);
                audit.Append(new AuditEvent { Actor = "user", Action = "WIKI_FOLDER_CREATED", Target = folder.Id });
                return Results.Json(folder, WikiJsonContext.Default.WikiFolder, statusCode: StatusCodes.Status201Created);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (WikiPathException ex)
            {
                return Results.BadRequest(new WikiErrorResponse("invalid_path", ex.Message));
            }
        });

        app.MapPost("/api/wiki/roots/{rootId}/pages", async (string rootId, HttpContext ctx, IWikiStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, WikiJsonContext.Default.CreateWikiPageRequest, ct).ConfigureAwait(false);
            if (req is null) return Results.BadRequest(new WikiErrorResponse("empty_body", "Request body is required."));

            try
            {
                var page = await store.CreatePageAsync(rootId, req.FolderId, req.Title ?? string.Empty, req.Markdown ?? string.Empty, ct).ConfigureAwait(false);
                audit.Append(new AuditEvent { Actor = "user", Action = "WIKI_PAGE_CREATED", Target = page.Page.Id });
                return Results.Json(page, WikiJsonContext.Default.WikiPageDocument, statusCode: StatusCodes.Status201Created);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (WikiPathException ex)
            {
                return Results.BadRequest(new WikiErrorResponse("invalid_path", ex.Message));
            }
        });

        app.MapGet("/api/wiki/pages/{pageId}", async (string pageId, IWikiStore store, CancellationToken ct) =>
        {
            var page = await store.GetPageAsync(pageId, ct).ConfigureAwait(false);
            return page is null ? Results.NotFound() : Results.Json(page, WikiJsonContext.Default.WikiPageDocument);
        });

        app.MapPatch("/api/wiki/pages/{pageId}", async (string pageId, HttpContext ctx, IWikiStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            var req = await ReadAsync(ctx, WikiJsonContext.Default.UpdateWikiPageRequest, ct).ConfigureAwait(false);
            if (req is null) return Results.BadRequest(new WikiErrorResponse("empty_body", "Request body is required."));

            try
            {
                var updated = await store.UpdatePageAsync(
                    pageId,
                    req.Markdown ?? string.Empty,
                    req.ExpectedVersion,
                    req.Source ?? "user",
                    req.Summary,
                    ct).ConfigureAwait(false);
                if (updated is null) return Results.NotFound();

                audit.Append(new AuditEvent
                {
                    Actor = req.Source is "ai" ? "assistant" : "user",
                    Action = "WIKI_PAGE_UPDATED",
                    Target = pageId,
                    Details = new() { ["version"] = updated.Page.Version, ["source"] = req.Source ?? "user" },
                });
                return Results.Json(updated, WikiJsonContext.Default.WikiPageDocument);
            }
            catch (WikiVersionConflictException ex)
            {
                return Results.Conflict(new WikiConflictResponse(ex.PageId, ex.ExpectedVersion, ex.CurrentVersion));
            }
        });

        app.MapGet("/api/wiki/pages/{pageId}/revisions", async (string pageId, IWikiStore store, CancellationToken ct) =>
        {
            var revisions = await store.ListRevisionsAsync(pageId, ct).ConfigureAwait(false);
            return Results.Json(new WikiRevisionsResponse(revisions), WikiJsonContext.Default.WikiRevisionsResponse);
        });

        app.MapPost("/api/wiki/pages/{pageId}/revisions/{revisionId}/restore", async (string pageId, string revisionId, HttpContext ctx, IWikiStore store, IAuditLogger audit, CancellationToken ct) =>
        {
            RestoreWikiRevisionRequest? req = null;
            if (ctx.Request.ContentLength is > 0)
            {
                req = await ReadAsync(ctx, WikiJsonContext.Default.RestoreWikiRevisionRequest, ct).ConfigureAwait(false);
                if (req is null) return Results.BadRequest(new WikiErrorResponse("invalid_body", "Request body is invalid."));
            }

            try
            {
                var restored = await store.RestoreRevisionAsync(pageId, revisionId, req?.ExpectedVersion, ct).ConfigureAwait(false);
                if (restored is null) return Results.NotFound();

                audit.Append(new AuditEvent
                {
                    Actor = "user",
                    Action = "WIKI_PAGE_RESTORED",
                    Target = pageId,
                    Details = new() { ["revisionId"] = revisionId, ["version"] = restored.Page.Version },
                });
                return Results.Json(restored, WikiJsonContext.Default.WikiPageDocument);
            }
            catch (WikiVersionConflictException ex)
            {
                return Results.Conflict(new WikiConflictResponse(ex.PageId, ex.ExpectedVersion, ex.CurrentVersion));
            }
        });

        app.MapGet("/api/wiki/search", async (string? rootId, string? query, IWikiStore store, CancellationToken ct) =>
        {
            var results = await store.SearchAsync(rootId, query ?? string.Empty, ct).ConfigureAwait(false);
            return Results.Json(new WikiSearchResponse(results), WikiJsonContext.Default.WikiSearchResponse);
        });

        app.MapPost("/api/wiki/roots/{rootId}/index/rebuild", async (string rootId, IWikiStore store, CancellationToken ct) =>
        {
            var result = await store.RebuildIndexAsync(rootId, ct).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Json(result, WikiJsonContext.Default.WikiIndexRebuildResult);
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

public sealed record CreateWikiRootRequest(string? Name, string? Path);
public sealed record CreateWikiFolderRequest(string? Name, string? ParentFolderId);
public sealed record CreateWikiPageRequest(string? Title, string? FolderId, string? Markdown);
public sealed record UpdateWikiPageRequest(string? Markdown, long? ExpectedVersion, string? Source, string? Summary);
public sealed record RestoreWikiRevisionRequest(long? ExpectedVersion);
public sealed record WikiRootsResponse(IReadOnlyList<WikiRoot> Roots);
public sealed record WikiRevisionsResponse(IReadOnlyList<WikiRevision> Revisions);
public sealed record WikiSearchResponse(IReadOnlyList<WikiSearchResult> Results);
public sealed record WikiErrorResponse(string Error, string Message);
public sealed record WikiConflictResponse(string PageId, long ExpectedVersion, long CurrentVersion);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WikiRoot))]
[JsonSerializable(typeof(WikiFolder))]
[JsonSerializable(typeof(WikiPage))]
[JsonSerializable(typeof(WikiRevision))]
[JsonSerializable(typeof(WikiTree))]
[JsonSerializable(typeof(WikiPageDocument))]
[JsonSerializable(typeof(WikiSearchResult))]
[JsonSerializable(typeof(WikiIndexRebuildResult))]
[JsonSerializable(typeof(WikiRootsResponse))]
[JsonSerializable(typeof(WikiRevisionsResponse))]
[JsonSerializable(typeof(WikiSearchResponse))]
[JsonSerializable(typeof(WikiErrorResponse))]
[JsonSerializable(typeof(WikiConflictResponse))]
[JsonSerializable(typeof(CreateWikiRootRequest))]
[JsonSerializable(typeof(CreateWikiFolderRequest))]
[JsonSerializable(typeof(CreateWikiPageRequest))]
[JsonSerializable(typeof(UpdateWikiPageRequest))]
[JsonSerializable(typeof(RestoreWikiRevisionRequest))]
public partial class WikiJsonContext : JsonSerializerContext
{
}