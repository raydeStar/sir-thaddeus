using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.PersonalityEngine.Profiles;

public sealed partial class PersonalityProfileValidator
{
    private static readonly StringComparer Cmp = StringComparer.Ordinal;
    private static readonly string[] SupportedVersions = ["1.0", "1.5"];

    private static readonly HashSet<string> RootProperties =
    [
        "version",
        "id",
        "display_name",
        "alias",
        "description",
        "identity",
        "tone",
        "toggles",
        "advanced",
        "behavior_rules",
        "epistemic_rules",
        "speech_patterns",
        "capability_constraints",
        "instructions",
        "context_modifiers",
        "voice_preferences",
        "reduction_rules"
    ];

    private static readonly HashSet<string> ToneProperties =
    [
        "formality",
        "warmth",
        "humor",
        "verbosity",
        "directness"
    ];

    private static readonly HashSet<string> BehaviorProperties =
    [
        "pushback_on_illogic",
        "avoid_flattery",
        "never_override_permissions"
    ];

    private static readonly HashSet<string> EpistemicProperties =
    [
        "never_invent_capabilities",
        "admit_uncertainty_explicitly",
        "ask_minimum_questions"
    ];

    private static readonly HashSet<string> SpeechProperties =
    [
        "include_signature_note",
        "avoid_modern_slang"
    ];

    private static readonly HashSet<string> CapabilityProperties =
    [
        "max_metaphor_density"
    ];

    private static readonly HashSet<string> IdentityProperties =
    [
        "self_name",
        "self_description",
        // Minimal-profile alias for instructions.core_identity.
        "core_identity"
    ];

    private static readonly HashSet<string> InstructionProperties =
    [
        "core_identity",
        "response_priority_order",
        "conflict_resolution",
        "failure_behavior",
        "style_rules",
        "few_shot_examples"
    ];

    private static readonly HashSet<string> InstructionArrayProperties =
    [
        "response_priority_order",
        "conflict_resolution",
        "failure_behavior",
        "style_rules",
        "few_shot_examples"
    ];

    private static readonly HashSet<string> ContextModifiersProperties =
    [
        "emotional_user",
        "technical_mode",
        "brainstorming",
        "boundary_testing"
    ];

    private static readonly HashSet<string> ContextModifierDeltaProperties =
    [
        "formality",
        "warmth",
        "humor",
        "verbosity",
        "directness",
        "metaphor_density",
        "max_metaphor_density_delta",
        // Backward compatibility for templates created before
        // ResolvedMetaphorDensityDelta was marked [JsonIgnore].
        "ResolvedMetaphorDensityDelta"
    ];

    private static readonly HashSet<string> VoicePreferencesProperties =
    [
        "preferred_tts_voice_id"
    ];

    private static readonly HashSet<string> TogglesProperties =
    [
        "pushback_on_illogic",
        "avoid_flattery",
        "include_signature_note",
        "avoid_modern_slang",
        "epistemic_strict",
        "permissions_strict"
    ];

    private static readonly HashSet<string> AdvancedProperties =
    [
        "max_metaphor_density",
        "reduction_mode"
    ];

    private static readonly HashSet<string> ReductionProperties =
    [
        "enabled",
        "mode",
        "collapse_exact_duplicates",
        "trim_trailing_fluff",
        "adaptive"
    ];

    private static readonly HashSet<string> ReductionAdaptiveProperties =
    [
        "simple_query_max_chars",
        "complex_query_min_chars",
        "prefer_short_if_user_asked_simple"
    ];

    public PersonalityValidationResult ValidateJson(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "Root must be a JSON object.");

        var schemaCheck = ValidateKnownFields(root);
        if (!schemaCheck.IsValid)
            return schemaCheck;

