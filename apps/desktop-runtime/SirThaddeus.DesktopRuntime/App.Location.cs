using System.Windows;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.DesktopRuntime.Services;

namespace SirThaddeus.DesktopRuntime;

public partial class App
{
    private readonly LocationPolicy _locationPolicy = LocationPolicy.ManualOnly;
    private IDeviceLocationProvider? _deviceLocationProvider;
    private int _locationPromptInFlight;

    private void InitializeLocationServices()
    {
        _deviceLocationProvider = DeviceLocationProviderRegistry.CreateDefault(_locationPolicy);

        if (_deviceLocationProvider is WindowsDeviceLocationProvider)
        {
            throw new InvalidOperationException(
                "WindowsDeviceLocationProvider must not be registered in this build.");
        }
    }

    private void QueueManualLocationPromptFromConversation()
    {
        if (_isHeadless || !_locationPolicy.AllowManualLocation)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (Interlocked.Exchange(ref _locationPromptInFlight, 1) == 1)
                return;

            try
            {
                ShowManualLocationPromptForActiveProfile();
            }
            finally
            {
                Interlocked.Exchange(ref _locationPromptInFlight, 0);
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ShowManualLocationPromptForActiveProfile()
    {
        if (_settings is null)
            return;

        var activeProfileId = _orchestrator?.ActiveProfileId;
        var existing = _settings.GetEffectiveUserLocation(activeProfileId).GetResolvedLabel();
        if (!string.IsNullOrWhiteSpace(existing))
            return;

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "LOCATION_PROMPT_SHOWN",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["source"] = "conversation",
                ["profileId"] = activeProfileId ?? ""
            }
        });

        var prompt = new LocationPromptWindow
        {
            Owner = _mainWindow?.IsVisible == true
                ? _mainWindow
                : null
        };

        var accepted = prompt.ShowDialog() == true &&
                       !string.IsNullOrWhiteSpace(prompt.ManualLocationValue);
        if (!accepted)
            return; // Opt-out is valid; do not persist anything.

        var updated = SaveManualLocationFromConversation(prompt.ManualLocationValue);
        if (updated is null)
            return;

        _settings = updated;
        _permissionGate?.UpdateSettings(updated);
        ApplyManualLocationToOrchestrator(updated, emitAuditEvent: true);
    }

    private static AppSettings PersistManualLocation(
        AppSettings settings,
        string? profileId,
        string value,
        string mode,
        bool persistUnset = true)
    {
        var normalizedMode = string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase)
            ? "manual"
            : "unset";
        var normalizedValue = (value ?? "").Trim();
        if (normalizedMode != "manual")
            normalizedValue = "";

        var nowIso = DateTimeOffset.UtcNow.ToString("O");
        var profileKey = AppSettings.NormalizeLocationProfileKey(profileId);
        var nextLocation = settings.GetEffectiveUserLocation(profileId) with
        {
            Mode = normalizedMode,
            Value = normalizedValue,
            UpdatedAt = nowIso,

            // Keep legacy fields mirrored for backward compatibility.
            Enabled = normalizedMode == "manual",
            Label = normalizedValue,
            Timezone = "",
            Latitude = null,
            Longitude = null
        };

        var scopedLocations = new Dictionary<string, LocationSettings>(
            settings.UserProfile.LocationsByProfile,
            StringComparer.OrdinalIgnoreCase);
        if (normalizedMode == "unset" && !persistUnset)
            scopedLocations.Remove(profileKey);
        else
            scopedLocations[profileKey] = nextLocation;

        var updated = settings with
        {
            UserProfile = settings.UserProfile with
            {
                Location = nextLocation,
                LocationsByProfile = scopedLocations
            },
            Location = nextLocation
        };

        SettingsManager.Save(updated);
        return updated;
    }

    private AppSettings? SaveManualLocationFromConversation(string manualLocation)
    {
        if (_settings is null)
            return null;

        var normalized = (manualLocation ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return _settings;

        var activeProfileId = _orchestrator?.ActiveProfileId;
        var updated = PersistManualLocation(_settings, activeProfileId, normalized, "manual");
        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "LOCATION_MANUAL_SAVED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["mode"] = "manual",
                ["value"] = normalized,
                ["source"] = "conversation",
                ["profileId"] = activeProfileId ?? ""
            }
        });

        return updated;
    }

    private AppSettings? TouchManualLocationTimestampFromConversation()
    {
        if (_settings is null)
            return null;

        var activeProfileId = _orchestrator?.ActiveProfileId;
        var current = _settings.GetEffectiveUserLocation(activeProfileId).GetResolvedLabel();
        if (string.IsNullOrWhiteSpace(current))
            return _settings;

        var updated = PersistManualLocation(_settings, activeProfileId, current, "manual");
        _auditLogger?.Append(new AuditEvent
        {
            Actor = "user",
            Action = "LOCATION_MANUAL_SAVED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["mode"] = "manual",
                ["value"] = current,
                ["source"] = "conversation_confirmed",
                ["profileId"] = activeProfileId ?? ""
            }
        });

        return updated;
    }

    private void ApplyManualLocationToOrchestrator(AppSettings settings, bool emitAuditEvent)
    {
        if (_orchestrator is null)
            return;

        var activeProfileId = _orchestrator.ActiveProfileId;
        var location = settings.GetEffectiveUserLocation(activeProfileId);
        var manualLocation = location.GetResolvedLabel();
        if (string.IsNullOrWhiteSpace(manualLocation))
        {
            _orchestrator.UserLocationHint = null;
            _orchestrator.UserTimezone = null;
            _orchestrator.PreferredUnits = settings.Weather.GetNormalizedUnitSystem();
            return;
        }

        _orchestrator.UserLocationHint = manualLocation;
        _orchestrator.UserTimezone = location.GetResolvedTimezone();
        _orchestrator.PreferredUnits = settings.Weather.GetNormalizedUnitSystem();

        if (!emitAuditEvent)
            return;

        _auditLogger?.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "LOCATION_USED_MANUAL",
            Result = $"Using manual location: {manualLocation}",
            Details = new Dictionary<string, object>
            {
                ["mode"] = "manual",
                ["value"] = manualLocation,
                ["profileId"] = activeProfileId ?? ""
            }
        });
    }
}
