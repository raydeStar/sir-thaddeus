using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.Memory;

internal static partial class RuntimeApiServer
{
    private static void MapMemoryEndpoints(
        WebApplication app,
        Func<AppSettings> getSettings)
    {
        app.MapGet("/api/memory", async (string? filter, int? take, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled)
            {
                return Results.Json(new MemoryBrowseResponse([], [], [], [], 0, 0, 0, 0), JsonOptions);
            }

            try
            {
                var max = Math.Clamp(take ?? 40, 1, 200);
                using var store = CreateMemoryStore(currentSettings);
                await store.EnsureSchemaAsync(ct);

                var (facts, totalFacts) = await store.ListFactsAsync(filter, 0, max, ct);
                var (events, totalEvents) = await store.ListEventsAsync(filter, 0, max, ct);
                var (chunks, totalChunks) = await store.ListChunksAsync(filter, 0, max, ct);
                var (nuggets, totalNuggets) = await store.ListNuggetsAsync(filter, 0, max, ct);

                var response = new MemoryBrowseResponse(
                    Facts: facts.Select(ToFactDto).ToArray(),
                    Events: events.Select(ToEventDto).ToArray(),
                    Chunks: chunks.Select(ToChunkDto).ToArray(),
                    Nuggets: nuggets.Select(ToNuggetDto).ToArray(),
                    TotalFacts: totalFacts,
                    TotalEvents: totalEvents,
                    TotalChunks: totalChunks,
                    TotalNuggets: totalNuggets);

                return Results.Json(response, JsonOptions);
            }
            catch (Exception ex)
            {
                return Results.Problem(
                    title: "Memory load failed",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapPut("/api/memory/facts/{id}", async (string id, SaveMemoryFactRequest request, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);

            var fact = new MemoryFact
            {
                MemoryId = id,
                ProfileId = request.ProfileId,
                Subject = request.Subject,
                Predicate = request.Predicate,
                Object = request.Object,
                Confidence = request.Confidence,
                SourceRef = request.SourceRef,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.StoreFactAsync(fact, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Fact updated"), JsonOptions);
        });

        app.MapDelete("/api/memory/facts/{id}", async (string id, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");
            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            await store.DeleteFactAsync(id, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Fact deleted"), JsonOptions);
        });

        app.MapPut("/api/memory/events/{id}", async (string id, SaveMemoryEventRequest request, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);

            var evt = new MemoryEvent
            {
                EventId = id,
                ProfileId = request.ProfileId,
                Type = request.Type,
                Title = request.Title,
                Summary = request.Summary,
                WhenIso = request.WhenUtc,
                Confidence = request.Confidence,
                SourceRef = request.SourceRef,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.StoreEventAsync(evt, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Event updated"), JsonOptions);
        });

        app.MapDelete("/api/memory/events/{id}", async (string id, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");
            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            await store.DeleteEventAsync(id, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Event deleted"), JsonOptions);
        });

        app.MapPut("/api/memory/chunks/{id}", async (string id, SaveMemoryChunkRequest request, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);

            var chunk = new MemoryChunk
            {
                ChunkId = id,
                SourceType = request.SourceType,
                Text = request.Text,
                WhenIso = request.WhenUtc,
                SourceRef = request.SourceRef
            };
            await store.StoreChunkAsync(chunk, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Chunk updated"), JsonOptions);
        });

        app.MapDelete("/api/memory/chunks/{id}", async (string id, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");
            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            await store.DeleteChunkAsync(id, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Chunk deleted"), JsonOptions);
        });

        app.MapPut("/api/memory/nuggets/{id}", async (string id, SaveMemoryNuggetRequest request, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);

            var nugget = new MemoryNugget
            {
                NuggetId = id,
                Text = request.Text,
                Tags = request.Tags,
                Weight = request.Weight,
                PinLevel = request.PinLevel,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await store.StoreNuggetAsync(nugget, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Nugget updated"), JsonOptions);
        });

        app.MapDelete("/api/memory/nuggets/{id}", async (string id, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled) return Results.BadRequest("Memory disabled.");
            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            await store.DeleteNuggetAsync(id, ct);
            return Results.Json(new GenericMemoryActionResponse(true, "Nugget deleted"), JsonOptions);
        });
    }
}
