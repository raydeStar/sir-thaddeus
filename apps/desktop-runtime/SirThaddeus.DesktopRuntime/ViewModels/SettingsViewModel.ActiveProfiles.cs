using System.Text.Json;
using System.Windows.Input;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.DesktopRuntime.Services;
using SirThaddeus.Memory;

namespace SirThaddeus.DesktopRuntime.ViewModels;

public sealed partial class SettingsViewModel
{
    private const string DefaultJohnProfileId = ActiveProfileBootstrapper.DefaultJohnProfileId;
    private const string DefaultJaneProfileId = ActiveProfileBootstrapper.DefaultJaneProfileId;

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Dictionary<string, ProfileCard> _profilesById =
        new(StringComparer.OrdinalIgnoreCase);

    public ICommand AddActiveProfileCommand { get; private set; } = null!;
    public ICommand EditActiveProfileJsonCommand { get; private set; } = null!;
    public ICommand RefreshActiveProfilesCommand { get; private set; } = null!;

    private void InitializeActiveProfileCommands()
    {
        AddActiveProfileCommand = new AsyncRelayCommand(
            AddActiveProfileAsync,
            () => _store is not null);

        EditActiveProfileJsonCommand = new AsyncRelayCommand(
            EditSelectedActiveProfileJsonAsync,
            () => _store is not null && !string.IsNullOrWhiteSpace(_selectedProfile?.ProfileId));

        RefreshActiveProfilesCommand = new AsyncRelayCommand(
            LoadProfilesAsync,
            () => _store is not null);
    }

