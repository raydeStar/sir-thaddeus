using System.Text.Json;

namespace SirThaddeus.PersonalityEngine.Profiles;

/// <summary>
/// Projects profile JSON into the runtime profile contract.
/// Supports both full profiles and the minimal profile shape.
/// </summary>
public static class PersonalityProfileProjection
{
    public static PersonalityProfile FromJson(
        JsonElement root,
        JsonSerializerOptions? options = null)
    {
        PersonalityProfile profile = options is null
            ? (root.Deserialize<PersonalityProfile>() ?? new PersonalityProfile())
            : (root.Deserialize<PersonalityProfile>(options) ?? new PersonalityProfile());

        var identity = profile.Identity;
        var instructions = profile.Instructions;
        var behavior = profile.BehaviorRules;
        var speech = profile.SpeechPatterns;
        var epistemic = profile.EpistemicRules;
        var constraints = profile.CapabilityConstraints;
        var reduction = profile.ReductionRules;
        var description = profile.Description;

        if (TryGetString(root, "identity", "core_identity", out var coreIdentity))
        {
            if (string.IsNullOrWhiteSpace(identity.SelfDescription))
                identity = identity with { SelfDescription = coreIdentity };

            if (string.IsNullOrWhiteSpace(instructions.CoreIdentity))
                instructions = instructions with { CoreIdentity = coreIdentity };

            if (string.IsNullOrWhiteSpace(description))
                description = coreIdentity;
        }

        if (TryGetObject(root, "toggles", out var toggles))
        {
            if (TryGetBool(toggles, "pushback_on_illogic", out var pushbackOnIllogic))
                behavior = behavior with { PushbackOnIllogic = pushbackOnIllogic };

            if (TryGetBool(toggles, "avoid_flattery", out var avoidFlattery))
                behavior = behavior with { AvoidFlattery = avoidFlattery };

            if (TryGetBool(toggles, "permissions_strict", out var permissionsStrict))
                behavior = behavior with { NeverOverridePermissions = permissionsStrict };

            if (TryGetBool(toggles, "include_signature_note", out var includeSignature))
                speech = speech with { IncludeSignatureNote = includeSignature };

            if (TryGetBool(toggles, "avoid_modern_slang", out var avoidSlang))
                speech = speech with { AvoidModernSlang = avoidSlang };

            if (TryGetBool(toggles, "epistemic_strict", out var epistemicStrict))
            {
                epistemic = epistemic with
                {
                    // Keep no-invention safety enabled even in relaxed mode.
                    NeverInventCapabilities = true,
                    AdmitUncertaintyExplicitly = epistemicStrict,
                    AskMinimumQuestions = epistemicStrict
                };
            }
        }

        if (TryGetObject(root, "advanced", out var advanced))
        {
            if (TryGetDouble(advanced, "max_metaphor_density", out var maxMetaphorDensity))
                constraints = constraints with { MaxMetaphorDensity = maxMetaphorDensity };

            if (TryGetString(advanced, "reduction_mode", out var reductionMode))
            {
                var normalizedMode = NormalizeReductionMode(reductionMode);
                reduction = reduction with
                {
                    Mode = normalizedMode,
                    Enabled = normalizedMode == "always" || reduction.Enabled
                };
            }
        }

        return profile with
        {
            Description = description,
            Identity = identity,
            Instructions = instructions,
            BehaviorRules = behavior,
            SpeechPatterns = speech,
            EpistemicRules = epistemic,
            CapabilityConstraints = constraints,
            ReductionRules = reduction
        };
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.TryGetProperty(propertyName, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetString(
        JsonElement root,
        string parentName,
        string propertyName,
        out string value)
    {
        value = "";
        if (!TryGetObject(root, parentName, out var parent))
            return false;

        return TryGetString(parent, propertyName, out value);
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = "";
        if (!root.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind != JsonValueKind.String)
            return false;

        value = (element.GetString() ?? "").Trim();
        return true;
    }

    private static bool TryGetBool(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind != JsonValueKind.True &&
            element.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static bool TryGetDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0d;
        if (!root.TryGetProperty(propertyName, out var element))
            return false;
        if (element.ValueKind != JsonValueKind.Number)
            return false;

        return element.TryGetDouble(out value);
    }

    private static string NormalizeReductionMode(string mode)
    {
        var normalized = (mode ?? "").Trim().ToLowerInvariant();
        return normalized is "adaptive" or "always" or "never"
            ? normalized
            : "";
    }
}
