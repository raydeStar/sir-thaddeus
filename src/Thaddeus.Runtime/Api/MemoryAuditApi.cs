using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.Memory;
using Thaddeus.Runtime.Memory;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// User-facing audit surface for the SQLite-backed semantic memory store.
///
/// <para>The runtime's <c>AutoMemoryExtractor</c> silently writes facts,
/// events, chunks, and nuggets to <see cref="IMemoryStore"/> after every
/// chat turn. Without this API the user has no way to see what's been
/// learned about them, correct wrong extractions, or delete sensitive
/// entries — the single most-cited gap when comparing this system to
/// industry-standard agent memory (ChatGPT memory, Claude memory).</para>
///
/// <para>Read endpoints return display-shaped DTOs that strip embeddings
/// and other internal fields. Write endpoints preserve the original
/// extraction provenance while allowing user corrections, recomputing
/// any dedupe keys derived from editable fields so future auto-extraction
/// upserts stay coherent.</para>
/// </summary>
public static class MemoryAuditApi
{
    public static IEndpointRouteBuilder MapMemoryAuditApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/memory/overview", async (IMemoryStore store, CancellationToken ct) =>
        {
            var (facts, factCount) = await store.ListFactsAsync(null, 0, 0, ct).ConfigureAwait(false);
            var (events, eventCount) = await store.ListEventsAsync(null, 0, 0, ct).ConfigureAwait(false);
            var (chunks, chunkCount) = await store.ListChunksAsync(null, 0, 0, ct).ConfigureAwait(false);
            var (nuggets, nuggetCount) = await store.ListNuggetsAsync(null, 0, 0, ct).ConfigureAwait(false);
            var profile = await store.GetUserProfileAsync(ct).ConfigureAwait(false);

            // The List*Async take=0 calls give us TotalCount cheaply without
            // returning rows; ListFactsAsync etc. are documented as honouring
            // the count regardless of page size.
            _ = facts; _ = events; _ = chunks; _ = nuggets;

            return Results.Json(new MemoryOverviewResponse(
                FactCount: factCount,
                EventCount: eventCount,
                ChunkCount: chunkCount,
                NuggetCount: nuggetCount,
                Profile: profile is null ? null : ToDto(profile)),
                MemoryAuditJsonContext.Default.MemoryOverviewResponse);
        });

        app.MapGet("/api/memory/nuggets", async (string? filter, int? limit, IMemoryStore store, CancellationToken ct) =>
        {
            var take = ClampPage(limit, fallback: 50, max: 500);
            var (items, total) = await store.ListNuggetsAsync(filter, skip: 0, take, ct).ConfigureAwait(false);
            return Results.Json(
                new NuggetListResponse(items.Select(ToDto).ToArray(), total),
                MemoryAuditJsonContext.Default.NuggetListResponse);
        });

        app.MapGet("/api/memory/facts", async (string? filter, int? limit, IMemoryStore store, CancellationToken ct) =>
        {
            var take = ClampPage(limit, fallback: 50, max: 500);
            var (items, total) = await store.ListFactsAsync(filter, skip: 0, take, ct).ConfigureAwait(false);
            return Results.Json(
                new FactListResponse(items.Select(ToDto).ToArray(), total),
                MemoryAuditJsonContext.Default.FactListResponse);
        });

        app.MapGet("/api/memory/events", async (string? filter, int? limit, IMemoryStore store, CancellationToken ct) =>
        {
            var take = ClampPage(limit, fallback: 50, max: 500);
            var (items, total) = await store.ListEventsAsync(filter, skip: 0, take, ct).ConfigureAwait(false);
            return Results.Json(
                new EventListResponse(items.Select(ToDto).ToArray(), total),
                MemoryAuditJsonContext.Default.EventListResponse);
        });

        app.MapGet("/api/memory/profiles", async (IMemoryStore store, CancellationToken ct) =>
        {
            var items = await store.ListProfilesAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new ProfileListResponse(items.Select(ToDto).ToArray()),
                MemoryAuditJsonContext.Default.ProfileListResponse);
        });

        app.MapDelete("/api/memory/nuggets/{id}", async (string id, IMemoryStore store, CancellationToken ct) =>
        {
            await store.DeleteNuggetAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapDelete("/api/memory/facts/{id}", async (string id, IMemoryStore store, CancellationToken ct) =>
        {
            await store.DeleteFactAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapDelete("/api/memory/events/{id}", async (string id, IMemoryStore store, CancellationToken ct) =>
        {
            await store.DeleteEventAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        app.MapPut("/api/memory/nuggets/{id}", async (
            string id,
            HttpContext ctx,
            IMemoryStore store,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var req = await System.Text.Json.JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                MemoryAuditJsonContext.Default.UpdateNuggetRequest,
                ct).ConfigureAwait(false);
            if (req is null)
                return Results.BadRequest(new { error = "empty_body" });

            var text = req.Text?.Trim();
            if (string.IsNullOrWhiteSpace(text))
                return Results.BadRequest(new { error = "text_required" });

            var existing = await store.FindNuggetByIdAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
                return Results.NotFound(new { error = "nugget_not_found", id });

            var updated = ApplyNuggetCorrection(existing, req, DateTimeOffset.UtcNow);

            await store.StoreNuggetAsync(updated, ct).ConfigureAwait(false);
            await audit.AppendAsync(new AuditEvent
            {
                Actor = "runtime.memory_audit",
                Action = "memory.correction.nugget",
                Target = id,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["oldText"] = existing.Text,
                    ["newText"] = updated.Text,
                    ["oldTags"] = existing.Tags ?? string.Empty,
                    ["newTags"] = updated.Tags ?? string.Empty,
                    ["sourceTurnId"] = existing.SourceTurnId ?? string.Empty,
                    ["origin"] = existing.Origin ?? string.Empty,
                }
            }, ct).ConfigureAwait(false);

            return Results.Json(ToDto(updated), MemoryAuditJsonContext.Default.NuggetDto);
        });

        app.MapPut("/api/memory/facts/{id}", async (
            string id,
            HttpContext ctx,
            IMemoryStore store,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            var req = await System.Text.Json.JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                MemoryAuditJsonContext.Default.UpdateFactRequest,
                ct).ConfigureAwait(false);
            if (req is null)
                return Results.BadRequest(new { error = "empty_body" });

            var subject = req.Subject?.Trim();
            var predicate = req.Predicate?.Trim();
            var @object = req.Object?.Trim();
            if (string.IsNullOrWhiteSpace(subject) ||
                string.IsNullOrWhiteSpace(predicate) ||
                string.IsNullOrWhiteSpace(@object))
            {
                return Results.BadRequest(new { error = "subject_predicate_object_required" });
            }

            var existing = await store.FindFactByIdAsync(id, ct).ConfigureAwait(false);
            if (existing is null)
                return Results.NotFound(new { error = "fact_not_found", id });

            var updated = ApplyFactCorrection(existing, req, DateTimeOffset.UtcNow);

            await store.StoreFactAsync(updated, ct).ConfigureAwait(false);
            await audit.AppendAsync(new AuditEvent
            {
                Actor = "runtime.memory_audit",
                Action = "memory.correction.fact",
                Target = id,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["oldSubject"] = existing.Subject,
                    ["oldPredicate"] = existing.Predicate,
                    ["oldObject"] = existing.Object,
                    ["newSubject"] = updated.Subject,
                    ["newPredicate"] = updated.Predicate,
                    ["newObject"] = updated.Object,
                    ["sourceTurnId"] = existing.SourceTurnId ?? string.Empty,
                    ["origin"] = existing.Origin ?? string.Empty,
                }
            }, ct).ConfigureAwait(false);

            return Results.Json(ToDto(updated), MemoryAuditJsonContext.Default.FactDto);
        });

        // Manual reflection trigger. Returns the same report shape the
        // UI uses to render the "last reflection" panel — no separate
        // GET. Idempotent in the sense that a second call with no new
        // duplicates returns an empty Actions list.
        app.MapPost("/api/memory/reflect", async (
            MemoryReflectionService service,
            CancellationToken ct) =>
        {
            var report = await service.RunAsync(ct).ConfigureAwait(false);
            return Results.Json(report, MemoryAuditJsonContext.Default.ReflectionReport);
        });

        // Pin/unpin nuggets. Reads current state, flips PinLevel between
        // 0 (normal) and 1 (pinned), and upserts. Level 2 ("system") is
        // reserved for the agent's own promotions and isn't reachable
        // from the UI.
        app.MapPost("/api/memory/nuggets/{id}/pin", async (
            string id,
            HttpContext ctx,
            IMemoryStore store,
            CancellationToken ct) =>
        {
            var req = await System.Text.Json.JsonSerializer.DeserializeAsync(
                ctx.Request.Body,
                MemoryAuditJsonContext.Default.PinRequest,
                ct).ConfigureAwait(false);
            var current = await store.FindNuggetByIdAsync(id, ct).ConfigureAwait(false);
            if (current is null) return Results.NotFound(new { error = "nugget_not_found", id });

            var desired = req?.Pinned ?? (current.PinLevel == 0);
            var nextLevel = desired ? 1 : 0;
            if (current.PinLevel == nextLevel)
            {
                return Results.Json(ToDto(current), MemoryAuditJsonContext.Default.NuggetDto);
            }

            var updated = await store.SetNuggetPinLevelAsync(id, nextLevel, ct).ConfigureAwait(false)
                ?? current with { PinLevel = nextLevel, UpdatedAt = DateTimeOffset.UtcNow };
            return Results.Json(ToDto(updated), MemoryAuditJsonContext.Default.NuggetDto);
        });

        return app;
    }

    private static int ClampPage(int? limit, int fallback, int max)
        => limit is null or < 1 ? fallback : Math.Min(limit.Value, max);

    internal static MemoryNugget ApplyNuggetCorrection(
        MemoryNugget existing,
        UpdateNuggetRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(request);

        var text = request.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("text_required", nameof(request));

        return existing with
        {
            Text = text,
            Tags = request.TagsProvided ? NormalizeTags(request.Tags) : existing.Tags,
            DedupeKey = ComputeHash(text.ToLowerInvariant()),
            UpdatedAt = now,
        };
    }

    internal static MemoryFact ApplyFactCorrection(
        MemoryFact existing,
        UpdateFactRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(request);

        var subject = request.Subject?.Trim();
        var predicate = request.Predicate?.Trim();
        var @object = request.Object?.Trim();
        if (string.IsNullOrWhiteSpace(subject) ||
            string.IsNullOrWhiteSpace(predicate) ||
            string.IsNullOrWhiteSpace(@object))
        {
            throw new ArgumentException("subject_predicate_object_required", nameof(request));
        }

        return existing with
        {
            Subject = subject,
            Predicate = predicate,
            Object = @object,
            Confidence = Math.Max(existing.Confidence, 0.95),
            DedupeKey = ComputeHash($"{subject.ToLowerInvariant()}|{predicate.ToLowerInvariant()}"),
            UpdatedAt = now,
        };
    }

    internal static string? NormalizeTags(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var tags = raw
            .Split([',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return tags.Length == 0 ? null : $";{string.Join(';', tags)};";
    }

    internal static string ComputeHash(string input)
    {
        using var sha256 = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    // ── DTO mappers ──────────────────────────────────────────────────
    // Strip embeddings (float[] payloads run into the megabytes), normalize
    // enum casing, and flatten DateTimeOffsets so the wire shape is stable
    // and small.

    private static NuggetDto ToDto(MemoryNugget n) => new(
        Id: n.NuggetId,
        Text: n.Text,
        Tags: n.Tags,
        Pinned: n.PinLevel >= 1,
        PinLevel: n.PinLevel,
        Weight: n.Weight,
        Sensitivity: n.Sensitivity,
        UseCount: n.UseCount,
        LastUsedAt: n.LastUsedAt,
        CreatedAt: n.CreatedAt,
        UpdatedAt: n.UpdatedAt,
        Origin: n.Origin,
        SourceTurnId: n.SourceTurnId);

    private static FactDto ToDto(MemoryFact f) => new(
        Id: f.MemoryId,
        Subject: f.Subject,
        Predicate: f.Predicate,
        Object: f.Object,
        Confidence: f.Confidence,
        Weight: f.Weight,
        Sensitivity: f.Sensitivity.ToString().ToLowerInvariant(),
        CreatedAt: f.CreatedAt,
        UpdatedAt: f.UpdatedAt,
        Origin: f.Origin,
        ProfileId: f.ProfileId,
        SourceTurnId: f.SourceTurnId,
        SourceRef: f.SourceRef);

    private static EventDto ToDto(MemoryEvent e) => new(
        Id: e.EventId,
        Type: e.Type,
        Title: e.Title,
        Summary: e.Summary,
        WhenIso: e.WhenIso,
        Confidence: e.Confidence,
        Weight: e.Weight,
        Sensitivity: e.Sensitivity.ToString().ToLowerInvariant(),
        CreatedAt: e.CreatedAt,
        UpdatedAt: e.UpdatedAt,
        Origin: e.Origin,
        ProfileId: e.ProfileId,
        SourceTurnId: e.SourceTurnId,
        SourceRef: e.SourceRef);

    private static ProfileDto ToDto(ProfileCard p) => new(
        Id: p.ProfileId,
        Kind: p.Kind,
        DisplayName: p.DisplayName,
        Relationship: p.Relationship,
        Aliases: p.Aliases,
        ProfileJson: p.ProfileJson,
        UpdatedAt: p.UpdatedAt);
}

// ── Wire DTOs ─────────────────────────────────────────────────────────

public sealed record MemoryOverviewResponse(
    int FactCount,
    int EventCount,
    int ChunkCount,
    int NuggetCount,
    ProfileDto? Profile);

public sealed record NuggetDto(
    string Id,
    string Text,
    string? Tags,
    bool Pinned,
    int PinLevel,
    double Weight,
    string Sensitivity,
    int UseCount,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Origin,
    string? SourceTurnId);

public sealed record FactDto(
    string Id,
    string Subject,
    string Predicate,
    string Object,
    double Confidence,
    double Weight,
    string Sensitivity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Origin,
    string? ProfileId,
    string? SourceTurnId,
    string? SourceRef);

public sealed record EventDto(
    string Id,
    string Type,
    string Title,
    string? Summary,
    DateTimeOffset? WhenIso,
    double Confidence,
    double Weight,
    string Sensitivity,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Origin,
    string? ProfileId,
    string? SourceTurnId,
    string? SourceRef);

public sealed record ProfileDto(
    string Id,
    string Kind,
    string DisplayName,
    string? Relationship,
    string? Aliases,
    string ProfileJson,
    DateTimeOffset UpdatedAt);

public sealed record NuggetListResponse(IReadOnlyList<NuggetDto> Items, int TotalCount);
public sealed record FactListResponse(IReadOnlyList<FactDto> Items, int TotalCount);
public sealed record EventListResponse(IReadOnlyList<EventDto> Items, int TotalCount);
public sealed record ProfileListResponse(IReadOnlyList<ProfileDto> Items);

public sealed record PinRequest(bool? Pinned);
public sealed record UpdateNuggetRequest(string? Text, string? Tags, bool TagsProvided = false);
public sealed record UpdateFactRequest(string? Subject, string? Predicate, string? Object);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(MemoryOverviewResponse))]
[JsonSerializable(typeof(NuggetDto))]
[JsonSerializable(typeof(FactDto))]
[JsonSerializable(typeof(EventDto))]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(NuggetListResponse))]
[JsonSerializable(typeof(FactListResponse))]
[JsonSerializable(typeof(EventListResponse))]
[JsonSerializable(typeof(ProfileListResponse))]
[JsonSerializable(typeof(PinRequest))]
[JsonSerializable(typeof(UpdateNuggetRequest))]
[JsonSerializable(typeof(UpdateFactRequest))]
[JsonSerializable(typeof(ReflectionReport))]
[JsonSerializable(typeof(ReflectionAction))]
public partial class MemoryAuditJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