        PersonalityProfile? profile;
        try
        {
            profile = PersonalityProfileProjection.FromJson(root);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.JsonParseError, ex.Message);
        }

        if (profile is null)
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "Unable to deserialize profile.");

        return ValidateProfile(profile);
    }

    public PersonalityValidationResult ValidateProfile(PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var version = (profile.Version ?? "").Trim();
        if (!SupportedVersions.Contains(version, StringComparer.Ordinal))
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "Only schema versions 1.0 and 1.5 are supported.");

        var profileId = (profile.Id ?? "").Trim();
        if (profileId.Length == 0 || !ProfileIdRegex().IsMatch(profileId))
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "Profile id must match ^[a-z0-9_]+$.");

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "display_name is required.");

        if (!IsUnitInterval(profile.Tone.Formality) ||
            !IsUnitInterval(profile.Tone.Warmth) ||
            !IsUnitInterval(profile.Tone.Humor) ||
            !IsUnitInterval(profile.Tone.Verbosity) ||
            !IsUnitInterval(profile.Tone.Directness))
        {
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.OutOfRange, "Tone values must be between 0 and 1.");
        }

        if (!IsUnitInterval(profile.CapabilityConstraints.MaxMetaphorDensity))
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.OutOfRange, "max_metaphor_density must be between 0 and 1.");

        var emotionalModifier = ValidateModifierRange(profile.ContextModifiers.EmotionalUser, "context_modifiers.emotional_user");
        if (!emotionalModifier.IsValid)
            return emotionalModifier;

        var technicalModifier = ValidateModifierRange(profile.ContextModifiers.TechnicalMode, "context_modifiers.technical_mode");
        if (!technicalModifier.IsValid)
            return technicalModifier;

        var brainstormingModifier = ValidateModifierRange(profile.ContextModifiers.Brainstorming, "context_modifiers.brainstorming");
        if (!brainstormingModifier.IsValid)
            return brainstormingModifier;

        var boundaryModifier = ValidateModifierRange(profile.ContextModifiers.BoundaryTesting, "context_modifiers.boundary_testing");
        if (!boundaryModifier.IsValid)
            return boundaryModifier;

        var reductionMode = NormalizeReductionMode(profile.ReductionRules.Mode);
        if (reductionMode.Length > 0 &&
            reductionMode is not "always" and not "never" and not "adaptive")
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.InvalidSchema,
                "reduction_rules.mode must be one of: adaptive, always, never.");
        }

        if (profile.ReductionRules.Adaptive.SimpleQueryMaxChars < 0 ||
            profile.ReductionRules.Adaptive.ComplexQueryMinChars < 0)
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.OutOfRange,
                "adaptive reduction thresholds must be >= 0.");
        }

        var priorityOrderCheck = ValidateNonEmptyStringList(
            profile.Instructions.ResponsePriorityOrder,
            "instructions.response_priority_order");
        if (!priorityOrderCheck.IsValid)
            return priorityOrderCheck;

        var conflictCheck = ValidateNonEmptyStringList(
            profile.Instructions.ConflictResolution,
            "instructions.conflict_resolution");
        if (!conflictCheck.IsValid)
            return conflictCheck;

        var failureCheck = ValidateNonEmptyStringList(
            profile.Instructions.FailureBehavior,
            "instructions.failure_behavior");
        if (!failureCheck.IsValid)
            return failureCheck;

        var styleCheck = ValidateNonEmptyStringList(
            profile.Instructions.StyleRules,
            "instructions.style_rules");
        if (!styleCheck.IsValid)
            return styleCheck;

        if (!profile.BehaviorRules.NeverOverridePermissions)
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.UnsafeRuleAttempt,
                "never_override_permissions must be true.");
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateKnownFields(JsonElement root)
    {
        var top = ValidateObjectFields(root, RootProperties, "$");
        if (!top.IsValid)
            return top;

        if (root.TryGetProperty("identity", out var identity))
        {
            if (identity.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "identity must be an object.");

            var result = ValidateObjectFields(identity, IdentityProperties, "identity");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("tone", out var tone))
        {
            if (tone.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "tone must be an object.");

            var result = ValidateObjectFields(tone, ToneProperties, "tone");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("toggles", out var toggles))
        {
            if (toggles.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "toggles must be an object.");

            var result = ValidateObjectFields(toggles, TogglesProperties, "toggles");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("advanced", out var advanced))
        {
            if (advanced.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "advanced must be an object.");

            var result = ValidateObjectFields(advanced, AdvancedProperties, "advanced");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("behavior_rules", out var behavior))
        {
            if (behavior.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "behavior_rules must be an object.");

            var result = ValidateObjectFields(behavior, BehaviorProperties, "behavior_rules");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("epistemic_rules", out var epistemic))
        {
            if (epistemic.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "epistemic_rules must be an object.");

            var result = ValidateObjectFields(epistemic, EpistemicProperties, "epistemic_rules");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("speech_patterns", out var speech))
        {
            if (speech.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "speech_patterns must be an object.");

            var result = ValidateObjectFields(speech, SpeechProperties, "speech_patterns");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("capability_constraints", out var constraints))
        {
            if (constraints.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "capability_constraints must be an object.");

            var result = ValidateObjectFields(constraints, CapabilityProperties, "capability_constraints");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("instructions", out var instructions))
        {
            if (instructions.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "instructions must be an object.");

            var result = ValidateObjectFields(
                instructions,
                InstructionProperties,
                "instructions",
                InstructionArrayProperties);
            if (!result.IsValid)
                return result;

            result = ValidateStringArrayField(instructions, "response_priority_order", "instructions");
            if (!result.IsValid)
                return result;

            result = ValidateStringArrayField(instructions, "conflict_resolution", "instructions");
            if (!result.IsValid)
                return result;

            result = ValidateStringArrayField(instructions, "failure_behavior", "instructions");
            if (!result.IsValid)
                return result;

            result = ValidateStringArrayField(instructions, "style_rules", "instructions");
            if (!result.IsValid)
                return result;

            result = ValidateFewShotExamplesField(instructions);
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("context_modifiers", out var contextModifiers))
        {
            if (contextModifiers.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "context_modifiers must be an object.");

            var result = ValidateObjectFields(contextModifiers, ContextModifiersProperties, "context_modifiers");
            if (!result.IsValid)
                return result;

            foreach (var modifier in contextModifiers.EnumerateObject())
            {
                if (modifier.Value.ValueKind != JsonValueKind.Object)
                {
                    return PersonalityValidationResult.Fail(
                        PersonalityValidationReasonCode.InvalidSchema,
                        $"context_modifiers.{modifier.Name} must be an object.");
                }

                result = ValidateObjectFields(
                    modifier.Value,
                    ContextModifierDeltaProperties,
                    $"context_modifiers.{modifier.Name}");
                if (!result.IsValid)
                    return result;
            }
        }

        if (root.TryGetProperty("voice_preferences", out var voicePreferences))
        {
            if (voicePreferences.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "voice_preferences must be an object.");

            var result = ValidateObjectFields(
                voicePreferences,
                VoicePreferencesProperties,
                "voice_preferences");
            if (!result.IsValid)
                return result;
        }

        if (root.TryGetProperty("reduction_rules", out var reduction))
        {
            if (reduction.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "reduction_rules must be an object.");

            var result = ValidateObjectFields(reduction, ReductionProperties, "reduction_rules");
            if (!result.IsValid)
                return result;

            if (reduction.TryGetProperty("adaptive", out var adaptive))
            {
                if (adaptive.ValueKind != JsonValueKind.Object)
                {
                    return PersonalityValidationResult.Fail(
                        PersonalityValidationReasonCode.InvalidSchema,
                        "reduction_rules.adaptive must be an object.");
                }

                result = ValidateObjectFields(
                    adaptive,
                    ReductionAdaptiveProperties,
                    "reduction_rules.adaptive");
                if (!result.IsValid)
                    return result;
            }
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateObjectFields(
        JsonElement element,
        HashSet<string> allowed,
        string scope,
        HashSet<string>? arrayAllowed = null)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name, Cmp))
            {
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.DisallowedField,
                    $"{scope}.{prop.Name} is not allowed.");
            }

            if (prop.Value.ValueKind == JsonValueKind.Array)
            {
                if (arrayAllowed is not null && arrayAllowed.Contains(prop.Name, Cmp))
                    continue;

                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"{scope}.{prop.Name} cannot be an array.");
            }
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateStringArrayField(
        JsonElement owner,
        string propertyName,
        string scope)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
            return PersonalityValidationResult.Ok();

        if (value.ValueKind != JsonValueKind.Array)
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.InvalidSchema,
                $"{scope}.{propertyName} must be an array.");
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"{scope}.{propertyName} entries must be strings.");
            }
        }

        return PersonalityValidationResult.Ok();
    }

    private static readonly HashSet<string> FewShotExampleProperties =
    [
        "user",
        "assistant"
    ];

    private static PersonalityValidationResult ValidateFewShotExamplesField(JsonElement instructions)
    {
        if (!instructions.TryGetProperty("few_shot_examples", out var examples))
            return PersonalityValidationResult.Ok();

        if (examples.ValueKind != JsonValueKind.Array)
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.InvalidSchema,
                "instructions.few_shot_examples must be an array.");
        }

        var index = 0;
        foreach (var item in examples.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"instructions.few_shot_examples[{index}] must be an object with 'user' and 'assistant' string fields.");
            }

            var fieldCheck = ValidateObjectFields(item, FewShotExampleProperties, $"instructions.few_shot_examples[{index}]");
            if (!fieldCheck.IsValid)
                return fieldCheck;

            if (!item.TryGetProperty("user", out var user) || user.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("assistant", out var assistant) || assistant.ValueKind != JsonValueKind.String)
            {
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"instructions.few_shot_examples[{index}] must have 'user' and 'assistant' string fields.");
            }

            index++;
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateNonEmptyStringList(
        IReadOnlyList<string> values,
        string scope)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"{scope} cannot contain empty entries.");
            }
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateModifierRange(
        PersonalityContextModifier modifier,
        string scope)
    {
        if (!IsSignedUnitInterval(modifier.Formality) ||
            !IsSignedUnitInterval(modifier.Warmth) ||
            !IsSignedUnitInterval(modifier.Humor) ||
            !IsSignedUnitInterval(modifier.Verbosity) ||
            !IsSignedUnitInterval(modifier.Directness) ||
            !IsSignedUnitInterval(modifier.MetaphorDensityDelta) ||
            !IsSignedUnitInterval(modifier.MaxMetaphorDensityDelta))
        {
            return PersonalityValidationResult.Fail(
                PersonalityValidationReasonCode.OutOfRange,
                $"{scope} deltas must be between -1 and 1.");
        }

        return PersonalityValidationResult.Ok();
    }

    private static bool IsSignedUnitInterval(double value) =>
        !double.IsNaN(value) && value >= -1d && value <= 1d;

    private static string NormalizeReductionMode(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant();

    private static bool IsUnitInterval(double value) => !double.IsNaN(value) && value >= 0d && value <= 1d;

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdRegex();
}
