using SirThaddeus.Config;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.DesktopRuntime.Services;

public sealed record PersonalityVoicePreferenceResult
{
    public required AppSettings Settings { get; init; }
    public string AppliedVoiceId { get; init; } = "";
    public bool Applied => !string.IsNullOrWhiteSpace(AppliedVoiceId);
}

public static class PersonalityVoicePreferenceResolver
{
    private static readonly IReadOnlyDictionary<string, string> BuiltInVoiceDefaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [BuiltInProfileCatalog.HelpfulDefaultId] = "af_sarah",
            [BuiltInProfileCatalog.ProfessionalId] = "bm_george",
            [BuiltInProfileCatalog.SirThaddeusId] = "am_adam"
        };

    public static string ResolvePreferredTtsVoiceId(
        PersonalityProfileStore store,
        string profilesDirectory,
        string? activePersonalityId)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(profilesDirectory) ||
            string.IsNullOrWhiteSpace(activePersonalityId))
        {
            return "";
        }

        try
        {
            var requestedId = activePersonalityId.Trim();
            var loaded = store.LoadActive(profilesDirectory, requestedId);

            // Only auto-apply from the actually selected profile. If it fell
            // back to default due validation/missing file, do not surprise users.
            if (!string.Equals(loaded.Profile.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                return "";

            var preferred = loaded.Profile.VoicePreferences.GetResolvedPreferredTtsVoiceId();
            if (!string.IsNullOrWhiteSpace(preferred))
                return preferred;

            return BuiltInVoiceDefaults.TryGetValue(requestedId, out var fallbackVoiceId)
                ? fallbackVoiceId
                : "";
        }
        catch
        {
            return "";
        }
    }

    public static PersonalityVoicePreferenceResult ApplyPreferredTtsVoiceIfMissing(
        AppSettings settings,
        PersonalityProfileStore store,
        string profilesDirectory,
        string? activePersonalityId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        if (!string.IsNullOrWhiteSpace(settings.Voice.GetResolvedTtsVoiceId()))
        {
            return new PersonalityVoicePreferenceResult
            {
                Settings = settings
            };
        }

        var preferredVoiceId = ResolvePreferredTtsVoiceId(
            store,
            profilesDirectory,
            activePersonalityId);

        if (string.IsNullOrWhiteSpace(preferredVoiceId))
        {
            return new PersonalityVoicePreferenceResult
            {
                Settings = settings
            };
        }

        return new PersonalityVoicePreferenceResult
        {
            Settings = settings with
            {
                Voice = settings.Voice with
                {
                    TtsVoiceId = preferredVoiceId
                }
            },
            AppliedVoiceId = preferredVoiceId
        };
    }
}
