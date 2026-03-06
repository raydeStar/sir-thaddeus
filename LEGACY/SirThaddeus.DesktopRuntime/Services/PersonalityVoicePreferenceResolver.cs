using SirThaddeus.Config;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.DesktopRuntime.Services;

public sealed record PersonalityVoicePreferenceResult
{
    public required AppSettings Settings { get; init; }
    public string AppliedVoiceId { get; init; } = "";
    public string AppliedSource { get; init; } = "";
    public bool Applied => !string.IsNullOrWhiteSpace(AppliedVoiceId);
}

public static class PersonalityVoicePreferenceResolver
{
    private const string DefaultVoiceId = "bm_lewis";

    private static (string VoiceId, string Source) ResolvePreferredTtsVoice(
        PersonalityProfileStore store,
        string profilesDirectory,
        string? activePersonalityId,
        string? ttsEngine = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(profilesDirectory) ||
            string.IsNullOrWhiteSpace(activePersonalityId))
        {
            return ("", "");
        }

        try
        {
            var requestedId = activePersonalityId.Trim();
            var loaded = store.LoadActive(profilesDirectory, requestedId);

            // Only auto-apply from the actually selected profile. If it fell
            // back to default due validation/missing file, do not surprise users.
            if (!string.Equals(loaded.Profile.Id, requestedId, StringComparison.OrdinalIgnoreCase))
                return ("", "");

            var preferred = loaded.Profile.VoicePreferences.GetResolvedPreferredTtsVoiceId();
            if (!string.IsNullOrWhiteSpace(preferred))
                return (preferred, "profile_preference");

            // Use a stable global fallback voice so personality switches do not
            // unexpectedly flip TTS voice ids when users leave voice blank.
            // Only return the Kokoro default when the engine is actually Kokoro;
            // for other engines let downstream engine-specific defaults apply.
            var effectiveEngine = (ttsEngine ?? "").Trim();
            if (string.IsNullOrWhiteSpace(effectiveEngine) ||
                string.Equals(effectiveEngine, "kokoro", StringComparison.OrdinalIgnoreCase))
            {
                return (DefaultVoiceId, "global_default");
            }

            return ("", "");
        }
        catch
        {
            return ("", "");
        }
    }

    public static string ResolvePreferredTtsVoiceId(
        PersonalityProfileStore store,
        string profilesDirectory,
        string? activePersonalityId,
        string? ttsEngine = null)
        => ResolvePreferredTtsVoice(store, profilesDirectory, activePersonalityId, ttsEngine).VoiceId;

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

        var preferredVoice = ResolvePreferredTtsVoice(
            store,
            profilesDirectory,
            activePersonalityId,
            settings.Voice.GetNormalizedTtsEngine());

        if (string.IsNullOrWhiteSpace(preferredVoice.VoiceId))
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
                    TtsVoiceId = preferredVoice.VoiceId
                }
            },
            AppliedVoiceId = preferredVoice.VoiceId,
            AppliedSource = preferredVoice.Source
        };
    }
}
