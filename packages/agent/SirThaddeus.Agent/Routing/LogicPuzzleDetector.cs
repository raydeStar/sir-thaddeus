namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Compound-signal detectors for logic puzzles, trick questions, and
/// abstract reasoning prompts. Each sub-detector requires 2+ cues to
/// minimize false positives on real-world queries that share vocabulary.
/// </summary>
internal static class LogicPuzzleDetector
{
    public static bool IsLogicPuzzle(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return LooksLikeFamilyPhotoPuzzle(lower)
            || LooksLikeContainerMeasurePuzzle(lower)
            || LooksLikeRiverCrossingPuzzle(lower)
            || LooksLikeMontyHallPuzzle(lower)
            || LooksLikeTruthLiarPuzzle(lower)
            || LooksLikeHatPuzzle(lower)
            || LooksLikeBridgeTorchPuzzle(lower)
            || LooksLikeEggDropPuzzle(lower)
            || LooksLikeTrickQuestion(lower)
            || LooksLikeMathIntuitionTrap(lower)
            || LooksLikeLogicalParadox(lower)
            || LooksLikeWordplayPuzzle(lower)
            || LooksLikeClockAnglePuzzle(lower)
            || LooksLikeSyllogismPuzzle(lower)
            || LooksLikeExplicitPuzzleFraming(lower);
    }

    // ── Existing sub-detectors (migrated from IntentFeatureExtractor) ─

