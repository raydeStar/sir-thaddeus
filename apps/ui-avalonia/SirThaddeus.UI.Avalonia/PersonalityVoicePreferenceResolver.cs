using SirThaddeus.Config;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.UI.Avalonia;

internal static class PersonalityVoicePreferenceResolver
{
    private const string DefaultKokoroVoiceId = "bm_lewis";

    public static string ResolvePreferredTtsVoiceId(
        AppSettings settings,
        string? activePersonalityId = null,
        string? ttsEngine = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var profilesDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
        var personalityId = string.IsNullOrWhiteSpace(activePersonalityId)
            ? settings.ActivePersonalityId
            : activePersonalityId.Trim();

        if (string.IsNullOrWhiteSpace(profilesDirectory) || string.IsNullOrWhiteSpace(personalityId))
        {
            return ResolveDefault(ttsEngine);
        }

        try
        {
            var store = new PersonalityProfileStore();
            store.EnsureBuiltInsInstalled(profilesDirectory);
            var loaded = store.LoadActive(profilesDirectory, personalityId);

            if (!string.Equals(loaded.Profile.Id, personalityId, StringComparison.OrdinalIgnoreCase))
            {
                return ResolveDefault(ttsEngine);
            }

            var preferred = loaded.Profile.VoicePreferences.GetResolvedPreferredTtsVoiceId();
            return string.IsNullOrWhiteSpace(preferred)
                ? ResolveDefault(ttsEngine)
                : preferred.Trim();
        }
        catch
        {
            return ResolveDefault(ttsEngine);
        }
    }

    private static string ResolveDefault(string? ttsEngine)
    {
        var normalized = (ttsEngine ?? "").Trim();
        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, "kokoro", StringComparison.OrdinalIgnoreCase)
            ? DefaultKokoroVoiceId
            : string.Empty;
    }
}
