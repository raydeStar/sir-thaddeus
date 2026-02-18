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

    public static ReductionFormatOptions BuildReductionOptions(PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ReductionFormatOptions
        {
            Enabled = profile.ReductionRules.Enabled,
            CollapseExactDuplicates = profile.ReductionRules.CollapseExactDuplicates,
            TrimTrailingFluff = profile.ReductionRules.TrimTrailingFluff
        };
    }
}
