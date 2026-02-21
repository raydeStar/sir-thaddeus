using System.Text.Json.Serialization;

namespace SirThaddeus.PersonalityEngine.Profiles;

/// <summary>
/// Declarative, deterministic personality profile.
/// </summary>
public sealed record PersonalityProfile
{
    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.0";

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("tone")]
    public PersonalityTone Tone { get; init; } = new();

    [JsonPropertyName("behavior_rules")]
    public PersonalityBehaviorRules BehaviorRules { get; init; } = new();

    [JsonPropertyName("epistemic_rules")]
    public PersonalityEpistemicRules EpistemicRules { get; init; } = new();

    [JsonPropertyName("speech_patterns")]
    public PersonalitySpeechPatterns SpeechPatterns { get; init; } = new();

    [JsonPropertyName("capability_constraints")]
    public PersonalityCapabilityConstraints CapabilityConstraints { get; init; } = new();

    [JsonPropertyName("instructions")]
    public PersonalityInstructions Instructions { get; init; } = new();

    [JsonPropertyName("context_modifiers")]
    public PersonalityContextModifiers ContextModifiers { get; init; } = new();

    [JsonPropertyName("voice_preferences")]
    public PersonalityVoicePreferences VoicePreferences { get; init; } = new();

    [JsonPropertyName("reduction_rules")]
    public PersonalityReductionRules ReductionRules { get; init; } = new();

    [JsonPropertyName("identity")]
    public PersonalityIdentity Identity { get; init; } = new();
}

public sealed record PersonalityTone
{
    [JsonPropertyName("formality")]
    public double Formality { get; init; } = 0.6;

    [JsonPropertyName("warmth")]
    public double Warmth { get; init; } = 0.6;

    [JsonPropertyName("humor")]
    public double Humor { get; init; } = 0.25;

    [JsonPropertyName("verbosity")]
    public double Verbosity { get; init; } = 0.55;

    [JsonPropertyName("directness")]
    public double Directness { get; init; } = 0.8;
}

public sealed record PersonalityBehaviorRules
{
    [JsonPropertyName("pushback_on_illogic")]
    public bool PushbackOnIllogic { get; init; } = true;

    [JsonPropertyName("avoid_flattery")]
    public bool AvoidFlattery { get; init; } = true;

    [JsonPropertyName("never_override_permissions")]
    public bool NeverOverridePermissions { get; init; } = true;
}

public sealed record PersonalityEpistemicRules
{
    [JsonPropertyName("never_invent_capabilities")]
    public bool NeverInventCapabilities { get; init; } = true;

    [JsonPropertyName("admit_uncertainty_explicitly")]
    public bool AdmitUncertaintyExplicitly { get; init; } = true;

    [JsonPropertyName("ask_minimum_questions")]
    public bool AskMinimumQuestions { get; init; } = true;
}

public sealed record PersonalitySpeechPatterns
{
    [JsonPropertyName("include_signature_note")]
    public bool IncludeSignatureNote { get; init; }

    [JsonPropertyName("avoid_modern_slang")]
    public bool AvoidModernSlang { get; init; } = true;
}

public sealed record PersonalityCapabilityConstraints
{
    [JsonPropertyName("max_metaphor_density")]
    public double MaxMetaphorDensity { get; init; } = 0.3;
}

public sealed record PersonalityInstructions
{
    [JsonPropertyName("core_identity")]
    public string CoreIdentity { get; init; } = "";

    [JsonPropertyName("response_priority_order")]
    public IReadOnlyList<string> ResponsePriorityOrder { get; init; } = [];

    [JsonPropertyName("conflict_resolution")]
    public IReadOnlyList<string> ConflictResolution { get; init; } = [];

    [JsonPropertyName("failure_behavior")]
    public IReadOnlyList<string> FailureBehavior { get; init; } = [];

    [JsonPropertyName("style_rules")]
    public IReadOnlyList<string> StyleRules { get; init; } = [];

    [JsonPropertyName("few_shot_examples")]
    public IReadOnlyList<PersonalityFewShotExample> FewShotExamples { get; init; } = [];
}

public sealed record PersonalityFewShotExample
{
    [JsonPropertyName("user")]
    public string User { get; init; } = "";

    [JsonPropertyName("assistant")]
    public string Assistant { get; init; } = "";
}

public sealed record PersonalityContextModifiers
{
    [JsonPropertyName("emotional_user")]
    public PersonalityContextModifier EmotionalUser { get; init; } = new();

    [JsonPropertyName("technical_mode")]
    public PersonalityContextModifier TechnicalMode { get; init; } = new();

    [JsonPropertyName("brainstorming")]
    public PersonalityContextModifier Brainstorming { get; init; } = new();

    [JsonPropertyName("boundary_testing")]
    public PersonalityContextModifier BoundaryTesting { get; init; } = new();
}

public sealed record PersonalityContextModifier
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

    // Backward/forward compatible aliases for metaphor tuning.
    [JsonPropertyName("metaphor_density")]
    public double MetaphorDensityDelta { get; init; }

    [JsonPropertyName("max_metaphor_density_delta")]
    public double MaxMetaphorDensityDelta { get; init; }

    [JsonIgnore]
    public double ResolvedMetaphorDensityDelta => MetaphorDensityDelta + MaxMetaphorDensityDelta;
}

public sealed record PersonalityVoicePreferences
{
    [JsonPropertyName("preferred_tts_voice_id")]
    public string PreferredTtsVoiceId { get; init; } = "";

    public string GetResolvedPreferredTtsVoiceId() =>
        string.IsNullOrWhiteSpace(PreferredTtsVoiceId)
            ? ""
            : PreferredTtsVoiceId.Trim();
}

public sealed record PersonalityReductionRules
{
    /// <summary>
    /// Opt-in reduction pass. Disabled by default for semantic safety.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "";

    /// <summary>
    /// If true, remove exact duplicate paragraphs only.
    /// </summary>
    [JsonPropertyName("collapse_exact_duplicates")]
    public bool CollapseExactDuplicates { get; init; } = true;

    /// <summary>
    /// If true, trim known trailing fluff phrases.
    /// </summary>
    [JsonPropertyName("trim_trailing_fluff")]
    public bool TrimTrailingFluff { get; init; } = true;

    [JsonPropertyName("adaptive")]
    public PersonalityAdaptiveReduction Adaptive { get; init; } = new();
}

public sealed record PersonalityAdaptiveReduction
{
    [JsonPropertyName("simple_query_max_chars")]
    public int SimpleQueryMaxChars { get; init; } = 900;

    [JsonPropertyName("complex_query_min_chars")]
    public int ComplexQueryMinChars { get; init; } = 700;

    [JsonPropertyName("prefer_short_if_user_asked_simple")]
    public bool PreferShortIfUserAskedSimple { get; init; } = true;
}

public sealed record PersonalityIdentity
{
    /// <summary>
    /// The name the personality uses when identifying itself (e.g. "Sir Thaddeus").
    /// Falls back to <see cref="PersonalityProfile.DisplayName"/> when empty.
    /// </summary>
    [JsonPropertyName("self_name")]
    public string SelfName { get; init; } = "";

    /// <summary>
    /// First-person self-characterization injected into the system prompt.
    /// Richer than the short UI-facing <see cref="PersonalityProfile.Description"/>.
    /// </summary>
    [JsonPropertyName("self_description")]
    public string SelfDescription { get; init; } = "";
}

public sealed record PersonalityProfileDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Hash { get; init; }
    public required string SourcePath { get; init; }
}
