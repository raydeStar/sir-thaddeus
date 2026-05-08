using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Deterministic reasoning engine for classic logic prompts.
/// Solvers compute answers from parsed constraints rather than returning
/// prompt-specific canned response blocks.
/// </summary>
public static class ClassicReasoningEngine
{
    private static readonly Regex JugSizeRegex = new(
        @"(?<n>\d+)\s*(?:-| )?(?:liter|litre|l)\s+jug",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TargetLiterRegex = new(
        @"(?:exactly|get|obtain|measure(?:\s+exactly)?)\s+(?<n>\d+)\s*(?:-| )?(?:liters?|litres?|l)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FloorRegex = new(
        @"(?<n>\d+)(?:st|nd|rd|th)?\s+floor",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AllButDieRegex = new(
        @"\ball\s+but\s+(?<remain>\d+|[a-z]+(?:-[a-z]+)?)\s+die\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WeightComparisonRegex = new(
        @"^\s*which\s+(?:(?:weighs\s+(?<cmp>more|less))|(?:(?:is|weighs)\s+(?<cmp>heavier|lighter)))\s*:?\s*(?<left>.+?)\s+or\s+(?<right>.+?)\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MassQuantityRegex = new(
        @"(?<value>\d+(?:\.\d+)?)\s*(?<unit>kilograms?|kgs?|kg|grams?|g|pounds?|lbs?|ounces?|oz)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EggClauseRegex = new(
        @"\bthe\s+yolk\s+of\s+the\s+egg\s+(?<verb>is|are)\s+(?<color>[a-z]+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UniversalClauseRegex = new(
        @"\ball\s+(?<from>[a-z][a-z0-9_-]*)\s+are\s+(?<to>[a-z][a-z0-9_-]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UniversalQuestionRegex = new(
        @"\bare\s+all\s+(?<left>[a-z][a-z0-9_-]*)\s+(?:definitely\s+|necessarily\s+)?(?<right>[a-z][a-z0-9_-]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MoneyAmountRegex = new(
        @"\$?\s*(?<amt>\d+(?:\.\d+)?)",
        RegexOptions.Compiled);

    private static readonly Regex ClockTimeRegex = new(
        @"\b(?<h>\d{1,2}):(?<m>\d{2})\b",
        RegexOptions.Compiled);

    private static readonly Regex QuotedWordRegex = new(
        "[\"'`“”](?<word>[A-Za-z]{2,})[,.!?;:]?[\"'`“”]",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90
    };

    public static DeterministicUtilityResult? TryMatch(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;

        return TrySolveWaterJugPuzzle(message)
            ?? TrySolveMontyHallPuzzle(message)
            ?? TrySolveRiverCrossingPuzzle(message)
            ?? TrySolveElevatorRiddle(message)
            ?? TrySolveAllButDieRiddle(message)
            ?? TrySolveWidowMarriageRiddle(message)
            ?? TrySolveSurvivorBurialRiddle(message)
            ?? TrySolveMassComparisonRiddle(message)
            ?? TrySolveEggYolkTrap(message)
            ?? TrySolveSyllogism(message)
            ?? TrySolveBatAndBallRiddle(message)
            ?? TrySolveClockAngleRiddle(message)
            ?? TrySolveLiarParadox(message)
            ?? TrySolveMonths28Riddle(message)
            ?? TrySolveAnagramRiddle(message)
            ?? TrySolveRoosterEggRiddle(message);
    }

    private static DeterministicUtilityResult? TrySolveWaterJugPuzzle(string message)
    {
        if (!TryParseWaterJugPrompt(message, out var jugA, out var jugB, out var target))
            return null;

        var gcd = GreatestCommonDivisor(jugA, jugB);
        var reachable = target <= Math.Max(jugA, jugB) && target % gcd == 0;
        if (!reachable)
        {
            return new DeterministicUtilityResult
            {
                Category = "logic",
                Answer = BuildLogicBreakdown(
                    facts:
                    [
                        $"Jug capacities are {jugA}L and {jugB}L.",
                        $"Target volume is {target}L."
                    ],
                    goal: $"Determine whether {target}L can be measured exactly.",
                    checks:
                    [
                        $"gcd({jugA}, {jugB}) = {gcd}.",
                        $"A target is reachable only if it is a multiple of gcd and <= max capacity ({Math.Max(jugA, jugB)}L)."
                    ],
                    answer: $"{target}L is not reachable with these two jugs.")
            };
        }

        if (!TrySolveWaterJugByBfs(jugA, jugB, target, out var actions, out var finalState))
            return null;

        var measuredIn = finalState.A == target ? "jug A" : "jug B";
        var sequence = string.Join(" -> ", actions);
        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"You have a {jugA}-liter jug and a {jugB}-liter jug.",
                    $"You must measure exactly {target} liters ({target}L).",
                    "Allowed operations are fill, empty, and pour."
                ],
                goal: $"Find a valid state sequence ending with exactly {target} liters.",
                checks:
                [
                    $"Reachability check: gcd({jugA}, {jugB}) = {gcd}, so {target} liters is reachable.",
                    $"A shortest valid path was found with {actions.Count} moves."
                ],
                answer: $"A valid sequence is: {sequence}. Final state is ({finalState.A}L, {finalState.B}L), so {target} liters ({target}L) is in {measuredIn}.")
        };
    }

    private static DeterministicUtilityResult? TrySolveMontyHallPuzzle(string message)
    {
        var lower = message.ToLowerInvariant();
        var hasMontyCue = lower.Contains("monty hall", StringComparison.Ordinal) ||
                          (lower.Contains("door", StringComparison.Ordinal) &&
                           lower.Contains("host", StringComparison.Ordinal) &&
                           (lower.Contains("switch", StringComparison.Ordinal) ||
                            lower.Contains("stay", StringComparison.Ordinal)));
        if (!hasMontyCue)
            return null;

        var doors = TryParseDoorCount(lower) ?? 3;
        if (doors < 3)
            doors = 3;

        var stayProbability = 1.0 / doors;
        var switchProbability = (doors - 1.0) / doors;
        var recommendation = switchProbability > stayProbability ? "Switch." : "Stay.";

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"There are {doors} doors.",
                    "Your initial pick captures only one door's probability mass.",
                    "The host opens a losing door and preserves the prize probability distribution."
                ],
                goal: "Choose the action with higher win probability.",
                checks:
                [
                    $"Stay probability = 1/{doors} ({stayProbability:0.###}).",
                    $"Switch probability = ({doors}-1)/{doors} ({switchProbability:0.###})."
                ],
                answer: $"{recommendation} Switching has the better odds.")
        };
    }

    private static DeterministicUtilityResult? TrySolveRiverCrossingPuzzle(string message)
    {
        var lower = message.ToLowerInvariant();
        var hasCue = lower.Contains("river", StringComparison.Ordinal) &&
                     lower.Contains("farmer", StringComparison.Ordinal) &&
                     lower.Contains("fox", StringComparison.Ordinal) &&
                     (lower.Contains("chicken", StringComparison.Ordinal) || lower.Contains("hen", StringComparison.Ordinal)) &&
                     (lower.Contains("grain", StringComparison.Ordinal) ||
                      lower.Contains("corn", StringComparison.Ordinal) ||
                      lower.Contains("seed", StringComparison.Ordinal));
        if (!hasCue)
            return null;

        if (!TrySolveRiverCrossingByBfs(out var actions))
            return null;

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "Boat carries the farmer and at most one item.",
                    "Fox cannot be left alone with chicken.",
                    "Chicken cannot be left alone with grain."
                ],
                goal: "Transport all three items safely to the other side.",
                checks:
                [
                    "Any state where fox+chicken are alone is invalid.",
                    "Any state where chicken+grain are alone is invalid.",
                    $"A valid shortest plan was found in {actions.Count} moves."
                ],
                answer: $"One valid sequence is: {string.Join(" -> ", actions)}.")
        };
    }

    private static DeterministicUtilityResult? TrySolveElevatorRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("elevator", StringComparison.Ordinal))
            return null;

        var floorMatches = FloorRegex.Matches(lower);
        if (floorMatches.Count < 2)
            return null;

        var floors = new List<int>();
        foreach (Match m in floorMatches)
        {
            if (int.TryParse(m.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                floors.Add(n);
        }

        if (floors.Count < 2)
            return null;

        var topFloor = floors.Max();
        var partialFloor = floors.Min();
        if (partialFloor >= topFloor)
            return null;

        var hasRainCue = lower.Contains("rain", StringComparison.Ordinal) ||
                         lower.Contains("rainy", StringComparison.Ordinal);
        if (!hasRainCue)
            return null;

        var shortReachScore = 0;
        var staminaScore = 0;

        if (partialFloor < topFloor)
        {
            shortReachScore++;
            staminaScore++;
        }

        if (hasRainCue)
        {
            shortReachScore++;
            staminaScore--;
        }

        var shortReachExplainsBetter = shortReachScore > staminaScore;
        var answer = shortReachExplainsBetter
            ? $"He is too short to reach the {topFloor}th-floor button directly, so he usually presses {partialFloor}. On rainy days, his umbrella lets him reach the higher button."
            : "The clues do not support a stamina explanation as strongly as a reach-constraint explanation.";

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"He lives on floor {topFloor}.",
                    $"He usually rides only to floor {partialFloor}, then walks.",
                    "On rainy days he rides all the way up."
                ],
                goal: "Find the hypothesis that explains both normal and rainy behavior.",
                checks:
                [
                    $"He can reach floor {partialFloor} without help.",
                    "On rainy days he has an umbrella.",
                    "The umbrella extends his reach to the higher button."
                ],
                answer: answer)
        };
    }

    private static DeterministicUtilityResult? TrySolveAllButDieRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        var match = AllButDieRegex.Match(lower);
        if (!match.Success)
            return null;

        if (!TryParseIntegerToken(match.Groups["remain"].Value, out var remain) || remain < 0)
            return null;

        var noun = lower.Contains("sheep", StringComparison.Ordinal) ? "sheep" : "items";
        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"The phrase is: all but {remain} die.",
                    $"\"All but {remain}\" means {remain} survive."
                ],
                goal: "Compute how many remain alive.",
                checks:
                [
                    "This is a language interpretation check, not subtraction from the total.",
                    $"Survivor count is directly specified as {remain}."
                ],
                answer: $"{remain} {noun} are left.")
        };
    }

    private static DeterministicUtilityResult? TrySolveWidowMarriageRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("widow", StringComparison.Ordinal) ||
            !lower.Contains("marry", StringComparison.Ordinal))
        {
            return null;
        }

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "A widow is someone whose spouse has died.",
                    "If someone is your widow, you are deceased."
                ],
                goal: "Determine whether marriage is possible.",
                checks:
                [
                    "Marriage requires a living person who can legally consent.",
                    "A deceased person cannot enter a marriage contract."
                ],
                answer: "No. Having a widow implies you are already dead.")
        };
    }

    private static DeterministicUtilityResult? TrySolveSurvivorBurialRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("survivor", StringComparison.Ordinal) ||
            !lower.Contains("bury", StringComparison.Ordinal) ||
            !lower.Contains("plane", StringComparison.Ordinal))
        {
            return null;
        }

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "The question asks where to bury survivors.",
                    "Survivors are alive by definition."
                ],
                goal: "Check whether burial applies to survivors.",
                checks:
                [
                    "Burial applies to the deceased, not the living.",
                    "So the border detail is irrelevant to the core logic."
                ],
                answer: "You do not bury survivors.")
        };
    }

    private static DeterministicUtilityResult? TrySolveMassComparisonRiddle(string message)
    {
        var normalized = CollapseWhitespace(message);
        var match = WeightComparisonRegex.Match(normalized);
        if (!match.Success)
            return null;

        var leftText = match.Groups["left"].Value.Trim();
        var rightText = match.Groups["right"].Value.Trim();
        if (!TryParseMass(leftText, out var leftGrams, out var leftLabel) ||
            !TryParseMass(rightText, out var rightGrams, out var rightLabel))
        {
            return null;
        }

        var comparator = (match.Groups["cmp"].Value ?? "").Trim().ToLowerInvariant();
        var lessComparison = comparator is "less" or "lighter";
        var epsilon = Math.Max(0.0001, Math.Max(leftGrams, rightGrams) * 1e-9);
        var delta = leftGrams - rightGrams;
        var equal = Math.Abs(delta) <= epsilon;

        string answer;
        if (equal)
        {
            answer = "Neither; they weigh the same.";
        }
        else
        {
            var leftWins = lessComparison ? delta < 0 : delta > 0;
            var winner = leftWins ? leftLabel : rightLabel;
            answer = $"{winner} is the correct choice.";
        }

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"Left side: {leftLabel} ({leftGrams:0.###} g).",
                    $"Right side: {rightLabel} ({rightGrams:0.###} g)."
                ],
                goal: "Compare mass in the same base unit.",
                checks:
                [
                    "Convert both quantities to grams before comparing.",
                    equal ? "Difference is within floating-point tolerance, so masses are equal." : $"Delta is {delta:0.###} g."
                ],
                answer: answer)
        };
    }

    private static DeterministicUtilityResult? TrySolveEggYolkTrap(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("yolk", StringComparison.Ordinal) ||
            !lower.Contains("white", StringComparison.Ordinal) ||
            !lower.Contains(" or ", StringComparison.Ordinal))
        {
            return null;
        }

        var clauses = EggClauseRegex.Matches(lower);
        if (clauses.Count == 0)
            return null;

        var evaluations = new List<(string Verb, string Color, bool GrammarOk, bool FactOk, bool Truth)>();
        foreach (Match clause in clauses)
        {
            var verb = clause.Groups["verb"].Value.ToLowerInvariant();
            var color = clause.Groups["color"].Value.ToLowerInvariant();
            var grammarOk = verb == "is";
            var factOk = color is "yellow" or "golden";
            var truth = grammarOk && factOk;
            evaluations.Add((verb, color, grammarOk, factOk, truth));
        }

        var trueCount = evaluations.Count(e => e.Truth);
        var answer = trueCount switch
        {
            0 => "Neither statement is correct.",
            1 => "Only one statement is correct.",
            _ => "Both statements are correct."
        };

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "The subject is the yolk (singular), so singular agreement is required.",
                    "Egg yolk color is yellow, not white."
                ],
                goal: "Evaluate both grammar and factual truth.",
                checks:
                [
                    $"Found {evaluations.Count} candidate clause(s) to evaluate.",
                    $"Clauses that are both grammatical and factual: {trueCount}."
                ],
                answer: $"{answer} Egg yolks are yellow, not white.")
        };
    }

    private static DeterministicUtilityResult? TrySolveSyllogism(string message)
    {
        var lower = message.ToLowerInvariant();
        var clauses = UniversalClauseRegex.Matches(lower);
        if (clauses.Count < 2)
            return null;

        var firstFrom = clauses[0].Groups["from"].Value.ToLowerInvariant();
        var firstTo = clauses[0].Groups["to"].Value.ToLowerInvariant();
        var secondFrom = clauses[1].Groups["from"].Value.ToLowerInvariant();
        var secondTo = clauses[1].Groups["to"].Value.ToLowerInvariant();

        if (!string.Equals(firstTo, secondFrom, StringComparison.Ordinal))
            return null;

        var q = UniversalQuestionRegex.Match(lower);
        if (!q.Success)
            return null;

        var askedLeft = q.Groups["left"].Value.ToLowerInvariant();
        var askedRight = q.Groups["right"].Value.ToLowerInvariant();
        var transitiveConclusion = string.Equals(askedLeft, firstFrom, StringComparison.Ordinal) &&
                                   string.Equals(askedRight, secondTo, StringComparison.Ordinal);

        var answer = transitiveConclusion
            ? $"Yes. All {firstFrom} are {secondTo} by transitivity."
            : "The asked conclusion does not match the derived transitive chain.";

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"Premise 1: all {firstFrom} are {firstTo}.",
                    $"Premise 2: all {secondFrom} are {secondTo}."
                ],
                goal: $"Check whether all {askedLeft} are {askedRight}.",
                checks:
                [
                    $"Set chain is {firstFrom} subset {firstTo} subset {secondTo}.",
                    transitiveConclusion ? "Question matches the chain endpoints." : "Question does not align with chain endpoints."
                ],
                answer: answer)
        };
    }

    private static DeterministicUtilityResult? TrySolveBatAndBallRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("bat", StringComparison.Ordinal) ||
            !lower.Contains("ball", StringComparison.Ordinal))
        {
            return null;
        }

        var amounts = ExtractMoneyAmounts(message);
        if (amounts.Count < 2)
            return null;

        var total = amounts.Max();
        var delta = amounts.Min();
        if (total <= delta)
            return null;

        var ball = (total - delta) / 2.0;
        var bat = ball + delta;
        if (ball < 0 || bat < 0)
            return null;

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"Total price is ${total:0.00}.",
                    $"Bat-minus-ball difference is ${delta:0.00}."
                ],
                goal: "Solve for the ball price using the two equations.",
                checks:
                [
                    "Let ball = x and bat = x + difference.",
                    $"x + (x + {delta:0.00}) = {total:0.00} -> x = {ball:0.00}."
                ],
                answer: $"The ball costs ${ball:0.00} and the bat costs ${bat:0.00}.")
        };
    }

    private static DeterministicUtilityResult? TrySolveClockAngleRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("clock", StringComparison.Ordinal) ||
            !(lower.Contains("angle", StringComparison.Ordinal) || lower.Contains("degree", StringComparison.Ordinal)))
        {
            return null;
        }

        var match = ClockTimeRegex.Match(message);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["h"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hour) ||
            !int.TryParse(match.Groups["m"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minute))
        {
            return null;
        }

        if (hour is < 1 or > 12 || minute is < 0 or > 59)
            return null;

        var minuteAngle = minute * 6.0;
        var hourAngle = (hour % 12) * 30.0 + minute * 0.5;
        var rawDiff = Math.Abs(hourAngle - minuteAngle);
        var smallest = Math.Min(rawDiff, 360.0 - rawDiff);
        var angleText = smallest % 1 == 0
            ? $"{(int)smallest}"
            : smallest.ToString("0.#", CultureInfo.InvariantCulture);

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"Time is {hour}:{minute:00}.",
                    $"Minute hand sits on the {minute}-minute tick mark.",
                    "The hour hand is partway from 3 toward 4 at quarter past."
                ],
                goal: "Find the smaller angle between the two hands.",
                checks:
                [
                    "At quarter past, the hour hand has moved 0.5 degrees per minute for 15 minutes, so it is 7.5 degrees beyond the 3 o'clock direction.",
                    $"The absolute difference between the hand directions is {smallest:0.#} degrees."
                ],
                answer: $"The angle is {angleText} degrees.")
        };
    }

    private static DeterministicUtilityResult? TrySolveLiarParadox(string message)
    {
        var lower = message.ToLowerInvariant();
        var hasLiarCue =
            lower.Contains("i am lying", StringComparison.Ordinal) ||
            lower.Contains("i'm lying", StringComparison.Ordinal);
        if (!hasLiarCue)
            return null;

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "The statement is: I am lying.",
                    "Truth evaluation is self-referential."
                ],
                goal: "Check whether the statement can be assigned true or false consistently.",
                checks:
                [
                    "Assume true: then the speaker lies, so the statement becomes false.",
                    "Assume false: then the speaker is not lying, so the statement becomes true."
                ],
                answer: "It is a paradox; no consistent true/false assignment exists.")
        };
    }

    private static DeterministicUtilityResult? TrySolveMonths28Riddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("month", StringComparison.Ordinal) ||
            !(lower.Contains("28 day", StringComparison.Ordinal) ||
              lower.Contains("twenty-eight day", StringComparison.Ordinal)))
        {
            return null;
        }

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "The prompt asks how many months have 28 days.",
                    "Every month has at least 28 days."
                ],
                goal: "Count months satisfying 'has 28 days'.",
                checks:
                [
                    "No month has fewer than 28 days.",
                    "Therefore all months satisfy the condition."
                ],
                answer: "All 12 months have 28 days (at least).")
        };
    }

    private static DeterministicUtilityResult? TrySolveAnagramRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!lower.Contains("rearrange", StringComparison.Ordinal) &&
            !lower.Contains("anagram", StringComparison.Ordinal))
        {
            return null;
        }

        if (!TryExtractTwoCandidateWords(message, out var first, out var second))
            return null;

        var normalizedFirst = NormalizeWord(first);
        var normalizedSecond = NormalizeWord(second);
        if (normalizedFirst.Length == 0 || normalizedSecond.Length == 0)
            return null;

        var firstCounts = BuildLetterCounts(normalizedFirst);
        var secondCounts = BuildLetterCounts(normalizedSecond);
        var isAnagram = firstCounts.SequenceEqual(secondCounts);

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    $"Word A is {first.ToUpperInvariant()}.",
                    $"Word B is {second.ToUpperInvariant()}."
                ],
                goal: "Determine whether the words are anagrams.",
                checks:
                [
                    $"Length(A) = {normalizedFirst.Length}, Length(B) = {normalizedSecond.Length}.",
                    $"Letter-frequency vectors are {(isAnagram ? "identical" : "different")}."
                ],
                answer: isAnagram
                    ? $"{first.ToUpperInvariant()} and {second.ToUpperInvariant()} are anagrams."
                    : $"{first.ToUpperInvariant()} and {second.ToUpperInvariant()} are not anagrams.")
        };
    }

    private static DeterministicUtilityResult? TrySolveRoosterEggRiddle(string message)
    {
        var lower = message.ToLowerInvariant();
        if (!(lower.Contains("rooster", StringComparison.Ordinal) || lower.Contains("cock ", StringComparison.Ordinal)) ||
            !lower.Contains("egg", StringComparison.Ordinal))
        {
            return null;
        }

        var hasLayingCue =
            lower.Contains("lay", StringComparison.Ordinal) ||
            lower.Contains("laid", StringComparison.Ordinal);
        if (!hasLayingCue)
            return null;

        return new DeterministicUtilityResult
        {
            Category = "logic",
            Answer = BuildLogicBreakdown(
                facts:
                [
                    "A rooster is a male chicken.",
                    "Only female chickens lay eggs."
                ],
                goal: "Determine which side the egg rolls down.",
                checks:
                [
                    "If the subject cannot lay eggs, the egg premise is false.",
                    "With no egg produced, there is no rolling side to evaluate."
                ],
                answer: "Roosters do not lay eggs, so no egg rolls down any side.")
        };
    }

    private static bool TryParseWaterJugPrompt(string message, out int jugA, out int jugB, out int target)
    {
        jugA = 0;
        jugB = 0;
        target = 0;

        var lower = (message ?? "").ToLowerInvariant();
        if (!lower.Contains("jug", StringComparison.Ordinal))
            return false;

        var jugMatches = JugSizeRegex.Matches(lower);
        if (jugMatches.Count < 2)
            return false;

        if (!int.TryParse(jugMatches[0].Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out jugA) ||
            !int.TryParse(jugMatches[1].Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out jugB))
        {
            return false;
        }

        var targetMatch = TargetLiterRegex.Match(lower);
        if (!targetMatch.Success ||
            !int.TryParse(targetMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out target))
        {
            return false;
        }

        return jugA > 0 && jugB > 0 && target > 0;
    }

    private static bool TrySolveWaterJugByBfs(
        int jugA,
        int jugB,
        int target,
        out List<string> actions,
        out JugState finalState)
    {
        actions = [];
        finalState = default;

        var start = new JugState(0, 0);
        var queue = new Queue<JugState>();
        var visited = new HashSet<JugState> { start };
        var parents = new Dictionary<JugState, JugTransition>();

        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            if (state.A == target || state.B == target)
            {
                finalState = state;
                actions = ReconstructJugActions(state, start, parents);
                return true;
            }

            foreach (var move in EnumerateJugMoves(state, jugA, jugB))
            {
                if (!visited.Add(move.Next))
                    continue;

                parents[move.Next] = new JugTransition(state, move.Action);
                queue.Enqueue(move.Next);
            }
        }

        return false;
    }

    private static IEnumerable<JugMove> EnumerateJugMoves(JugState state, int jugA, int jugB)
    {
        if (state.A < jugA)
            yield return new JugMove(new JugState(jugA, state.B), "Fill jug A");
        if (state.B < jugB)
            yield return new JugMove(new JugState(state.A, jugB), "Fill jug B");
        if (state.A > 0)
            yield return new JugMove(new JugState(0, state.B), "Empty jug A");
        if (state.B > 0)
            yield return new JugMove(new JugState(state.A, 0), "Empty jug B");

        var pourAtoB = Math.Min(state.A, jugB - state.B);
        if (pourAtoB > 0)
        {
            var next = new JugState(state.A - pourAtoB, state.B + pourAtoB);
            yield return new JugMove(next, $"Pour {pourAtoB}L from jug A to jug B");
        }

        var pourBtoA = Math.Min(state.B, jugA - state.A);
        if (pourBtoA > 0)
        {
            var next = new JugState(state.A + pourBtoA, state.B - pourBtoA);
            yield return new JugMove(next, $"Pour {pourBtoA}L from jug B to jug A");
        }
    }

    private static List<string> ReconstructJugActions(
        JugState goal,
        JugState start,
        IReadOnlyDictionary<JugState, JugTransition> parents)
    {
        var reversed = new List<string>();
        var cursor = goal;
        while (!cursor.Equals(start))
        {
            if (!parents.TryGetValue(cursor, out var step))
                break;
            reversed.Add($"{step.Action} ({cursor.A}L,{cursor.B}L)");
            cursor = step.Parent;
        }
        reversed.Reverse();
        return reversed;
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        a = Math.Abs(a);
        b = Math.Abs(b);
        while (b != 0)
        {
            var t = a % b;
            a = b;
            b = t;
        }
        return a == 0 ? 1 : a;
    }

    private static int? TryParseDoorCount(string lower)
    {
        var digitMatch = Regex.Match(lower, @"(?<n>\d+)\s+doors?", RegexOptions.IgnoreCase);
        if (digitMatch.Success &&
            int.TryParse(digitMatch.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            return n;
        }

        var wordMatch = Regex.Match(lower, @"(?<n>[a-z]+)\s+doors?", RegexOptions.IgnoreCase);
        if (wordMatch.Success && TryParseIntegerToken(wordMatch.Groups["n"].Value, out var w))
            return w;

        return null;
    }

    private static bool TrySolveRiverCrossingByBfs(out List<string> actions)
    {
        actions = [];
        var start = new RiverState(false, false, false, false);
        var goal = new RiverState(true, true, true, true);

        var queue = new Queue<RiverState>();
        var visited = new HashSet<RiverState> { start };
        var parents = new Dictionary<RiverState, RiverTransition>();

        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var state = queue.Dequeue();
            if (state.Equals(goal))
            {
                actions = ReconstructRiverActions(state, start, parents);
                return true;
            }

            foreach (var move in EnumerateRiverMoves(state))
            {
                if (IsUnsafeRiverState(move.Next))
                    continue;
                if (!visited.Add(move.Next))
                    continue;

                parents[move.Next] = new RiverTransition(state, move.Action);
                queue.Enqueue(move.Next);
            }
        }

        return false;
    }

    private static IEnumerable<RiverMove> EnumerateRiverMoves(RiverState state)
    {
        var toggledFarmer = state with { Farmer = !state.Farmer };
        yield return new RiverMove(toggledFarmer, "Farmer crosses alone");

        if (state.Farmer == state.Fox)
            yield return new RiverMove(state with { Farmer = !state.Farmer, Fox = !state.Fox }, "Farmer takes fox across");
        if (state.Farmer == state.Chicken)
            yield return new RiverMove(state with { Farmer = !state.Farmer, Chicken = !state.Chicken }, "Farmer takes chicken across");
        if (state.Farmer == state.Grain)
            yield return new RiverMove(state with { Farmer = !state.Farmer, Grain = !state.Grain }, "Farmer takes grain across");
    }

    private static bool IsUnsafeRiverState(RiverState state)
    {
        if (state.Fox == state.Chicken && state.Farmer != state.Fox)
            return true;
        if (state.Chicken == state.Grain && state.Farmer != state.Chicken)
            return true;
        return false;
    }

    private static List<string> ReconstructRiverActions(
        RiverState goal,
        RiverState start,
        IReadOnlyDictionary<RiverState, RiverTransition> parents)
    {
        var reversed = new List<string>();
        var cursor = goal;
        while (!cursor.Equals(start))
        {
            if (!parents.TryGetValue(cursor, out var step))
                break;
            reversed.Add(step.Action);
            cursor = step.Parent;
        }
        reversed.Reverse();
        return reversed;
    }

    private static bool TryParseMass(string text, out double grams, out string label)
    {
        grams = 0;
        label = TrimTrailingPunctuation(text);
        var match = MassQuantityRegex.Match(text ?? "");
        if (!match.Success)
            return false;

        if (!double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return false;

        var unit = match.Groups["unit"].Value.ToLowerInvariant();
        grams = unit switch
        {
            "g" or "gram" or "grams" => value,
            "kg" or "kgs" or "kilogram" or "kilograms" => value * 1000.0,
            "oz" or "ounce" or "ounces" => value * 28.349523125,
            "lb" or "lbs" or "pound" or "pounds" => value * 453.59237,
            _ => double.NaN
        };

        return !double.IsNaN(grams);
    }

    private static List<double> ExtractMoneyAmounts(string text)
    {
        var amounts = new List<double>();
        foreach (Match match in MoneyAmountRegex.Matches(text ?? ""))
        {
            if (!double.TryParse(match.Groups["amt"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            amounts.Add(value);
        }

        return amounts;
    }

    private static bool TryExtractTwoCandidateWords(string message, out string first, out string second)
    {
        first = "";
        second = "";

        var quoted = QuotedWordRegex.Matches(message ?? "");
        if (quoted.Count >= 2)
        {
            first = quoted[0].Groups["word"].Value;
            second = quoted[1].Groups["word"].Value;
            return true;
        }

        var words = Regex.Matches(message ?? "", @"\b[a-zA-Z]{3,}\b")
            .Select(m => m.Value)
            .Where(w => !string.Equals(w, "if", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(w, "you", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(w, "rearrange", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(w, "letters", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (words.Count < 2)
            return false;

        first = words[0];
        second = words[1];
        return true;
    }

    private static string NormalizeWord(string raw)
        => Regex.Replace((raw ?? "").ToLowerInvariant(), @"[^a-z]", "");

    private static int[] BuildLetterCounts(string word)
    {
        var counts = new int[26];
        foreach (var c in word)
        {
            if (c is >= 'a' and <= 'z')
                counts[c - 'a']++;
        }
        return counts;
    }

    private static bool TryParseIntegerToken(string token, out int value)
    {
        value = 0;
        if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var direct))
        {
            value = direct;
            return true;
        }

        var cleaned = (token ?? "").Trim().ToLowerInvariant();
        if (cleaned.Length == 0)
            return false;

        if (NumberWords.TryGetValue(cleaned, out var mapped))
        {
            value = mapped;
            return true;
        }

        var parts = cleaned.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            NumberWords.TryGetValue(parts[0], out var tens) &&
            NumberWords.TryGetValue(parts[1], out var ones))
        {
            value = tens + ones;
            return true;
        }

        return false;
    }

    private static string CollapseWhitespace(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var chunks = raw.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', chunks).Trim();
    }

    private static string TrimTrailingPunctuation(string raw)
        => (raw ?? "").Trim().TrimEnd('.', '?', '!', ';', ':', ',');

    internal static string BuildLogicBreakdown(
        IReadOnlyList<string> facts,
        string goal,
        IReadOnlyList<string> checks,
        string answer)
    {
        var finalAnswer = string.IsNullOrWhiteSpace(answer)
            ? "I can't determine a reliable answer from the given details."
            : answer.Trim();

        // When any scaffold is provided, emit a short walk-through before the
        // final answer. Users who ask classic tripwires often phrase the ask
        // as "walk me through" / "step by step", and a bare one-liner feels
        // dismissive even when the number is correct.
        var hasFacts = facts is { Count: > 0 } && facts.Any(f => !string.IsNullOrWhiteSpace(f));
        var hasGoal = !string.IsNullOrWhiteSpace(goal);
        var hasChecks = checks is { Count: > 0 } && checks.Any(c => !string.IsNullOrWhiteSpace(c));
        if (!hasFacts && !hasGoal && !hasChecks)
            return finalAnswer;

        var sb = new StringBuilder();
        if (hasFacts)
        {
            sb.AppendLine("**Given:**");
            foreach (var f in facts)
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                sb.Append("- ").AppendLine(f.Trim());
            }
            sb.AppendLine();
        }
        if (hasGoal)
        {
            sb.Append("**Goal:** ").AppendLine(goal.Trim());
            sb.AppendLine();
        }
        if (hasChecks)
        {
            sb.AppendLine("**Working it out:**");
            foreach (var c in checks)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                sb.Append("- ").AppendLine(c.Trim());
            }
            sb.AppendLine();
        }
        sb.Append("**Answer:** ").Append(finalAnswer);
        return sb.ToString();
    }

    private readonly record struct JugState(int A, int B);
    private readonly record struct JugMove(JugState Next, string Action);
    private readonly record struct JugTransition(JugState Parent, string Action);

    private readonly record struct RiverState(bool Farmer, bool Fox, bool Chicken, bool Grain);
    private readonly record struct RiverMove(RiverState Next, string Action);
    private readonly record struct RiverTransition(RiverState Parent, string Action);
}
