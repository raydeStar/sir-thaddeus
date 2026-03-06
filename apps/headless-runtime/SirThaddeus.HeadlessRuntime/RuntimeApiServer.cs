using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.RuntimeHost;

internal static class RuntimeApiServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(
        int port,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        Action<AppSettings> setSettings,
        IAuditLogger audit,
        ApiPermissionGate? permissionGate,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        var runs = new ConcurrentDictionary<string, RunState>(StringComparer.OrdinalIgnoreCase);

        if (permissionGate is not null)
        {
            permissionGate.Requested += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(RuntimeEventTypes.ToolRequested, payload);
                }
            };

            permissionGate.Resolved += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(
                        payload.Approved ? RuntimeEventTypes.ToolApproved : RuntimeEventTypes.ToolDenied,
                        payload);
                }
            };
        }

        app.MapGet("/api/health", () =>
        {
            return new HealthResponse(
                Status: "ok",
                Version: typeof(RuntimeApiServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Runtime: "headless-runtime",
                UtcNow: DateTimeOffset.UtcNow);
        });

        app.MapGet("/api/audit", async (int? take, CancellationToken ct) =>
        {
            if (audit is not JsonLineAuditLogger logger)
            {
                return Results.Json(Array.Empty<AuditEntryDto>(), JsonOptions);
            }

            var max = Math.Clamp(take ?? 200, 1, 1000);
            var events = await logger.ReadTailAsync(max, ct);
            var dtos = events.Select((evt, index) => new AuditEntryDto(
                Id: $"{evt.Timestamp.ToUnixTimeMilliseconds()}-{index}",
                Category: evt.Action,
                Message: BuildAuditMessage(evt),
                TimestampUtc: evt.Timestamp,
                CorrelationId: evt.PermissionTokenId,
                MetadataJson: evt.Details is null ? null : JsonSerializer.Serialize(evt.Details, JsonOptions)))
                .ToArray();

            return Results.Json(dtos, JsonOptions);
        });

        app.MapGet("/api/memory", async (string? filter, int? take, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled)
            {
                return Results.Json(new MemoryBrowseResponse([], [], [], [], 0, 0, 0, 0), JsonOptions);
            }

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
        });

        app.MapGet("/api/profiles", async (CancellationToken ct) =>
        {
            var currentSettings = getSettings();

            var profiles = Array.Empty<ProfileListItemDto>();
            if (currentSettings.Memory.Enabled)
            {
                using var store = CreateMemoryStore(currentSettings);
                await store.EnsureSchemaAsync(ct);
                var cards = await store.ListProfilesAsync(ct);
                profiles = cards
                    .OrderByDescending(c => string.Equals(c.ProfileId, currentSettings.ActiveProfileId, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(card => new ProfileListItemDto(
                        ProfileId: card.ProfileId,
                        Kind: card.Kind,
                        DisplayName: card.DisplayName,
                        PreferredName: ExtractPreferredName(card.ProfileJson),
                        Relationship: card.Relationship,
                        IsActive: string.Equals(card.ProfileId, currentSettings.ActiveProfileId, StringComparison.OrdinalIgnoreCase),
                        UpdatedAtUtc: card.UpdatedAt.ToUniversalTime()))
                    .ToArray();
            }

            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            var personalities = personalityStore
                .ListProfiles(personalityDirectory)
                .OrderByDescending(p => string.Equals(p.Id, currentSettings.ActivePersonalityId, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(p => new PersonalityListItemDto(
                    Id: p.Id,
                    DisplayName: p.DisplayName,
                    Alias: p.Alias,
                    Description: p.Description,
                    IsActive: string.Equals(p.Id, currentSettings.ActivePersonalityId, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

            var response = new ProfileSummaryResponse(
                ActiveProfileId: currentSettings.ActiveProfileId,
                Profiles: profiles,
                Personalities: personalities,
                ActivePersonalityId: currentSettings.ActivePersonalityId);

            return Results.Json(response, JsonOptions);
        });

        app.MapPost("/api/profiles/active", async (SetActiveProfileRequest request, CancellationToken ct) =>
        {
            var requestedProfileId = (request.ProfileId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(requestedProfileId))
            {
                return Results.BadRequest("ProfileId is required.");
            }

            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled)
            {
                return Results.BadRequest("Memory is disabled in settings.");
            }

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            var profiles = await store.ListProfilesAsync(ct);
            var exists = profiles.Any(p => string.Equals(p.ProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return Results.NotFound();
            }

            var updatedSettings = currentSettings with { ActiveProfileId = requestedProfileId };
            SettingsManager.Save(updatedSettings);
            setSettings(updatedSettings);
            permissionGate?.UpdateSettings(updatedSettings);

            var response = new SetActiveProfileResponse(
                Applied: true,
                ActiveProfileId: requestedProfileId,
                Message: "Active profile updated.");

            return Results.Json(response, JsonOptions);
        });

        app.MapPost("/api/personalities/active", (SetActivePersonalityRequest request) =>
        {
            var requestedPersonalityId = (request.PersonalityId ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(requestedPersonalityId))
            {
                return Results.BadRequest("PersonalityId is required.");
            }

            var currentSettings = getSettings();
            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            var exists = personalityStore
                .ListProfiles(personalityDirectory)
                .Any(p => string.Equals(p.Id, requestedPersonalityId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return Results.NotFound();
            }

            var updatedSettings = currentSettings with { ActivePersonalityId = requestedPersonalityId };
            SettingsManager.Save(updatedSettings);
            setSettings(updatedSettings);
            permissionGate?.UpdateSettings(updatedSettings);

            var response = new SetActivePersonalityResponse(
                Applied: true,
                ActivePersonalityId: requestedPersonalityId,
                Message: "Active personality updated.");

            return Results.Json(response, JsonOptions);
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
                    var response = await orchestrator.ProcessAsync(request.Prompt, state.CancellationToken);
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

            var applied = permissionGate.TryApplyDecision(requestId, request.Approved);
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

        await app.RunAsync(cancellationToken);
    }

    private static SqliteMemoryStore CreateMemoryStore(AppSettings settings)
    {
        var dbPath = RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPath(settings.Memory.DbPath);
        return new SqliteMemoryStore(dbPath);
    }

    private static string? ExtractPreferredName(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            if (doc.RootElement.TryGetProperty("preferred_name", out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch
        {
            // Best effort only.
        }

        return null;
    }

    private static MemoryFactItemDto ToFactDto(MemoryFact fact) => new(
        MemoryId: fact.MemoryId,
        ProfileId: fact.ProfileId,
        Subject: fact.Subject,
        Predicate: fact.Predicate,
        Object: fact.Object,
        Confidence: fact.Confidence,
        UpdatedAtUtc: fact.UpdatedAt.ToUniversalTime(),
        SourceRef: fact.SourceRef);

    private static MemoryEventItemDto ToEventDto(MemoryEvent evt) => new(
        EventId: evt.EventId,
        ProfileId: evt.ProfileId,
        Type: evt.Type,
        Title: evt.Title,
        Summary: evt.Summary,
        WhenUtc: evt.WhenIso?.ToUniversalTime(),
        Confidence: evt.Confidence,
        UpdatedAtUtc: evt.UpdatedAt.ToUniversalTime(),
        SourceRef: evt.SourceRef);

    private static MemoryChunkItemDto ToChunkDto(MemoryChunk chunk) => new(
        ChunkId: chunk.ChunkId,
        SourceType: chunk.SourceType,
        SourceRef: chunk.SourceRef,
        Text: chunk.Text,
        WhenUtc: chunk.WhenIso?.ToUniversalTime());

    private static MemoryNuggetItemDto ToNuggetDto(MemoryNugget nugget) => new(
        NuggetId: nugget.NuggetId,
        Text: nugget.Text,
        Tags: nugget.Tags,
        Weight: nugget.Weight,
        PinLevel: nugget.PinLevel,
        UseCount: nugget.UseCount,
        UpdatedAtUtc: nugget.UpdatedAt.ToUniversalTime());

    private static DeepDiveBriefingDto? ToBriefingDto(DeepDiveBriefing? briefing)
    {
        if (briefing is null)
        {
            return null;
        }

        return new DeepDiveBriefingDto(
            briefing.Version,
            new BriefingTopicDto(
                briefing.Topic.Kind,
                briefing.Topic.Query,
                briefing.Topic.Timezone,
                briefing.Topic.Locale,
                briefing.Topic.UserLocationHint),
            new BriefingHeroDto(
                briefing.Hero.Title,
                briefing.Hero.Confidence,
                briefing.Hero.LastCheckedIso,
                briefing.Hero.StatusLine,
                briefing.Hero.ClosesText,
                briefing.Hero.Address,
                briefing.Hero.Phone,
                briefing.Hero.Website,
                briefing.Hero.DirectionsUrl),
            briefing.Cards.Select(card => new BriefingCardDto(
                card.Type,
                card.Title,
                card.Sources.Select(ToSourceRefDto).ToArray(),
                card.Bullets.ToArray())).ToArray(),
            briefing.Map is null
                ? null
                : new BriefingMapDto(briefing.Map.Latitude, briefing.Map.Longitude, briefing.Map.Label),
            briefing.Audit.Select(step => new BriefingAuditStepDto(
                step.Step,
                step.Detail,
                step.TimestampIso,
                step.Sources.Select(ToSourceRefDto).ToArray())).ToArray());
    }

    private static BriefingSourceRefDto ToSourceRefDto(SourceRef source)
        => new(source.Name, source.Url, source.FetchedIso);

    private static string BuildAuditMessage(AuditEvent auditEvent)
    {
        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
        {
            return $"{auditEvent.Action} -> {auditEvent.Target} ({auditEvent.Result})";
        }

        return $"{auditEvent.Action} ({auditEvent.Result})";
    }

    private sealed class RunState
    {
        private readonly object _gate = new();
        private readonly List<RuntimeEventEnvelope> _history = [];
        private readonly List<ChannelWriter<RuntimeEventEnvelope>> _subscribers = [];
        private readonly CancellationTokenSource _cancellation = new();
        private bool _completed;

        public RunState(string runId)
        {
            RunId = runId;
        }

        public string RunId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public void Cancel() => _cancellation.Cancel();

        public void Complete()
        {
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                subscribers = [.. _subscribers];
                _subscribers.Clear();
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryComplete();
            }
        }

        public void Append(string eventType, object payload)
        {
            var envelope = new RuntimeEventEnvelope(eventType, RunId, DateTimeOffset.UtcNow, payload);
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                _history.Add(envelope);
                subscribers = [.. _subscribers];
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryWrite(envelope);
            }
        }

        public async IAsyncEnumerable<RuntimeEventEnvelope> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ChannelReader<RuntimeEventEnvelope>? reader = null;
            List<RuntimeEventEnvelope> replay;
            ChannelWriter<RuntimeEventEnvelope>? writer = null;

            lock (_gate)
            {
                replay = [.. _history];
                if (!_completed)
                {
                    var channel = Channel.CreateUnbounded<RuntimeEventEnvelope>();
                    writer = channel.Writer;
                    reader = channel.Reader;
                    _subscribers.Add(writer);
                }
            }

            try
            {
                foreach (var evt in replay)
                {
                    yield return evt;
                }

                if (reader is null)
                {
                    yield break;
                }

                await foreach (var evt in reader.ReadAllAsync(cancellationToken))
                {
                    yield return evt;
                }
            }
            finally
            {
                if (writer is not null)
                {
                    lock (_gate)
                    {
                        _subscribers.Remove(writer);
                    }
                }
            }
        }
    }
}

internal sealed class ApiPermissionGate : IToolPermissionGate
{
    private readonly Func<string?> _currentRunIdAccessor;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile PolicySnapshot _snapshot;

    public ApiPermissionGate(AppSettings initialSettings, Func<string?> currentRunIdAccessor)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(initialSettings, isDebugBuild: false);
        _currentRunIdAccessor = currentRunIdAccessor;
    }

    public event Action<string, ToolRequestedPayload>? Requested;
    public event Action<string, ToolDecisionPayload>? Resolved;

    public void UpdateSettings(AppSettings settings)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(settings, isDebugBuild: false);
    }

    public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        var policy = ToolGroupPolicy.ResolveEffectivePolicy(group, _snapshot);

        if (policy == "off")
        {
            return Task.FromResult(ToolPermissionResult.Deny("Disabled in settings"));
        }

        if (policy == "always" || group == "meta")
        {
            return Task.FromResult(ToolPermissionResult.NotRequired());
        }

        return WaitForDecisionAsync(canonical, argumentsJson, ct);
    }

    public bool TryApplyDecision(string requestId, bool approved)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(approved);
            return true;
        }

        return false;
    }

    private async Task<ToolPermissionResult> WaitForDecisionAsync(
        string canonicalToolName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var runId = _currentRunIdAccessor() ?? "unknown";
        var reason = ToolGroupPolicy.BuildRedactedPurpose(canonicalToolName, argumentsJson);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        Requested?.Invoke(runId, new ToolRequestedPayload(
            RequestId: requestId,
            ToolName: canonicalToolName,
            Reason: reason,
            ArgumentsJson: argumentsJson));

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        bool approved;
        try
        {
            approved = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, false));
            return ToolPermissionResult.Deny("Cancelled");
        }

        Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, approved));
        return approved
            ? ToolPermissionResult.Grant()
            : ToolPermissionResult.Deny("Denied by user");
    }
}

internal static class RunExecutionContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentRunId => Current.Value;

    public static IDisposable Enter(string runId)
    {
        var previous = Current.Value;
        Current.Value = runId;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Current.Value = _previous;
        }
    }
}