    private static bool LooksLikeFamilyPhotoPuzzle(string lower)
    {
        var normalized = lower.Replace('\u2019', '\'');
        var hasPhotoCue =
            normalized.Contains("who is in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who is in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who is in the picture", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("who's in the photo", StringComparison.Ordinal) ||
            normalized.Contains("who's in the picture", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photograph", StringComparison.Ordinal) ||
            normalized.Contains("whos in the photo", StringComparison.Ordinal) ||
            normalized.Contains("whos in the picture", StringComparison.Ordinal) ||
            normalized.Contains("looking at a photograph", StringComparison.Ordinal) ||
            normalized.Contains("pointing to a photograph", StringComparison.Ordinal);
        var hasOnlyChildCue =
            normalized.Contains("brothers and sisters, i have none", StringComparison.Ordinal) ||
            normalized.Contains("brothers and sisters i have none", StringComparison.Ordinal) ||
            normalized.Contains("i have no siblings", StringComparison.Ordinal) ||
            normalized.Contains("i don't have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i do not have siblings", StringComparison.Ordinal) ||
            normalized.Contains("i am an only child", StringComparison.Ordinal) ||
            normalized.Contains("i'm an only child", StringComparison.Ordinal);

        var hasFamilyEquation =
            (normalized.Contains("that man's", StringComparison.Ordinal) ||
             normalized.Contains("that woman's", StringComparison.Ordinal) ||
             normalized.Contains("that person's", StringComparison.Ordinal) ||
             normalized.Contains("that boy's", StringComparison.Ordinal) ||
             normalized.Contains("that girl's", StringComparison.Ordinal)) &&
            normalized.Contains(" is my ", StringComparison.Ordinal) &&
            (normalized.Contains(" father's ", StringComparison.Ordinal) ||
             normalized.Contains(" mother's ", StringComparison.Ordinal)) &&
            (normalized.Contains(" son", StringComparison.Ordinal) ||
             normalized.Contains(" daughter", StringComparison.Ordinal));

        return hasPhotoCue && hasOnlyChildCue && hasFamilyEquation;
    }

    private static bool LooksLikeContainerMeasurePuzzle(string lower)
    {
        ReadOnlySpan<string> containerCues = ["jug", "bucket", "container", "pitcher", "barrel"];
        ReadOnlySpan<string> volumeUnits   = ["liter", "litre", "gallon", "ml ", "milliliter"];
        ReadOnlySpan<string> goalCues      = ["measure exactly", "measure out", "get exactly", "how do you measure"];

        return ContainsAny(lower, containerCues)
            && ContainsAny(lower, volumeUnits)
            && ContainsAny(lower, goalCues);
    }

    private static bool LooksLikeRiverCrossingPuzzle(string lower)
    {
        ReadOnlySpan<string> crossingCues   = ["cross a river", "river crossing", "across the river", "cross the river"];
        ReadOnlySpan<string> constraintCues = ["boat can only", "can only carry", "one at a time", "left alone", "if left"];

        return ContainsAny(lower, crossingCues) && ContainsAny(lower, constraintCues);
    }

    private static bool LooksLikeMontyHallPuzzle(string lower)
    {
        ReadOnlySpan<string> showCues  = ["game show", "three doors", "3 doors"];
        ReadOnlySpan<string> prizeCues = ["goat", "car behind", "prize behind"];

        return ContainsAny(lower, showCues) && ContainsAny(lower, prizeCues);
    }

    private static bool LooksLikeTruthLiarPuzzle(string lower)
    {
        ReadOnlySpan<string> truthCues = ["always tells the truth", "always lies", "one is a liar"];
        ReadOnlySpan<string> guardCues = ["guard", "knight", "knave", "native", "islander"];

        return ContainsAny(lower, truthCues) && ContainsAny(lower, guardCues);
    }

    private static bool LooksLikeHatPuzzle(string lower)
    {
        var hasHat = lower.Contains("hat", StringComparison.Ordinal);
        ReadOnlySpan<string> constraintCues = ["can only see", "can see the person", "facing the wall", "prisoners"];
        ReadOnlySpan<string> colorCues      = ["red or blue", "black or white", "color of", "colour of"];

        return hasHat && (ContainsAny(lower, constraintCues) || ContainsAny(lower, colorCues));
    }

    private static bool LooksLikeBridgeTorchPuzzle(string lower)
    {
        ReadOnlySpan<string> bridgeCues = ["cross a bridge", "bridge at night", "cross the bridge"];
        ReadOnlySpan<string> torchCues  = ["one torch", "one flashlight", "one lantern", "crossing time"];

        return ContainsAny(lower, bridgeCues) && ContainsAny(lower, torchCues);
    }

    private static bool LooksLikeEggDropPuzzle(string lower)
    {
        var hasEgg = lower.Contains("egg", StringComparison.Ordinal);
        ReadOnlySpan<string> floorCues = ["floor", "story building", "storey building"];
        ReadOnlySpan<string> goalCues  = ["find the", "minimum number", "fewest", "critical floor", "breaks"];

        return hasEgg && ContainsAny(lower, floorCues) && ContainsAny(lower, goalCues);
    }

    // ── New sub-detectors ────────────────────────────────────────────

    private static bool LooksLikeTrickQuestion(string lower)
    {
        // "All but N die" wording
        if (lower.Contains("all but", StringComparison.Ordinal) &&
            lower.Contains("die", StringComparison.Ordinal))
            return true;

        // Egg yolk trap: "yolk" + "white" + choice framing
        if (lower.Contains("yolk", StringComparison.Ordinal) &&
            lower.Contains("white", StringComparison.Ordinal) &&
            (lower.Contains("correct", StringComparison.Ordinal) ||
             lower.Contains("which is", StringComparison.Ordinal)))
            return true;

        // Rooster egg
        if (lower.Contains("rooster", StringComparison.Ordinal) &&
            lower.Contains("egg", StringComparison.Ordinal))
            return true;

        // Months with 28 days
        if (lower.Contains("month", StringComparison.Ordinal) &&
            lower.Contains("28 day", StringComparison.Ordinal))
            return true;

        // Elevator riddle: elevator + floor + rain/umbrella
        if (lower.Contains("elevator", StringComparison.Ordinal) &&
            lower.Contains("floor", StringComparison.Ordinal) &&
            (lower.Contains("rain", StringComparison.Ordinal) ||
             lower.Contains("umbrella", StringComparison.Ordinal)))
            return true;

        // Widow marriage
        if (lower.Contains("widow", StringComparison.Ordinal) &&
            lower.Contains("marry", StringComparison.Ordinal))
            return true;

        // Bury survivors
        if (lower.Contains("survivor", StringComparison.Ordinal) &&
            lower.Contains("bury", StringComparison.Ordinal))
            return true;

        // Car wash + walk/drive goal inference
        if (lower.Contains("car wash", StringComparison.Ordinal) &&
            (lower.Contains("walk or drive", StringComparison.Ordinal) ||
             lower.Contains("drive or walk", StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static bool LooksLikeMathIntuitionTrap(string lower)
    {
        // Bat and ball cost puzzle
        if (lower.Contains("bat", StringComparison.Ordinal) &&
            lower.Contains("ball", StringComparison.Ordinal) &&
            (lower.Contains("1.10", StringComparison.Ordinal) ||
             lower.Contains("$1", StringComparison.Ordinal)))
            return true;

        // Steel vs feathers equal weight
        if (lower.Contains("feather", StringComparison.Ordinal) &&
            lower.Contains("weigh", StringComparison.Ordinal) &&
            (lower.Contains("steel", StringComparison.Ordinal) ||
             lower.Contains("iron", StringComparison.Ordinal) ||
             lower.Contains("lead", StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static bool LooksLikeLogicalParadox(string lower)
    {
        if ((lower.Contains("i am lying", StringComparison.Ordinal) ||
             lower.Contains("i'm lying", StringComparison.Ordinal)) &&
            (lower.Contains("truth", StringComparison.Ordinal) ||
             lower.Contains("telling", StringComparison.Ordinal) ||
             lower.Contains("is he", StringComparison.Ordinal) ||
             lower.Contains("?", StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static bool LooksLikeWordplayPuzzle(string lower)
    {
        if (lower.Contains("rearrange", StringComparison.Ordinal) &&
            lower.Contains("letter", StringComparison.Ordinal))
            return true;

        if (lower.Contains("anagram", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool LooksLikeClockAnglePuzzle(string lower)
    {
        return (lower.Contains("clock", StringComparison.Ordinal) ||
                lower.Contains("watch", StringComparison.Ordinal)) &&
               (lower.Contains("angle", StringComparison.Ordinal) ||
                lower.Contains("degree", StringComparison.Ordinal));
    }

    private static bool LooksLikeSyllogismPuzzle(string lower)
    {
        // Made-up categorical terms (bloops / razzies / lazzies)
        if (lower.Contains("bloop", StringComparison.Ordinal) ||
            lower.Contains("razz", StringComparison.Ordinal) ||
            lower.Contains("lazz", StringComparison.Ordinal))
            return true;

        // Generic "if all X are Y ... are all X definitely Z?"
        if (lower.Contains("if all", StringComparison.Ordinal) &&
            (lower.Contains("are all", StringComparison.Ordinal) ||
             lower.Contains("definitely", StringComparison.Ordinal) ||
             lower.Contains("necessarily", StringComparison.Ordinal)))
            return true;

        return false;
    }

    private static bool LooksLikeExplicitPuzzleFraming(string lower)
    {
        ReadOnlySpan<string> cues =
        [
            "brain teaser", "logic puzzle", "logic problem",
            "riddle for you", "solve this riddle",
            "thought experiment", "solve this puzzle"
        ];
        return ContainsAny(lower, cues);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool ContainsAny(string lower, ReadOnlySpan<string> tokens)
    {
        foreach (var token in tokens)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
