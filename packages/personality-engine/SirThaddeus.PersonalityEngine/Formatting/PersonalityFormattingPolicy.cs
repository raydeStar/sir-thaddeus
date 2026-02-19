using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.PersonalityEngine.Formatting;

public static class PersonalityFormattingPolicy
{
    public static PresentationFormatOptions BuildPresentationOptions(PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new PresentationFormatOptions
        {
            IncludeSignatureNote = profile.SpeechPatterns.IncludeSignatureNote,
            SignatureText = profile.SpeechPatterns.IncludeSignatureNote
                ? $"-- {profile.DisplayName}"
                : ""
        };
    }

    public static ReductionFormatOptions BuildReductionOptions(
        PersonalityProfile profile,
        string? latestUserMessage = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var mode = (profile.ReductionRules.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is not "adaptive" and not "always" and not "never")
            mode = profile.ReductionRules.Enabled ? "always" : "never";

        return new ReductionFormatOptions
        {
            Enabled = profile.ReductionRules.Enabled,
            Mode = mode,
            CollapseExactDuplicates = profile.ReductionRules.CollapseExactDuplicates,
            TrimTrailingFluff = profile.ReductionRules.TrimTrailingFluff,
            SimpleQueryMaxChars = profile.ReductionRules.Adaptive.SimpleQueryMaxChars,
            ComplexQueryMinChars = profile.ReductionRules.Adaptive.ComplexQueryMinChars,
            PreferShortIfUserAskedSimple = profile.ReductionRules.Adaptive.PreferShortIfUserAskedSimple,
            LatestUserMessage = latestUserMessage ?? ""
        };
    }
}