    private async Task EnsureDefaultUserProfilesAsync()
    {
        if (_store is null)
            return;

        var existing = await _store.ListProfilesAsync();
        if (existing.Count > 0)
            return;

        var now = DateTimeOffset.UtcNow;
        var defaults = new[]
        {
            new ProfileCard
            {
                ProfileId = DefaultJohnProfileId,
                Kind = "user",
                DisplayName = "John Doe",
                ProfileJson = BuildDefaultProfileJson("John Doe"),
                UpdatedAt = now
            },
            new ProfileCard
            {
                ProfileId = DefaultJaneProfileId,
                Kind = "user",
                DisplayName = "Jane Doe",
                ProfileJson = BuildDefaultProfileJson("Jane Doe"),
                UpdatedAt = now
            }
        };

        foreach (var profile in defaults)
            await _store.StoreProfileAsync(profile);

        _audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "PROFILE_DEFAULTS_CREATED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["profiles"] = "John Doe, Jane Doe"
            }
        });
    }

    private void EnsureActiveProfileSelection(IReadOnlyList<ProfileCard> profiles)
    {
        if (profiles.Count == 0)
            return;

        var activeId = (_settings.ActiveProfileId ?? "").Trim();
        var hasActive = !string.IsNullOrWhiteSpace(activeId) &&
                        profiles.Any(p => string.Equals(p.ProfileId, activeId, StringComparison.OrdinalIgnoreCase));
        if (hasActive)
            return;

        var fallbackId =
            profiles.FirstOrDefault(p => string.Equals(p.ProfileId, DefaultJohnProfileId, StringComparison.OrdinalIgnoreCase))
                ?.ProfileId
            ?? profiles[0].ProfileId;

        _settings = _settings with { ActiveProfileId = fallbackId };
        SettingsManager.Save(_settings);
        ActiveProfileChanged?.Invoke(fallbackId, ResolveProfileDisplayName(fallbackId));
        SettingsChanged?.Invoke(_settings);
    }

    private async Task AddActiveProfileAsync()
    {
        if (_store is null)
        {
            StatusText = "Enable memory to manage active profiles.";
            return;
        }

        await _store.EnsureSchemaAsync();
        var profiles = await _store.ListProfilesAsync();
        var nextDisplayName = GetNextProfileDisplayName(profiles);
        var profileId = BuildUniqueProfileId(
            nextDisplayName,
            profiles.Select(p => p.ProfileId));

        var profile = new ProfileCard
        {
            ProfileId = profileId,
            Kind = "user",
            DisplayName = nextDisplayName,
            ProfileJson = BuildDefaultProfileJson(nextDisplayName),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.StoreProfileAsync(profile);
        await LoadProfilesAsync();

        SelectedProfile = AvailableProfiles.FirstOrDefault(p =>
            string.Equals(p.ProfileId, profile.ProfileId, StringComparison.OrdinalIgnoreCase));

        StatusText = $"Created profile '{profile.DisplayName}'.";
        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PROFILE_CREATED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["profileId"] = profile.ProfileId,
                ["displayName"] = profile.DisplayName,
                ["source"] = "settings"
            }
        });
    }

    private async Task EditSelectedActiveProfileJsonAsync()
    {
        if (_store is null)
        {
            StatusText = "Enable memory to edit active profiles.";
            return;
        }

        var profileId = _selectedProfile?.ProfileId;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            StatusText = "Select a profile first.";
            return;
        }

        if (!_profilesById.TryGetValue(profileId, out var profile))
        {
            await LoadProfilesAsync();
            if (!_profilesById.TryGetValue(profileId, out profile))
            {
                StatusText = "Selected profile could not be loaded.";
                return;
            }
        }

        var editor = new ProfileJsonEditorWindow(
            profile.DisplayName,
            FormatJsonForEditor(profile.ProfileJson))
        {
            Owner = System.Windows.Application.Current?.MainWindow
        };

        if (editor.ShowDialog() != true)
            return;

        var updatedDisplayName = string.IsNullOrWhiteSpace(editor.DisplayNameValue)
            ? profile.DisplayName
            : editor.DisplayNameValue.Trim();
        var updatedJson = NormalizeJsonForStorage(editor.ProfileJsonValue);

        var updated = profile with
        {
            DisplayName = updatedDisplayName,
            ProfileJson = updatedJson,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _store.StoreProfileAsync(updated);
        await LoadProfilesAsync();
        SelectedProfile = AvailableProfiles.FirstOrDefault(p =>
            string.Equals(p.ProfileId, updated.ProfileId, StringComparison.OrdinalIgnoreCase));

        StatusText = $"Updated profile '{updated.DisplayName}'.";
        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = "PROFILE_EDITED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["profileId"] = updated.ProfileId,
                ["displayName"] = updated.DisplayName,
                ["source"] = "settings"
            }
        });
    }

    private string? ResolveProfileDisplayName(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return null;

        if (!_profilesById.TryGetValue(profileId, out var card))
            return null;

        // Prefer preferred_name from profile JSON, fall back to DisplayName
        if (!string.IsNullOrWhiteSpace(card.ProfileJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(card.ProfileJson);
                if (doc.RootElement.TryGetProperty("preferred_name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    var preferred = nameEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(preferred))
                        return preferred;
                }
            }
            catch { /* malformed JSON — fall through */ }
        }

        return string.IsNullOrWhiteSpace(card.DisplayName) ? null : card.DisplayName;
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
        }, ProfileJsonOptions);
    }

    private static string FormatJsonForEditor(string profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
            return "{}";

        try
        {
            return NormalizeJsonForStorage(profileJson);
        }
        catch
        {
            return profileJson;
        }
    }

    private static string NormalizeJsonForStorage(string profileJson)
    {
        var raw = string.IsNullOrWhiteSpace(profileJson) ? "{}" : profileJson.Trim();
        using var json = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(json.RootElement, ProfileJsonOptions);
    }

    private static string GetNextProfileDisplayName(IReadOnlyList<ProfileCard> profiles)
    {
        var existing = new HashSet<string>(
            profiles.Select(p => p.DisplayName.Trim()),
            StringComparer.OrdinalIgnoreCase);

        if (!existing.Contains("New User"))
            return "New User";

        var suffix = 2;
        while (true)
        {
            var candidate = $"New User {suffix}";
            if (!existing.Contains(candidate))
                return candidate;
            suffix++;
        }
    }

    private static string BuildUniqueProfileId(string displayName, IEnumerable<string> existingIds)
    {
        var cleaned = new string((displayName ?? "")
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        cleaned = cleaned.Trim('-');
        if (string.IsNullOrWhiteSpace(cleaned))
            cleaned = "new-user";

        var id = $"user-{cleaned}";
        var taken = new HashSet<string>(existingIds, StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(id))
            return id;

        var suffix = 2;
        while (true)
        {
            var candidate = $"{id}-{suffix}";
            if (!taken.Contains(candidate))
                return candidate;
            suffix++;
        }
    }
}
