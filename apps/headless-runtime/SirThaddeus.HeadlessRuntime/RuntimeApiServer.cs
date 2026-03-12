using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions EditableDocumentJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
    private static readonly JsonDocumentOptions EditableDocumentReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task RunAsync(
        int port,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        Action<AppSettings> setSettings,
        Func<CancellationToken, Task<SearchStatusResponse>> getSearchStatus,
        IAuditLogger audit,
        ApiPermissionGate? permissionGate,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        var runs = new ConcurrentDictionary<string, RunState>(StringComparer.OrdinalIgnoreCase);
        void PersistSettings(AppSettings updatedSettings)
        {
            SettingsManager.Save(updatedSettings);
            var persistedSettings = SettingsManager.Load();
            setSettings(persistedSettings);
            permissionGate?.UpdateSettings(persistedSettings);
        }

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

        app.MapGet("/api/search/status", async (CancellationToken ct) =>
        {
            var snapshot = await getSearchStatus(ct);
            return Results.Json(snapshot, JsonOptions);
        });

        app.MapPut("/api/settings", (AppSettings request) =>
        {
            PersistSettings(request);
            return Results.Json(getSettings(), JsonOptions);
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
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
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

        app.MapGet("/api/profiles/template", (string? profileId) =>
        {
            var suggestedId = string.IsNullOrWhiteSpace(profileId)
                ? ($"profile-{Guid.NewGuid():N}")[..16]
                : profileId.Trim();

            var response = new ProfileDocumentResponse(
                ProfileId: suggestedId,
                DocumentJson: CreateProfileTemplateJson(suggestedId));

            return Results.Json(response, JsonOptions);
        });

        app.MapGet("/api/profiles/{profileId}", async (string profileId, CancellationToken ct) =>
        {
            var requestedProfileId = (profileId ?? "").Trim();
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
            var profile = profiles.FirstOrDefault(p => string.Equals(p.ProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return Results.NotFound();
            }

            var response = new ProfileDocumentResponse(
                ProfileId: profile.ProfileId,
                DocumentJson: BuildProfileDocumentJson(profile));

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
            PersistSettings(updatedSettings);

            var response = new SetActiveProfileResponse(
                Applied: true,
                ActiveProfileId: requestedProfileId,
                Message: "Active profile updated.");

            return Results.Json(response, JsonOptions);
        });

        app.MapPost("/api/profiles", async (SaveProfileDocumentRequest request, CancellationToken ct) =>
        {
            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled)
            {
                return Results.BadRequest("Memory is disabled in settings.");
            }

            if (!TryParseProfileDocument(request.DocumentJson, out var profile, out var error))
            {
                return Results.BadRequest(error);
            }

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            var profiles = await store.ListProfilesAsync(ct);
            var exists = profiles.Any(p => string.Equals(p.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                return Results.Conflict($"Profile '{profile.ProfileId}' already exists.");
            }

            await store.StoreProfileAsync(profile, ct);

            var response = new SaveProfileDocumentResponse(
                Applied: true,
                ProfileId: profile.ProfileId,
                Message: $"Saved profile '{profile.ProfileId}'.");

            return Results.Json(response, JsonOptions);
        });

        app.MapPut("/api/profiles/{profileId}", async (string profileId, SaveProfileDocumentRequest request, CancellationToken ct) =>
        {
            var routeProfileId = (profileId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(routeProfileId))
            {
                return Results.BadRequest("ProfileId is required.");
            }

            var currentSettings = getSettings();
            if (!currentSettings.Memory.Enabled)
            {
                return Results.BadRequest("Memory is disabled in settings.");
            }

            if (!TryParseProfileDocument(request.DocumentJson, out var profile, out var error))
            {
                return Results.BadRequest(error);
            }

            if (!string.Equals(routeProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Profile id in the document must match the profile being edited.");
            }

            using var store = CreateMemoryStore(currentSettings);
            await store.EnsureSchemaAsync(ct);
            var profiles = await store.ListProfilesAsync(ct);
            var exists = profiles.Any(p => string.Equals(p.ProfileId, routeProfileId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return Results.NotFound();
            }

            await store.StoreProfileAsync(profile, ct);

            var response = new SaveProfileDocumentResponse(
                Applied: true,
                ProfileId: profile.ProfileId,
                Message: $"Saved profile '{profile.ProfileId}'.");

            return Results.Json(response, JsonOptions);
        });

        app.MapDelete("/api/profiles/{profileId}", async (string profileId, CancellationToken ct) =>
        {
            var requestedProfileId = (profileId ?? "").Trim();
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
            var profile = profiles.FirstOrDefault(p => string.Equals(p.ProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                return Results.NotFound();
            }

            await store.DeleteProfileAsync(requestedProfileId, ct);

            string? nextActiveProfileId = currentSettings.ActiveProfileId;
            if (string.Equals(currentSettings.ActiveProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                var remaining = (await store.ListProfilesAsync(ct))
                    .OrderByDescending(p => string.Equals(p.Kind, "user", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                nextActiveProfileId = remaining.FirstOrDefault()?.ProfileId;
                PersistSettings(currentSettings with { ActiveProfileId = nextActiveProfileId });
            }

            var response = new DeleteProfileResponse(
                Applied: true,
                ActiveProfileId: nextActiveProfileId,
                Message: string.Equals(currentSettings.ActiveProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase)
                    ? $"Deleted profile '{requestedProfileId}'. Active profile is now '{nextActiveProfileId ?? "(none)"}'."
                    : $"Deleted profile '{requestedProfileId}'.");

            return Results.Json(response, JsonOptions);
        });

        app.MapGet("/api/personalities/template", (string? personalityId) =>
        {
            var suggestedId = string.IsNullOrWhiteSpace(personalityId)
                ? ($"personality_{Guid.NewGuid():N}")[..20]
                : personalityId.Trim().ToLowerInvariant();
            var template = PersonalityProfileTemplateFactory.CreateAverageTemplate(suggestedId);

            var response = new PersonalityDocumentResponse(
                PersonalityId: suggestedId,
                DocumentJson: PersonalityProfileTemplateFactory.RenderMinimalTemplateJson(template));

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
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
            var exists = personalityStore
                .ListProfiles(personalityDirectory)
                .Any(p => string.Equals(p.Id, requestedPersonalityId, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return Results.NotFound();
            }

            var updatedSettings = currentSettings with { ActivePersonalityId = requestedPersonalityId };
            PersistSettings(updatedSettings);

            var response = new SetActivePersonalityResponse(
                Applied: true,
                ActivePersonalityId: requestedPersonalityId,
                Message: "Active personality updated.");

            return Results.Json(response, JsonOptions);
        });

        app.MapGet("/api/personalities/{personalityId}", (string personalityId) =>
        {
            var requestedPersonalityId = (personalityId ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(requestedPersonalityId))
            {
                return Results.BadRequest("PersonalityId is required.");
            }

            var currentSettings = getSettings();
            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
            var path = personalityStore.ResolveProfilePath(personalityDirectory, requestedPersonalityId);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var response = new PersonalityDocumentResponse(
                PersonalityId: requestedPersonalityId,
                DocumentJson: File.ReadAllText(path));

            return Results.Json(response, JsonOptions);
        });

        app.MapPost("/api/personalities", (SavePersonalityDocumentRequest request) =>
        {
            if (!TryParsePersonalityDocument(request.DocumentJson, out var profile, out var error))
            {
                return Results.BadRequest(error);
            }

            var currentSettings = getSettings();
            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
            var path = personalityStore.ResolveProfilePath(personalityDirectory, profile.Id);
            if (File.Exists(path))
            {
                return Results.Conflict($"Personality '{profile.Id}' already exists.");
            }

            personalityStore.SaveProfileTemplate(personalityDirectory, profile, request.DocumentJson);

            var response = new SavePersonalityDocumentResponse(
                Applied: true,
                PersonalityId: profile.Id,
                Message: $"Saved personality '{profile.Id}'.");

            return Results.Json(response, JsonOptions);
        });

        app.MapPut("/api/personalities/{personalityId}", (string personalityId, SavePersonalityDocumentRequest request) =>
        {
            var routePersonalityId = (personalityId ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(routePersonalityId))
            {
                return Results.BadRequest("PersonalityId is required.");
            }

            if (!TryParsePersonalityDocument(request.DocumentJson, out var profile, out var error))
            {
                return Results.BadRequest(error);
            }

            if (!string.Equals(routePersonalityId, profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Personality id in the document must match the personality being edited.");
            }

            var currentSettings = getSettings();
            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
            var path = personalityStore.ResolveProfilePath(personalityDirectory, routePersonalityId);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            personalityStore.SaveProfileTemplate(personalityDirectory, profile, request.DocumentJson);

            var response = new SavePersonalityDocumentResponse(
                Applied: true,
                PersonalityId: profile.Id,
                Message: $"Saved personality '{profile.Id}'.");

            return Results.Json(response, JsonOptions);
        });

        app.MapDelete("/api/personalities/{personalityId}", (string personalityId) =>
        {
            var requestedPersonalityId = (personalityId ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(requestedPersonalityId))
            {
                return Results.BadRequest("PersonalityId is required.");
            }

            if (string.Equals(requestedPersonalityId, BuiltInProfileCatalog.HelpfulDefaultId, StringComparison.OrdinalIgnoreCase))
            {
                return Results.BadRequest("Cannot delete the fallback default personality.");
            }

            var currentSettings = getSettings();
            var personalityStore = new PersonalityProfileStore();
            var personalityDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings);
            personalityStore.EnsureBuiltInsInstalled(personalityDirectory);
            var path = personalityStore.ResolveProfilePath(personalityDirectory, requestedPersonalityId);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            File.Delete(path);

            var nextActivePersonalityId = currentSettings.ActivePersonalityId;
            if (string.Equals(currentSettings.ActivePersonalityId, requestedPersonalityId, StringComparison.OrdinalIgnoreCase))
            {
                nextActivePersonalityId = BuiltInProfileCatalog.HelpfulDefaultId;
                PersistSettings(currentSettings with { ActivePersonalityId = nextActivePersonalityId });
            }

            var response = new DeletePersonalityResponse(
                Applied: true,
                ActivePersonalityId: nextActivePersonalityId,
                Message: string.Equals(currentSettings.ActivePersonalityId, requestedPersonalityId, StringComparison.OrdinalIgnoreCase)
                    ? $"Deleted personality '{requestedPersonalityId}'. Active personality is now '{nextActivePersonalityId}'."
                    : $"Deleted personality '{requestedPersonalityId}'.");

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

    private static string BuildProfileDocumentJson(ProfileCard profile)
    {
        JsonElement data;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(profile.ProfileJson) ? "{}" : profile.ProfileJson, EditableDocumentReadOptions);
            data = doc.RootElement.Clone();
        }
        catch
        {
            data = ParseJsonElement("{}");
        }

        var document = new EditableProfileCardDocument
        {
            ProfileId = profile.ProfileId,
            Kind = string.IsNullOrWhiteSpace(profile.Kind) ? "user" : profile.Kind,
            DisplayName = profile.DisplayName,
            Relationship = profile.Relationship,
            Aliases = SplitAliases(profile.Aliases).ToArray(),
            Data = data
        };

        return JsonSerializer.Serialize(document, EditableDocumentJsonOptions);
    }

    private static string CreateProfileTemplateJson(string profileId)
    {
        var document = new EditableProfileCardDocument
        {
            ProfileId = profileId,
            Kind = "user",
            DisplayName = "New Profile",
            Relationship = "",
            Aliases = [profileId],
            Data = ParseJsonElement("""
                {
                  "preferred_name": "Preferred name",
                  "pronouns": "they/them",
                  "timezone": "America/Denver",
                  "location": "Denver, CO",
                  "style": "Direct, practical, friendly",
                  "about_me": "Short summary for this profile",
                  "highlight": "One useful detail to remember",
                  "notes": "Anything else worth keeping handy",
                  "never_mention": []
                }
                """)
        };

        return JsonSerializer.Serialize(document, EditableDocumentJsonOptions);
    }

    private static bool TryParseProfileDocument(string? documentJson, out ProfileCard profile, out string error)
    {
        profile = new ProfileCard
        {
            ProfileId = "",
            DisplayName = ""
        };
        error = "";

        if (string.IsNullOrWhiteSpace(documentJson))
        {
            error = "DocumentJson is required.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(documentJson, EditableDocumentReadOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Profile document must be a JSON object.";
                return false;
            }

            var profileId = ReadRequiredString(root, "profile_id", out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            var displayName = ReadRequiredString(root, "display_name", out error);
            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            var kind = ReadOptionalString(root, "kind");
            kind = string.IsNullOrWhiteSpace(kind) ? "user" : kind.Trim().ToLowerInvariant();
            if (!string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "person", StringComparison.OrdinalIgnoreCase))
            {
                error = "Profile kind must be 'user' or 'person'.";
                return false;
            }

            var relationship = ReadOptionalString(root, "relationship");
            var aliases = ReadAliases(root);
            var data = root.TryGetProperty("data", out var dataNode) && dataNode.ValueKind != JsonValueKind.Null
                ? dataNode
                : ParseJsonElement("{}");
            if (data.ValueKind != JsonValueKind.Object)
            {
                error = "Profile document field 'data' must be a JSON object.";
                return false;
            }

            var profileJson = JsonSerializer.Serialize(data, EditableDocumentJsonOptions);
            profile = new ProfileCard
            {
                ProfileId = profileId,
                Kind = kind,
                DisplayName = displayName,
                Relationship = string.IsNullOrWhiteSpace(relationship) ? null : relationship.Trim(),
                Aliases = aliases.Count == 0 ? null : string.Join(';', aliases),
                ProfileJson = profileJson,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryParsePersonalityDocument(string? documentJson, out PersonalityProfile profile, out string error)
    {
        profile = new PersonalityProfile();
        error = "";

        if (string.IsNullOrWhiteSpace(documentJson))
        {
            error = "DocumentJson is required.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(documentJson, EditableDocumentReadOptions);
            var validator = new PersonalityProfileValidator();
            var validation = validator.ValidateJson(doc.RootElement);
            if (!validation.IsValid)
            {
                error = $"{validation.ReasonCode}: {validation.Detail}";
                return false;
            }

            profile = PersonalityProfileProjection.FromJson(doc.RootElement);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
        if (string.Equals(auditEvent.Action, "WEB_SEARCH_PROVIDER_TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWebSearchProviderTraceMessage(auditEvent);
        }

        if (string.Equals(auditEvent.Action, "SEARXNG_AUTOSTART", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSearxngAutostartMessage(auditEvent);
        }

        if (string.Equals(auditEvent.Action, "LOCAL_NEWS_QUERY_RETRY_ABORTED", StringComparison.OrdinalIgnoreCase))
        {
            return BuildLocalNewsRetryAbortedMessage(auditEvent);
        }

        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
        {
            return $"{auditEvent.Action} -> {auditEvent.Target} ({auditEvent.Result})";
        }

        return $"{auditEvent.Action} ({auditEvent.Result})";
    }

    private static string BuildWebSearchProviderTraceMessage(AuditEvent auditEvent)
    {
        var requestedQuery = ReadAuditDetail(auditEvent, "requested_query");
        var effectiveQuery = ReadAuditDetail(auditEvent, "effective_query");
        var provider = ReadAuditDetail(auditEvent, "provider");
        var pathSummary = NormalizeAuditValue(ReadAuditDetail(auditEvent, "path_summary"));
        var sourceCount = ReadAuditDetail(auditEvent, "source_count");
        var failureCode = ReadAuditDetail(auditEvent, "failure_code");
        var failureMessage = NormalizeAuditValue(ReadAuditDetail(auditEvent, "failure_message"));
        var failure = !string.IsNullOrWhiteSpace(failureCode) ? failureCode : failureMessage;

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestedQuery))
        {
            if (!string.IsNullOrWhiteSpace(effectiveQuery) &&
                !string.Equals(requestedQuery, effectiveQuery, StringComparison.Ordinal))
            {
                parts.Add($"query=\"{requestedQuery}\" -> \"{effectiveQuery}\"");
            }
            else
            {
                parts.Add($"query=\"{requestedQuery}\"");
            }
        }

        if (!string.IsNullOrWhiteSpace(provider))
            parts.Add($"provider={provider}");

        if (!string.IsNullOrWhiteSpace(sourceCount))
            parts.Add($"sources={sourceCount}");

        if (!string.IsNullOrWhiteSpace(failure))
            parts.Add($"failure={failure}");

        if (!string.IsNullOrWhiteSpace(pathSummary))
            parts.Add($"path={pathSummary}");

        return parts.Count == 0
            ? "Web search provider trace."
            : "Web search provider trace: " + string.Join(" | ", parts);
    }

    private static string BuildSearxngAutostartMessage(AuditEvent auditEvent)
    {
        var status = ReadAuditDetail(auditEvent, "status");
        var mode = ReadAuditDetail(auditEvent, "mode");
        var message = NormalizeAuditValue(ReadAuditDetail(auditEvent, "message"));
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mode))
            parts.Add($"mode={mode}");

        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
            parts.Add($"url={auditEvent.Target}");

        if (!string.IsNullOrWhiteSpace(status))
            parts.Add($"status={status}");

        if (!string.IsNullOrWhiteSpace(message))
            parts.Add(message);

        return parts.Count == 0
            ? $"SearxNG autostart ({auditEvent.Result})"
            : "SearxNG autostart: " + string.Join(" | ", parts);
    }

    private static string BuildLocalNewsRetryAbortedMessage(AuditEvent auditEvent)
    {
        var query = ReadAuditDetail(auditEvent, "query");
        var recency = ReadAuditDetail(auditEvent, "recency");
        var budget = ReadAuditDetail(auditEvent, "budget");
        var limit = ReadAuditDetail(auditEvent, "limit");
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(query))
            parts.Add($"query=\"{query}\"");

        if (!string.IsNullOrWhiteSpace(recency))
            parts.Add($"recency={recency}");

        if (!string.IsNullOrWhiteSpace(budget))
            parts.Add($"budget={budget}");

        if (!string.IsNullOrWhiteSpace(limit))
            parts.Add($"limit={limit}");

        return parts.Count == 0
            ? "Local news retry aborted."
            : "Local news retry aborted: " + string.Join(" | ", parts);
    }

    private static string? ReadAuditDetail(AuditEvent auditEvent, string key)
    {
        if (auditEvent.Details is null ||
            !auditEvent.Details.TryGetValue(key, out var value) ||
            value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement jsonElement => jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => jsonElement.GetRawText()
            },
            _ => value.ToString()
        };
    }

    private static string? NormalizeAuditValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }

    private static string ReadRequiredString(JsonElement root, string propertyName, out string error)
    {
        error = "";
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            error = $"Profile document field '{propertyName}' is required.";
            return "";
        }

        var text = value.GetString()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = $"Profile document field '{propertyName}' is required.";
            return "";
        }

        return text;
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.GetRawText();
    }

    private static List<string> ReadAliases(JsonElement root)
    {
        if (!root.TryGetProperty("aliases", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()?
                .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
                ?? [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Select(alias => alias!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    private static IEnumerable<string> SplitAliases(string? aliases)
    {
        return string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var doc = JsonDocument.Parse(json, EditableDocumentReadOptions);
        return doc.RootElement.Clone();
    }

    private sealed record EditableProfileCardDocument
    {
        [JsonPropertyName("profile_id")]
        public string ProfileId { get; init; } = "";

        [JsonPropertyName("kind")]
        public string Kind { get; init; } = "user";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("relationship")]
        public string? Relationship { get; init; }

        [JsonPropertyName("aliases")]
        public IReadOnlyList<string> Aliases { get; init; } = [];

        [JsonPropertyName("data")]
        public JsonElement Data { get; init; }
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






