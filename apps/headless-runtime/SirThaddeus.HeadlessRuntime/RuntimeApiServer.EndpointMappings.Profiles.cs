using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.PersonalityEngine.Profiles;

internal static partial class RuntimeApiServer
{
    private static void MapProfileEndpoints(
        WebApplication app,
        Func<AppSettings> getSettings,
        Action<AppSettings> persistSettings)
    {
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
            persistSettings(updatedSettings);

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
                persistSettings(currentSettings with { ActiveProfileId = nextActiveProfileId });
            }

            var response = new DeleteProfileResponse(
                Applied: true,
                ActiveProfileId: nextActiveProfileId,
                Message: string.Equals(currentSettings.ActiveProfileId, requestedProfileId, StringComparison.OrdinalIgnoreCase)
                    ? $"Deleted profile '{requestedProfileId}'. Active profile is now '{nextActiveProfileId ?? "(none)"}'."
                    : $"Deleted profile '{requestedProfileId}'.");

            return Results.Json(response, JsonOptions);
        });
    }
}
