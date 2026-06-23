using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Routes for discovering folder paths the assistant *could* be authorized
/// to read. The runtime resolves the user's per-OS Documents / Downloads /
/// Desktop locations and reports their existence so the web onboarding
/// wizard can show realistic, clickable suggestions instead of asking the
/// user to type absolute paths.
/// </summary>
public static class FilesApi
{
    public static IEndpointRouteBuilder MapFilesApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/files/folder-suggestions", () =>
        {
            var suggestions = ResolveSuggestions();
            return Results.Json(
                new FolderSuggestionsResponse(suggestions),
                FilesJsonContext.Default.FolderSuggestionsResponse);
        });

        return app;
    }

    private static IReadOnlyList<FolderSuggestion> ResolveSuggestions()
    {
        // Documents and Desktop have first-class SpecialFolder enum entries.
        // Downloads does not (pre-.NET8 anyway), so we synthesise the
        // conventional location off the user profile. Each suggestion
        // reports `exists` so the UI can grey out folders the OS hasn't
        // actually provisioned (e.g. roaming profiles, mapped drives).
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.Combine(userProfile, "Downloads");

        var list = new List<FolderSuggestion>(3);
        AddIfPathPresent(list, "documents", "Documents", "Personal documents, projects, and notes.", documents, defaultEnabled: true);
        AddIfPathPresent(list, "downloads", "Downloads", "Files you've recently saved from the browser.", downloads, defaultEnabled: false);
        AddIfPathPresent(list, "desktop", "Desktop", "Items currently on your desktop.", desktop, defaultEnabled: false);
        return list;
    }

    private static void AddIfPathPresent(
        List<FolderSuggestion> list,
        string id,
        string label,
        string description,
        string? path,
        bool defaultEnabled)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var exists = SafeDirectoryExists(path);
        list.Add(new FolderSuggestion(id, label, description, path, exists, defaultEnabled));
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch { return false; }
    }
}

/// <summary>One suggested allowed-root folder shown during onboarding.</summary>
public sealed record FolderSuggestion(
    string Id,
    string Label,
    string Description,
    string Path,
    bool Exists,
    bool DefaultEnabled);

/// <summary>Response payload for GET /api/files/folder-suggestions.</summary>
public sealed record FolderSuggestionsResponse(IReadOnlyList<FolderSuggestion> Suggestions);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FolderSuggestion))]
[JsonSerializable(typeof(FolderSuggestionsResponse))]
public partial class FilesJsonContext : JsonSerializerContext
{
}
