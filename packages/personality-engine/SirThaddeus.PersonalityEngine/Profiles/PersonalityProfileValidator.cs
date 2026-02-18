using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.PersonalityEngine.Profiles;

public sealed partial class PersonalityProfileValidator
{
    private static readonly StringComparer Cmp = StringComparer.Ordinal;

    private static readonly HashSet<string> RootProperties =
    [
        "version",
        "id",
        "display_name",
        "description",
        "identity",
        "tone",
        "behavior_rules",
        "speech_patterns",
        "capability_constraints",
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
        "self_description"
    ];

    private static readonly HashSet<string> ReductionProperties =
    [
        "enabled",
        "collapse_exact_duplicates",
        "trim_trailing_fluff"
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
            profile = root.Deserialize<PersonalityProfile>();
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

        if (!string.Equals(profile.Version.Trim(), "1.0", StringComparison.Ordinal))
            return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "Only schema version 1.0 is supported.");

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

        if (root.TryGetProperty("behavior_rules", out var behavior))
        {
            if (behavior.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "behavior_rules must be an object.");

            var result = ValidateObjectFields(behavior, BehaviorProperties, "behavior_rules");
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

        if (root.TryGetProperty("reduction_rules", out var reduction))
        {
            if (reduction.ValueKind != JsonValueKind.Object)
                return PersonalityValidationResult.Fail(PersonalityValidationReasonCode.InvalidSchema, "reduction_rules must be an object.");

            var result = ValidateObjectFields(reduction, ReductionProperties, "reduction_rules");
            if (!result.IsValid)
                return result;
        }

        return PersonalityValidationResult.Ok();
    }

    private static PersonalityValidationResult ValidateObjectFields(
        JsonElement element,
        HashSet<string> allowed,
        string scope)
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
                return PersonalityValidationResult.Fail(
                    PersonalityValidationReasonCode.InvalidSchema,
                    $"{scope}.{prop.Name} cannot be an array.");
            }
        }

        return PersonalityValidationResult.Ok();
    }

    private static bool IsUnitInterval(double value) => !double.IsNaN(value) && value >= 0d && value <= 1d;

    [GeneratedRegex("^[a-z0-9_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileIdRegex();
}
