using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.PersonalityEngine.Profiles;

/// <summary>
/// Produces concise personality templates for user-facing editing.
/// </summary>
public static class PersonalityProfileTemplateFactory
{
    public static PersonalityProfile CreateAverageTemplate(string profileId)
    {
        var normalizedId = (profileId ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedId))
            throw new ArgumentException("Profile id is required.", nameof(profileId));

        const string coreIdentity =
            "Witty, pragmatic butler-mentor. Calm, truthful, pushy with illogic, never flattering.";

        return new PersonalityProfile
        {
            Version = "1.0",
            Id = normalizedId,
            DisplayName = "New Personality",
            Alias = normalizedId.Replace('_', '-'),
            Description = coreIdentity,
            Identity = new PersonalityIdentity
            {
                SelfName = "Sir Thaddeus",
                SelfDescription = coreIdentity
            },
            Instructions = new PersonalityInstructions
            {
                CoreIdentity = coreIdentity,
                ResponsePriorityOrder = [],
                ConflictResolution = [],
                FailureBehavior = [],
                StyleRules = []
            },
            Tone = new PersonalityTone
            {
                Formality = 0.70,
                Warmth = 0.70,
                Humor = 0.50,
                Verbosity = 0.60,
                Directness = 0.82
            },
            BehaviorRules = new PersonalityBehaviorRules
            {
                PushbackOnIllogic = true,
                AvoidFlattery = true,
                NeverOverridePermissions = true
            },
            SpeechPatterns = new PersonalitySpeechPatterns
            {
                IncludeSignatureNote = true,
                AvoidModernSlang = true
            },
            EpistemicRules = new PersonalityEpistemicRules
            {
                NeverInventCapabilities = true,
                AdmitUncertaintyExplicitly = true,
                AskMinimumQuestions = true
            },
            CapabilityConstraints = new PersonalityCapabilityConstraints
            {
                MaxMetaphorDensity = 0.28
            },
            ReductionRules = new PersonalityReductionRules
            {
                Enabled = false,
                Mode = "adaptive",
                CollapseExactDuplicates = true,
                TrimTrailingFluff = true
            }
        };
    }

    public static string RenderMinimalTemplateJson(PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var document = new MinimalProfileDocument
        {
            Version = string.IsNullOrWhiteSpace(profile.Version) ? "1.0" : profile.Version.Trim(),
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            Alias = profile.Alias,
            Identity = new MinimalIdentity
            {
                SelfName = ResolveSelfName(profile),
                CoreIdentity = ResolveCoreIdentity(profile)
            },
            Tone = new MinimalTone
            {
                Formality = profile.Tone.Formality,
                Warmth = profile.Tone.Warmth,
                Humor = profile.Tone.Humor,
                Verbosity = profile.Tone.Verbosity,
                Directness = profile.Tone.Directness
            },
            Toggles = new MinimalToggles
            {
                PushbackOnIllogic = profile.BehaviorRules.PushbackOnIllogic,
                AvoidFlattery = profile.BehaviorRules.AvoidFlattery,
                IncludeSignatureNote = profile.SpeechPatterns.IncludeSignatureNote,
                AvoidModernSlang = profile.SpeechPatterns.AvoidModernSlang,
                EpistemicStrict = ResolveEpistemicStrict(profile),
                PermissionsStrict = profile.BehaviorRules.NeverOverridePermissions
            },
            Advanced = new MinimalAdvanced
            {
                MaxMetaphorDensity = profile.CapabilityConstraints.MaxMetaphorDensity,
                ReductionMode = ResolveReductionMode(profile.ReductionRules)
            }
        };

        return JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string ResolveSelfName(PersonalityProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Identity.SelfName))
            return profile.Identity.SelfName;
        if (!string.IsNullOrWhiteSpace(profile.DisplayName))
            return profile.DisplayName;
        return "Assistant";
    }

    private static string ResolveCoreIdentity(PersonalityProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Instructions.CoreIdentity))
            return profile.Instructions.CoreIdentity;
        if (!string.IsNullOrWhiteSpace(profile.Identity.SelfDescription))
            return profile.Identity.SelfDescription;
        return profile.Description;
    }

    private static bool ResolveEpistemicStrict(PersonalityProfile profile) =>
        profile.EpistemicRules.NeverInventCapabilities &&
        profile.EpistemicRules.AdmitUncertaintyExplicitly &&
        profile.EpistemicRules.AskMinimumQuestions;

    private static string ResolveReductionMode(PersonalityReductionRules reductionRules)
    {
        var mode = (reductionRules.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is "adaptive" or "always" or "never")
            return mode;

        return reductionRules.Enabled ? "always" : "adaptive";
    }

    private sealed record MinimalProfileDocument
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = "1.0";

        [JsonPropertyName("id")]
        public string Id { get; init; } = "";

        [JsonPropertyName("display_name")]
        public string DisplayName { get; init; } = "";

        [JsonPropertyName("alias")]
        public string Alias { get; init; } = "";

        [JsonPropertyName("identity")]
        public MinimalIdentity Identity { get; init; } = new();

        [JsonPropertyName("tone")]
        public MinimalTone Tone { get; init; } = new();

        [JsonPropertyName("toggles")]
        public MinimalToggles Toggles { get; init; } = new();

        [JsonPropertyName("advanced")]
        public MinimalAdvanced Advanced { get; init; } = new();
    }

    private sealed record MinimalIdentity
    {
        [JsonPropertyName("self_name")]
        public string SelfName { get; init; } = "";

        [JsonPropertyName("core_identity")]
        public string CoreIdentity { get; init; } = "";
    }

    private sealed record MinimalTone
    {
        [JsonPropertyName("formality")]
        public double Formality { get; init; }

        [JsonPropertyName("warmth")]
        public double Warmth { get; init; }

        [JsonPropertyName("humor")]
        public double Humor { get; init; }

        [JsonPropertyName("verbosity")]
        public double Verbosity { get; init; }

        [JsonPropertyName("directness")]
        public double Directness { get; init; }
    }

    private sealed record MinimalToggles
    {
        [JsonPropertyName("pushback_on_illogic")]
        public bool PushbackOnIllogic { get; init; }

        [JsonPropertyName("avoid_flattery")]
        public bool AvoidFlattery { get; init; }

        [JsonPropertyName("include_signature_note")]
        public bool IncludeSignatureNote { get; init; }

        [JsonPropertyName("avoid_modern_slang")]
        public bool AvoidModernSlang { get; init; }

        [JsonPropertyName("epistemic_strict")]
        public bool EpistemicStrict { get; init; }

        [JsonPropertyName("permissions_strict")]
        public bool PermissionsStrict { get; init; }
    }

    private sealed record MinimalAdvanced
    {
        [JsonPropertyName("max_metaphor_density")]
        public double MaxMetaphorDensity { get; init; }

        [JsonPropertyName("reduction_mode")]
        public string ReductionMode { get; init; } = "adaptive";
    }
}
