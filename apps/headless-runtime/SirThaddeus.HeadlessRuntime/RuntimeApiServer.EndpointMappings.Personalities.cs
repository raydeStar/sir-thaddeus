using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.PersonalityEngine.Profiles;

internal static partial class RuntimeApiServer
{
    private static void MapPersonalityEndpoints(
        WebApplication app,
        Func<AppSettings> getSettings,
        Action<AppSettings> persistSettings)
    {
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
            persistSettings(updatedSettings);

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
                persistSettings(currentSettings with { ActivePersonalityId = nextActivePersonalityId });
            }

            var response = new DeletePersonalityResponse(
                Applied: true,
                ActivePersonalityId: nextActivePersonalityId,
                Message: string.Equals(currentSettings.ActivePersonalityId, requestedPersonalityId, StringComparison.OrdinalIgnoreCase)
                    ? $"Deleted personality '{requestedPersonalityId}'. Active personality is now '{nextActivePersonalityId}'."
                    : $"Deleted personality '{requestedPersonalityId}'.");

            return Results.Json(response, JsonOptions);
        });
    }
}
