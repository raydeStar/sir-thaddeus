using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;

namespace SirThaddeus.DesktopRuntime.Services;

internal static class ActiveProfileBootstrapper
{
    internal const string DefaultJohnProfileId = "user-john-doe";
    internal const string DefaultJaneProfileId = "user-jane-doe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal static async Task<BootstrapResult> EnsureInitializedAsync(
        AppSettings settings,
        string memoryDbPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryDbPath);

        using var store = new SqliteMemoryStore(memoryDbPath);
        await store.EnsureSchemaAsync(cancellationToken);

        var profiles = await store.ListProfilesAsync(cancellationToken);
        var createdDefaults = false;

        if (profiles.Count == 0)
        {
            createdDefaults = true;
            var now = DateTimeOffset.UtcNow;

            await store.StoreProfileAsync(new ProfileCard
            {
                ProfileId = DefaultJohnProfileId,
                Kind = "user",
                DisplayName = "John Doe",
                ProfileJson = BuildDefaultProfileJson("John Doe"),
                UpdatedAt = now
            }, cancellationToken);

            await store.StoreProfileAsync(new ProfileCard
            {
                ProfileId = DefaultJaneProfileId,
                Kind = "user",
                DisplayName = "Jane Doe",
                ProfileJson = BuildDefaultProfileJson("Jane Doe"),
                UpdatedAt = now
            }, cancellationToken);

            profiles = await store.ListProfilesAsync(cancellationToken);
        }

        if (profiles.Count == 0)
            return new BootstrapResult(settings, createdDefaults, false);

        var activeId = (settings.ActiveProfileId ?? "").Trim();
        var hasValidActive = !string.IsNullOrWhiteSpace(activeId) &&
                             profiles.Any(p => string.Equals(p.ProfileId, activeId, StringComparison.OrdinalIgnoreCase));
        if (hasValidActive)
            return new BootstrapResult(settings, createdDefaults, false);

        var fallbackId =
            profiles.FirstOrDefault(p => string.Equals(p.ProfileId, DefaultJohnProfileId, StringComparison.OrdinalIgnoreCase))
                ?.ProfileId
            ?? profiles[0].ProfileId;

        var updatedSettings = settings with { ActiveProfileId = fallbackId };
        return new BootstrapResult(updatedSettings, createdDefaults, true);
    }

    private static string BuildDefaultProfileJson(string displayName)
    {
        var preferredName = (displayName ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";

        return JsonSerializer.Serialize(new
        {
            preferred_name = preferredName,
            pronouns = "",
            timezone = "",
            style = "",
            notes = ""
        }, JsonOptions);
    }
}

internal readonly record struct BootstrapResult(
    AppSettings Settings,
    bool CreatedDefaults,
    bool AssignedActiveProfile);
