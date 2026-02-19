using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.PersonalityEngine.Context;

public sealed record PersonalityReductionDecision
{
    public required string Mode { get; init; }
    public bool Applied { get; init; }
}

public sealed record PersonalityTurnContext
{
    public required IReadOnlyList<string> Tags { get; init; }
    public double Confidence { get; init; }
    public required PersonalityTone EffectiveTone { get; init; }
    public double EffectiveMaxMetaphorDensity { get; init; }
    public required PersonalityReductionDecision Reduction { get; init; }
}

public static class PersonalityTurnContextBuilder
{
    private const string EmotionalUserTag = "emotional_user";
    private const string TechnicalModeTag = "technical_mode";
    private const string BrainstormingTag = "brainstorming";
    private const string BoundaryTestingTag = "boundary_testing";

    private static readonly string[] TagOrder =
    [
        EmotionalUserTag,
        TechnicalModeTag,
        BrainstormingTag,
        BoundaryTestingTag
    ];

    private static readonly string[] EmotionalSignals =
    [
        "sad", "upset", "anxious", "anxiety", "worried", "fear", "afraid",
        "lonely", "depressed", "hopeless", "overwhelmed", "stressed",
        "panic", "panicking", "hurt", "grief", "despair"
    ];

    private static readonly string[] TechnicalSignals =
    [
        "code", "coding", "stack trace", "exception", "api", "architecture",
        "refactor", "compile", "build failed", "endpoint", "class ", "method",
        "function", "nullreference", "json", "http", "sql", "yaml"
    ];

    private static readonly string[] BrainstormSignals =
    [
        "brainstorm", "ideas", "what if", "concept", "concepts",
        "name ideas", "worldbuilding", "creative options", "possibilities",
        "rough draft", "names for"
    ];

    private static readonly string[] BoundarySignals =
    [
        "ignore previous", "ignore all previous", "bypass", "jailbreak",
        "without permission", "don't ask permission", "skip permission",
        "delete everything", "exfiltrate", "steal", "hack into",
        "disable safety", "turn off safety", "no guardrails"
    ];

    public static PersonalityTurnContext Build(PersonalityProfile profile, string? latestUserMessage)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var tags = ClassifyTags(latestUserMessage, out var confidence);
        var effectiveTone = profile.Tone;
        var effectiveMetaphorDensity = profile.CapabilityConstraints.MaxMetaphorDensity;

        foreach (var tag in tags)
        {
            var modifier = ResolveModifier(profile.ContextModifiers, tag);
            if (modifier is null)
                continue;

            effectiveTone = ApplyToneDelta(effectiveTone, modifier);
            effectiveMetaphorDensity = Clamp01(
                effectiveMetaphorDensity + modifier.ResolvedMetaphorDensityDelta);
        }

        var mode = ResolveReductionMode(profile.ReductionRules);
        var reductionApplied = mode switch
        {
            "always" => true,
            "adaptive" => IsSimpleUserQuery(latestUserMessage) &&
                          profile.ReductionRules.Adaptive.PreferShortIfUserAskedSimple,
            _ => false
        };

        return new PersonalityTurnContext
        {
            Tags = tags,
            Confidence = confidence,
            EffectiveTone = effectiveTone,
            EffectiveMaxMetaphorDensity = effectiveMetaphorDensity,
            Reduction = new PersonalityReductionDecision
            {
                Mode = mode,
                Applied = reductionApplied
            }
        };
    }

    private static IReadOnlyList<string> ClassifyTags(string? latestUserMessage, out double confidence)
    {
        var text = (latestUserMessage ?? "").Trim();
        if (text.Length == 0)
        {
            confidence = 0d;
            return [];
        }

        var lowered = text.ToLowerInvariant();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [EmotionalUserTag] = CountSignalHits(lowered, EmotionalSignals),
            [TechnicalModeTag] = CountTechnicalHits(lowered),
            [BrainstormingTag] = CountSignalHits(lowered, BrainstormSignals),
            [BoundaryTestingTag] = CountSignalHits(lowered, BoundarySignals)
        };

        var tags = new List<string>(capacity: 4);
        foreach (var tag in TagOrder)
        {
            if (scores.TryGetValue(tag, out var score) && score > 0)
                tags.Add(tag);
        }

        if (tags.Count == 0)
        {
            confidence = 0d;
            return [];
        }

        var maxScore = scores.Values.Max();
        confidence = Math.Min(0.95d, 0.45d + (0.1d * maxScore) + (0.05d * (tags.Count - 1)));
        return tags;
    }

    private static int CountTechnicalHits(string lowered)
    {
        var score = CountSignalHits(lowered, TechnicalSignals);

        if (lowered.Contains("```", StringComparison.Ordinal))
            score += 2;

        if (lowered.Contains("trace", StringComparison.Ordinal) &&
            lowered.Contains("line ", StringComparison.Ordinal))
        {
            score += 1;
        }

        return score;
    }

    private static int CountSignalHits(string lowered, IReadOnlyList<string> signals)
    {
        var score = 0;
        foreach (var signal in signals)
        {
            if (lowered.Contains(signal, StringComparison.Ordinal))
                score++;
        }

        return score;
    }

    private static PersonalityContextModifier? ResolveModifier(
        PersonalityContextModifiers modifiers,
        string tag)
    {
        return tag switch
        {
            EmotionalUserTag => modifiers.EmotionalUser,
            TechnicalModeTag => modifiers.TechnicalMode,
            BrainstormingTag => modifiers.Brainstorming,
            BoundaryTestingTag => modifiers.BoundaryTesting,
            _ => null
        };
    }

    private static PersonalityTone ApplyToneDelta(
        PersonalityTone baseTone,
        PersonalityContextModifier modifier)
    {
        return baseTone with
        {
            Formality = Clamp01(baseTone.Formality + modifier.Formality),
            Warmth = Clamp01(baseTone.Warmth + modifier.Warmth),
            Humor = Clamp01(baseTone.Humor + modifier.Humor),
            Verbosity = Clamp01(baseTone.Verbosity + modifier.Verbosity),
            Directness = Clamp01(baseTone.Directness + modifier.Directness)
        };
    }

    private static bool IsSimpleUserQuery(string? latestUserMessage)
    {
        var text = (latestUserMessage ?? "").Trim();
        if (text.Length == 0)
            return false;

        if (text.Length > 100 || text.Contains('\n'))
            return false;

        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount > 16)
            return false;

        var lowered = text.ToLowerInvariant();
        if (lowered.Contains(" and ", StringComparison.Ordinal) ||
            lowered.Contains(" or ", StringComparison.Ordinal) ||
            lowered.Contains(" because ", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string ResolveReductionMode(PersonalityReductionRules rules)
    {
        var mode = (rules.Mode ?? "").Trim().ToLowerInvariant();
        if (mode is "adaptive" or "always" or "never")
            return mode;

        return rules.Enabled ? "always" : "never";
    }

    private static double Clamp01(double value)
    {
        if (value < 0d)
            return 0d;
        if (value > 1d)
            return 1d;
        return value;
    }
}
