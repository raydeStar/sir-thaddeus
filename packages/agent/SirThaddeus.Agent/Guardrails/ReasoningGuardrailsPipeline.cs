using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Guardrails;

public sealed record GuardrailsPipelineResult
{
    public required string AnswerText { get; init; }
    public IReadOnlyList<string> RationaleLines { get; init; } = [];
    public required string TriggerRisk { get; init; }
    public required string TriggerWhy { get; init; }
    public required string TriggerSource { get; init; }
    public int LlmRoundTrips { get; init; }
}

public sealed class ReasoningGuardrailsPipeline
{
    private readonly GuardrailsDetector _detector;
    private readonly GoalInferencer _goalInferencer;
    private readonly EntityExtractor _entityExtractor;
    private readonly ConstraintBuilder _constraintBuilder;
    private readonly OptionEvaluator _optionEvaluator;
    private readonly IAuditLogger _audit;

    private static readonly TimeSpan DetectorStepTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExtractionStepTimeout = TimeSpan.FromMilliseconds(850);
    private static readonly Regex WeightComparisonRegex = new(
        @"^\s*which\s+(?:(?:weighs\s+(?<cmp>more|less))|(?:(?:is|weighs)\s+(?<cmp>heavier|lighter)))\s*:?\s*(?<left>.+?)\s+or\s+(?<right>.+?)\s*\??\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MassQuantityRegex = new(
        @"(?<num>\d+(?:\.\d+)?|[a-z]+(?:[-\s][a-z]+){0,5})\s*(?<unit>pounds?|lbs?|ounces?|oz|kilograms?|kgs?|grams?|g)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProperNameRegex = new(
        @"\b[A-Z][A-Za-z'\-]*\b",
        RegexOptions.Compiled);
    private static readonly Regex TimeRangeRegex = new(
        @"(?<start>\d{1,2}(?::\d{2})?\s*(?:am|pm)?)\s*(?:-|–|to)\s*(?<end>\d{1,2}(?::\d{2})?\s*(?:am|pm)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FamilyPhotoRelationRegex = new(
        @"that\s+(?<photoGender>man|woman|person|boy|girl)['’]s\s+(?<lhsRel>father|mother|son|daughter)\s+is\s+my\s+(?<rhsParent>father|mother)['’]s\s+(?<rhsOnly>only\s+)?(?<rhsChild>son|daughter)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SpeakerGenderRegex = new(
        @"\ba\s+(?<gender>man|woman|boy|girl)\s+is\s+(?:looking|pointing)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> NonNameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "An", "The",
        "I", "You", "We", "He", "She", "They", "It",
        "Who", "Because", "If", "When", "Then", "And", "Or", "But"
    };
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

    public ReasoningGuardrailsPipeline(ILlmClient llm, IAuditLogger audit)
    {
        _detector = new GuardrailsDetector(llm);
        _goalInferencer = new GoalInferencer(llm);
        _entityExtractor = new EntityExtractor(llm);
        _constraintBuilder = new ConstraintBuilder(llm);
        _optionEvaluator = new OptionEvaluator();
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<GuardrailsPipelineResult?> TryRunAsync(
        string userMessage,
        string mode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        var normalizedMode = ReasoningGuardrailsMode.Normalize(mode);
        if (!ReasoningGuardrailsMode.IsEnabled(normalizedMode))
            return null;

        if (LooksLikeDialogueOrRoleplayTask(userMessage))
            return null;

        var llmRoundTrips = 0;
        var deterministicSpecialCase = TryRunDeterministicSpecialCase(userMessage);
        if (deterministicSpecialCase is not null)
        {
            return deterministicSpecialCase with
            {
                TriggerRisk = string.IsNullOrWhiteSpace(deterministicSpecialCase.TriggerRisk)
                    ? "medium"
                    : deterministicSpecialCase.TriggerRisk,
                TriggerWhy = deterministicSpecialCase.TriggerWhy,
                TriggerSource = deterministicSpecialCase.TriggerSource,
                LlmRoundTrips = llmRoundTrips
            };
        }

        GuardrailsTriggerDecision triggerDecision;

        if (string.Equals(normalizedMode, ReasoningGuardrailsMode.Always, StringComparison.Ordinal))
        {
            triggerDecision = new GuardrailsTriggerDecision(
                Triggered: true,
                Risk: "high",
                Why: "Always mode enabled by user setting.",
                Source: "mode_always",
                LlmRoundTrips: 0);
        }
        else
        {
            var detection = await RunBoundedAsync(
                ct => _detector.DetectAsync(userMessage, ct),
                DetectorStepTimeout,
                cancellationToken);
            if (detection is null || !detection.Triggered)
                return null;

            triggerDecision = detection;
            llmRoundTrips += detection.LlmRoundTrips;
        }

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_TRIGGERED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["risk"] = triggerDecision.Risk,
                ["source"] = triggerDecision.Source,
                ["why"] = triggerDecision.Why
            }
        });

        var goal = await RunBoundedAsync(
            ct => _goalInferencer.InferAsync(userMessage, ct),
            ExtractionStepTimeout,
            cancellationToken);
        if (goal is null)
        {
            WriteFallback("goal_inference_failed");
            return null;
        }

        llmRoundTrips += goal.LlmRoundTrips;

        var entities = await RunBoundedAsync(
            ct => _entityExtractor.ExtractAsync(userMessage, ct),
            ExtractionStepTimeout,
            cancellationToken);
        if (entities is null || entities.Options.Count == 0)
        {
            WriteFallback("entity_or_option_extraction_failed");
            return null;
        }

        llmRoundTrips += entities.LlmRoundTrips;

        var constraints = await RunBoundedAsync(
            ct => _constraintBuilder.BuildAsync(userMessage, goal, entities, ct),
            ExtractionStepTimeout,
            cancellationToken);
        if (constraints is null || constraints.Constraints.Count == 0)
        {
            WriteFallback("constraint_build_failed");
            return null;
        }

        llmRoundTrips += constraints.LlmRoundTrips;

        var evaluation = _optionEvaluator.Evaluate(userMessage, goal, entities, constraints);
        if (evaluation is null)
        {
            WriteFallback("option_evaluation_failed");
            return null;
        }

        var answer = AnswerComposer.Compose(goal, constraints, evaluation);

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_DECISION",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["goal"] = goal.PrimaryGoal,
                ["selectedAction"] = evaluation.SelectedAction,
                ["constraint"] = answer.RationaleLines.FirstOrDefault() ?? "",
                ["triggerRisk"] = triggerDecision.Risk
            }
        });

        return new GuardrailsPipelineResult
        {
            AnswerText = answer.AnswerText,
            RationaleLines = answer.RationaleLines,
            TriggerRisk = triggerDecision.Risk,
            TriggerWhy = triggerDecision.Why,
            TriggerSource = triggerDecision.Source,
            LlmRoundTrips = llmRoundTrips
        };
    }

    public GuardrailsPipelineResult? TryRunDeterministicSpecialCase(string userMessage)
    {
        if (!TryRunDeterministicSpecialCaseCore(userMessage, out var specialCase))
            return null;

        WriteSpecialCaseAudit(specialCase);
        return specialCase;
    }

    private static bool TryRunDeterministicSpecialCaseCore(
        string userMessage,
        out GuardrailsPipelineResult specialCase)
    {
        return TryResolveAlreadyCompletedTaskQuestion(userMessage, out specialCase) ||
               TryResolveUnitComparisonQuestion(userMessage, out specialCase) ||
               TryResolveMeetingOverlapQuestion(userMessage, out specialCase) ||
               TryResolveFamilyPhotoRelationPuzzle(userMessage, out specialCase) ||
               TryResolveAmbiguousReferentQuestion(userMessage, out specialCase);
    }

    private static bool TryResolveAlreadyCompletedTaskQuestion(
        string userMessage,
        out GuardrailsPipelineResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = CollapseWhitespace(userMessage).Replace('’', '\'');
        var lower = normalized.ToLowerInvariant();

        var asksDuration =
            lower.Contains("how long does it take", StringComparison.Ordinal) ||
            lower.Contains("how long would it take", StringComparison.Ordinal) ||
            lower.Contains("how long will it take", StringComparison.Ordinal) ||
            lower.Contains("how much time does it take", StringComparison.Ordinal) ||
            lower.Contains("how much time would it take", StringComparison.Ordinal);
        if (!asksDuration)
            return false;

        var hasCompletionCue =
            lower.Contains("already built", StringComparison.Ordinal) ||
            lower.Contains("already done", StringComparison.Ordinal) ||
            lower.Contains("already completed", StringComparison.Ordinal) ||
            lower.Contains("already finished", StringComparison.Ordinal) ||
            lower.Contains("already made", StringComparison.Ordinal);
        if (!hasCompletionCue)
            return false;

        var hasTaskCue =
            lower.Contains("build", StringComparison.Ordinal) ||
            lower.Contains("built", StringComparison.Ordinal) ||
            lower.Contains("complete", StringComparison.Ordinal) ||
            lower.Contains("finished", StringComparison.Ordinal) ||
            lower.Contains("done", StringComparison.Ordinal);
        if (!hasTaskCue)
            return false;

        var subject = lower.Contains("wall", StringComparison.Ordinal)
            ? "wall"
            : "task";
        var answer = $"Zero time. The {subject} is already complete.";

        result = new GuardrailsPipelineResult
        {
            AnswerText = answer,
            RationaleLines =
            [
                "Goal: Determine remaining time to complete the task.",
                "Constraint: If the task is already complete, remaining work time is zero regardless of workforce rate.",
                $"Decision: {answer} (alternative considered: scaling worker-hours; rejected because no work remains to do)."
            ],
            TriggerRisk = "medium",
            TriggerWhy = "Detected completed-task duration trick question.",
            TriggerSource = "already_completed_task_solver",
            LlmRoundTrips = 0
        };
        return true;
    }

    private static bool TryResolveUnitComparisonQuestion(
        string userMessage,
        out GuardrailsPipelineResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = CollapseWhitespace(userMessage);
        var match = WeightComparisonRegex.Match(normalized);
        if (!match.Success)
            return false;

        var leftText = match.Groups["left"].Value.Trim();
        var rightText = match.Groups["right"].Value.Trim();
        if (!TryParseMassMeasurement(leftText, out var leftMass) ||
            !TryParseMassMeasurement(rightText, out var rightMass))
        {
            return false;
        }

        var comparator = (match.Groups["cmp"].Value ?? "").Trim().ToLowerInvariant();
        var isLessQuestion = comparator is "less" or "lighter";
        var delta = leftMass.Grams - rightMass.Grams;
        var epsilon = Math.Max(0.0001, Math.Max(leftMass.Grams, rightMass.Grams) * 1e-9);
        var isEqual = Math.Abs(delta) <= epsilon;

        var leftComparable = TrimTrailingPunctuation(leftText);
        var rightComparable = TrimTrailingPunctuation(rightText);

        string answerText;
        string decisionText;
        if (isEqual)
        {
            answerText = "Neither. They weigh the same.";
            decisionText = "Decision: They are equal after unit normalization.";
        }
        else
        {
            var leftWins = isLessQuestion ? delta < 0 : delta > 0;
            var selected = leftWins ? leftComparable : rightComparable;
            var relationWord = isLessQuestion ? "less" : "more";
            answerText = $"{selected} weighs {relationWord}.";
            var alternative = leftWins ? rightComparable : leftComparable;
            decisionText = $"Decision: {selected} (alternative considered: {alternative}; selected after comparing normalized mass values).";
        }

        var comparisonDetail =
            $"{leftComparable} = {FormatGrams(leftMass.Grams)}; {rightComparable} = {FormatGrams(rightMass.Grams)}";

        result = new GuardrailsPipelineResult
        {
            AnswerText = answerText,
            RationaleLines =
            [
                "Goal: Compare both quantities fairly using the same mass unit.",
                $"Constraint: Convert both sides into grams before deciding. {comparisonDetail}",
                decisionText
            ],
            TriggerRisk = "medium",
            TriggerWhy = "Detected deterministic weight-comparison question with convertible units.",
            TriggerSource = "mass_unit_comparison_solver",
            LlmRoundTrips = 0
        };
        return true;
    }

    private static bool TryResolveMeetingOverlapQuestion(
        string userMessage,
        out GuardrailsPipelineResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = CollapseWhitespace(userMessage);
        var lower = normalized.ToLowerInvariant();
        var asksCanAttendBoth =
            lower.Contains("attend both", StringComparison.Ordinal) ||
            lower.Contains("both in full", StringComparison.Ordinal) ||
            lower.Contains("attend both meetings", StringComparison.Ordinal);

        if (!asksCanAttendBoth || !lower.Contains("meeting", StringComparison.Ordinal))
            return false;

        var matches = TimeRangeRegex.Matches(normalized);
        if (matches.Count < 2)
            return false;

        if (!TryParseClockTime(matches[0].Groups["start"].Value, out var start1) ||
            !TryParseClockTime(matches[0].Groups["end"].Value, out var end1) ||
            !TryParseClockTime(matches[1].Groups["start"].Value, out var start2) ||
            !TryParseClockTime(matches[1].Groups["end"].Value, out var end2))
        {
            return false;
        }

        if (end1 <= start1 || end2 <= start2)
            return false;

        var overlapStart = Math.Max(start1, start2);
        var overlapEnd = Math.Min(end1, end2);
        var hasOverlap = overlapStart < overlapEnd;

        if (hasOverlap)
        {
            var overlapWindow = $"{FormatClockMinutes(overlapStart)} to {FormatClockMinutes(overlapEnd)}";
            result = new GuardrailsPipelineResult
            {
                AnswerText = $"No, you cannot attend both in full. The meetings overlap from {overlapWindow}.",
                RationaleLines =
                [
                    "Goal: Determine whether both meetings can be attended in full.",
                    "Constraint: To attend both fully, the meeting windows must not overlap.",
                    $"Decision: Cannot attend both in full; overlap is {overlapWindow}."
                ],
                TriggerRisk = "medium",
                TriggerWhy = "Detected deterministic meeting-overlap question.",
                TriggerSource = "meeting_overlap_solver",
                LlmRoundTrips = 0
            };
            return true;
        }

        result = new GuardrailsPipelineResult
        {
            AnswerText = "Yes, you can attend both in full. The meeting windows do not overlap.",
            RationaleLines =
            [
                "Goal: Determine whether both meetings can be attended in full.",
                "Constraint: To attend both fully, the meeting windows must not overlap.",
                "Decision: Can attend both in full; there is no overlap."
            ],
            TriggerRisk = "medium",
            TriggerWhy = "Detected deterministic meeting-overlap question.",
            TriggerSource = "meeting_overlap_solver",
            LlmRoundTrips = 0
        };
        return true;
    }

    private static bool TryResolveFamilyPhotoRelationPuzzle(
        string userMessage,
        out GuardrailsPipelineResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = CollapseWhitespace(userMessage).Replace('’', '\'');
        var lower = normalized.ToLowerInvariant();

        if (!LooksLikePhotoIdentityQuestion(lower))
            return false;

        var relationMatch = FamilyPhotoRelationRegex.Match(lower);
        if (!relationMatch.Success)
            return false;

        var hasOnlyChildAnchor = HasOnlyChildAnchor(lower) || relationMatch.Groups["rhsOnly"].Success;
        if (!hasOnlyChildAnchor)
            return false;

        var lhsRelation = relationMatch.Groups["lhsRel"].Value.ToLowerInvariant();
        var rhsParent = relationMatch.Groups["rhsParent"].Value.ToLowerInvariant();
        var rhsChild = relationMatch.Groups["rhsChild"].Value.ToLowerInvariant();
        var photoGender = NormalizeGenderToken(relationMatch.Groups["photoGender"].Value);
        var inferredSpeakerGender = rhsChild == "son" ? "male" : "female";
        var explicitSpeakerGender = DetectSpeakerGender(lower);
        var speakerGender = explicitSpeakerGender ?? inferredSpeakerGender;

        if (explicitSpeakerGender is not null &&
            !string.Equals(explicitSpeakerGender, inferredSpeakerGender, StringComparison.Ordinal))
        {
            result = new GuardrailsPipelineResult
            {
                AnswerText = "The clues conflict, so the relationship cannot be resolved without clarification.",
                RationaleLines =
                [
                    "Goal: Identify who is in the photograph from the relationship statement.",
                    $"Constraint: The only-child clue conflicts with 'my {rhsParent}'s {rhsChild}' for the stated speaker gender.",
                    "Decision: Mark as inconsistent and ask for clarification instead of guessing."
                ],
                TriggerRisk = "medium",
                TriggerWhy = "Detected family-relation puzzle with internally conflicting clues.",
                TriggerSource = "family_photo_relation_conflict",
                LlmRoundTrips = 0
            };
            return true;
        }

        var kinship = ResolveKinship(lhsRelation, photoGender);
        if (kinship is null)
            return false;

        var speakerPossessive = speakerGender switch
        {
            "female" => "her",
            "male" => "his",
            _ => "their"
        };
        var answer = $"The person in the photograph is {speakerPossessive} {kinship}.";
        var rejectedAlternative = kinship is "father" or "mother" or "parent"
            ? $"{speakerPossessive} child"
            : $"{speakerPossessive} parent";

        result = new GuardrailsPipelineResult
        {
            AnswerText = answer,
            RationaleLines =
            [
                "Goal: Identify who is in the photograph from the relationship statement.",
                $"Constraint: With an only-child anchor, 'my {rhsParent}'s {rhsChild}' resolves to the speaker, so the photographed person's {lhsRelation} is the speaker.",
                $"Decision: {answer} (alternative considered: {rejectedAlternative}; rejected because it violates the stated relation)."
            ],
            TriggerRisk = "medium",
            TriggerWhy = "Detected deterministic family-relation photo puzzle.",
            TriggerSource = "family_photo_relation_solver",
            LlmRoundTrips = 0
        };
        return true;
    }

    private static bool LooksLikePhotoIdentityQuestion(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasPhotoNoun =
            lower.Contains("photograph", StringComparison.Ordinal) ||
            lower.Contains("photo", StringComparison.Ordinal) ||
            lower.Contains("picture", StringComparison.Ordinal);
        if (!hasPhotoNoun)
            return false;

        return lower.Contains("who is in", StringComparison.Ordinal) ||
               lower.Contains("who's in", StringComparison.Ordinal) ||
               lower.Contains("whos in", StringComparison.Ordinal);
    }

    private static bool HasOnlyChildAnchor(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("brothers and sisters, i have none", StringComparison.Ordinal) ||
               lower.Contains("brothers and sisters i have none", StringComparison.Ordinal) ||
               lower.Contains("i have no siblings", StringComparison.Ordinal) ||
               lower.Contains("i don't have siblings", StringComparison.Ordinal) ||
               lower.Contains("i do not have siblings", StringComparison.Ordinal) ||
               lower.Contains("i have no brother or sister", StringComparison.Ordinal) ||
               lower.Contains("i am an only child", StringComparison.Ordinal) ||
               lower.Contains("i'm an only child", StringComparison.Ordinal);
    }

    private static string? DetectSpeakerGender(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return null;

        var match = SpeakerGenderRegex.Match(lower);
        if (!match.Success)
            return null;

        return NormalizeGenderToken(match.Groups["gender"].Value);
    }

    private static string? NormalizeGenderToken(string token)
    {
        var normalized = (token ?? "").Trim().ToLowerInvariant();
        return normalized switch
        {
            "man" or "boy" => "male",
            "woman" or "girl" => "female",
            _ => null
        };
    }

    private static string? ResolveKinship(string lhsRelation, string? photoGender)
    {
        var lower = (lhsRelation ?? "").Trim().ToLowerInvariant();
        var isPhotoChildOfSpeaker = lower is "father" or "mother";
        var isPhotoParentOfSpeaker = lower is "son" or "daughter";
        if (!isPhotoChildOfSpeaker && !isPhotoParentOfSpeaker)
            return null;

        if (isPhotoChildOfSpeaker)
        {
            return photoGender switch
            {
                "male" => "son",
                "female" => "daughter",
                _ => "child"
            };
        }

        return photoGender switch
        {
            "male" => "father",
            "female" => "mother",
            _ => "parent"
        };
    }

    private static bool TryResolveAmbiguousReferentQuestion(
        string userMessage,
        out GuardrailsPipelineResult result)
    {
        result = null!;
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var normalized = CollapseWhitespace(userMessage);
        var lower = normalized.ToLowerInvariant();
        var becauseIndex = lower.IndexOf(" because ", StringComparison.Ordinal);
        if (becauseIndex <= 0 || !lower.Contains(" who ", StringComparison.Ordinal))
            return false;

        var preBecause = normalized[..becauseIndex];
        var names = ExtractProperNames(preBecause);
        if (names.Count < 2)
            return false;

        var candidateA = names[0];
        var candidateB = names[1];
        var afterBecause = normalized[(becauseIndex + " because ".Length)..];
        var becauseClause = ExtractClauseUntilSentenceBreak(afterBecause);
        var lowerClause = becauseClause.ToLowerInvariant();
        var lowerA = candidateA.ToLowerInvariant();
        var lowerB = candidateB.ToLowerInvariant();

        if (StartsWithWord(lowerClause, lowerA) || StartsWithWord(lowerClause, lowerB))
        {
            var resolved = StartsWithWord(lowerClause, lowerA) ? candidateA : candidateB;
            var alternate = string.Equals(resolved, candidateA, StringComparison.OrdinalIgnoreCase)
                ? candidateB
                : candidateA;

            result = new GuardrailsPipelineResult
            {
                AnswerText = $"{resolved}.",
                RationaleLines =
                [
                    "Goal: Resolve who the referent points to in the sentence.",
                    $"Constraint: The causal clause explicitly repeats '{resolved}', so the referent is disambiguated.",
                    $"Decision: {resolved}. (alternative considered: {alternate}; rejected because it is not the explicit referent)"
                ],
                TriggerRisk = "medium",
                TriggerWhy = "Detected explicit name-based referent disambiguation.",
                TriggerSource = "referent_explicit_name",
                LlmRoundTrips = 0
            };
            return true;
        }

        var startsWithAmbiguousPronoun =
            StartsWithWord(lowerClause, "he") ||
            StartsWithWord(lowerClause, "she") ||
            StartsWithWord(lowerClause, "they");

        if (!startsWithAmbiguousPronoun)
            return false;

        result = new GuardrailsPipelineResult
        {
            AnswerText =
                $"It cannot be determined from the sentence alone; the pronoun could refer to {candidateA} or {candidateB}. " +
                $"Who did you mean: {candidateA} or {candidateB}?",
            RationaleLines =
            [
                "Goal: Resolve an underspecified pronoun referent.",
                $"Constraint: Two candidate names are present ({candidateA}, {candidateB}) and the pronoun does not uniquely identify one.",
                $"Decision: Mark as ambiguous and request clarification ({candidateA} or {candidateB})."
            ],
            TriggerRisk = "medium",
            TriggerWhy = "Detected ambiguous pronoun with multiple valid named referents.",
            TriggerSource = "referent_ambiguity",
            LlmRoundTrips = 0
        };
        return true;
    }

    private static bool TryParseMassMeasurement(string text, out (double Grams, string Unit, double Quantity) measurement)
    {
        measurement = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = MassQuantityRegex.Match(text);
        if (!match.Success)
            return false;

        var numberText = match.Groups["num"].Value.Trim();
        var unitText = match.Groups["unit"].Value.Trim().ToLowerInvariant();
        if (!TryParseFlexibleNumber(numberText, out var quantity))
            return false;
        if (quantity < 0)
            return false;

        double grams;
        string canonicalUnit;
        switch (unitText)
        {
            case "pound":
            case "pounds":
            case "lb":
            case "lbs":
                grams = quantity * 453.59237;
                canonicalUnit = "pounds";
                break;
            case "ounce":
            case "ounces":
            case "oz":
                grams = quantity * 28.349523125;
                canonicalUnit = "ounces";
                break;
            case "kilogram":
            case "kilograms":
            case "kg":
            case "kgs":
                grams = quantity * 1000.0;
                canonicalUnit = "kilograms";
                break;
            case "gram":
            case "grams":
            case "g":
                grams = quantity;
                canonicalUnit = "grams";
                break;
            default:
                return false;
        }

        measurement = (grams, canonicalUnit, quantity);
        return true;
    }

    private static bool TryParseFlexibleNumber(string raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var cleaned = raw.Trim().ToLowerInvariant().Replace(",", "", StringComparison.Ordinal);
        if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            value = numeric;
            return true;
        }

        var tokens = cleaned
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return false;

        var total = 0;
        var current = 0;
        var sawNumberToken = false;
        foreach (var token in tokens)
        {
            if (string.Equals(token, "and", StringComparison.OrdinalIgnoreCase))
                continue;

            if (NumberWords.TryGetValue(token, out var numberWord))
            {
                current += numberWord;
                sawNumberToken = true;
                continue;
            }

            if (string.Equals(token, "hundred", StringComparison.OrdinalIgnoreCase))
            {
                current = current == 0 ? 100 : current * 100;
                sawNumberToken = true;
                continue;
            }

            if (string.Equals(token, "thousand", StringComparison.OrdinalIgnoreCase))
            {
                current = current == 0 ? 1 : current;
                total += current * 1000;
                current = 0;
                sawNumberToken = true;
                continue;
            }

            return false;
        }

        if (!sawNumberToken)
            return false;

        value = total + current;
        return true;
    }

    private void WriteSpecialCaseAudit(GuardrailsPipelineResult specialCase)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_TRIGGERED",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["risk"] = string.IsNullOrWhiteSpace(specialCase.TriggerRisk) ? "medium" : specialCase.TriggerRisk,
                ["source"] = specialCase.TriggerSource,
                ["why"] = specialCase.TriggerWhy
            }
        });

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_DECISION",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["goal"] = specialCase.RationaleLines.FirstOrDefault() ?? "",
                ["selectedAction"] = specialCase.AnswerText,
                ["constraint"] = specialCase.RationaleLines.Skip(1).FirstOrDefault() ?? "",
                ["triggerRisk"] = string.IsNullOrWhiteSpace(specialCase.TriggerRisk) ? "medium" : specialCase.TriggerRisk
            }
        });
    }

    private static string CollapseWhitespace(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var chunks = raw
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', chunks).Trim();
    }

    private static string TrimTrailingPunctuation(string raw)
        => (raw ?? "").Trim().TrimEnd('.', '?', '!', ';', ':', ',');

    private static string FormatGrams(double grams)
        => $"{grams:0.###} g";

    private static List<string> ExtractProperNames(string text)
    {
        var names = new List<string>();
        foreach (Match match in ProperNameRegex.Matches(text ?? ""))
        {
            var candidate = match.Value.Trim();
            if (candidate.Length == 0 || NonNameTokens.Contains(candidate))
                continue;

            candidate = NormalizeName(candidate);
            if (!names.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                names.Add(candidate);

            if (names.Count >= 2)
                break;
        }

        return names;
    }

    private static string ExtractClauseUntilSentenceBreak(string text)
    {
        var candidate = (text ?? "").Trim();
        if (candidate.Length == 0)
            return candidate;

        var breakIndex = candidate.IndexOfAny(['.', '?', '!']);
        if (breakIndex >= 0)
            candidate = candidate[..breakIndex];
        return candidate.Trim();
    }

    private static bool TryParseClockTime(string raw, out int minutes)
    {
        minutes = 0;
        var text = (raw ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0)
            return false;

        var isAm = false;
        var isPm = false;
        if (text.EndsWith("am", StringComparison.Ordinal))
        {
            isAm = true;
            text = text[..^2].Trim();
        }
        else if (text.EndsWith("pm", StringComparison.Ordinal))
        {
            isPm = true;
            text = text[..^2].Trim();
        }

        if (text.Length == 0)
            return false;

        var parts = text.Split(':', StringSplitOptions.TrimEntries);
        if (!int.TryParse(parts[0], out var hour))
            return false;

        var minute = 0;
        if (parts.Length > 1 && !int.TryParse(parts[1], out minute))
            return false;
        if (minute < 0 || minute > 59)
            return false;

        if (isAm || isPm)
        {
            if (hour < 1 || hour > 12)
                return false;

            hour %= 12;
            if (isPm)
                hour += 12;
        }
        else if (hour < 0 || hour > 23)
        {
            return false;
        }

        minutes = (hour * 60) + minute;
        return true;
    }

    private static string FormatClockMinutes(int minutes)
    {
        var bounded = ((minutes % 1440) + 1440) % 1440;
        var hour24 = bounded / 60;
        var minute = bounded % 60;
        var hour12 = hour24 % 12;
        if (hour12 == 0)
            hour12 = 12;
        return $"{hour12}:{minute:00}";
    }

    private static bool StartsWithWord(string textLower, string prefixLower)
    {
        if (!textLower.StartsWith(prefixLower, StringComparison.Ordinal))
            return false;

        if (textLower.Length == prefixLower.Length)
            return true;

        var next = textLower[prefixLower.Length];
        return !char.IsLetterOrDigit(next) && next != '\'';
    }

    private static string NormalizeName(string raw)
    {
        var trimmed = (raw ?? "").Trim().Trim('.', ',', '!', '?', '"', '\'');
        if (trimmed.Length == 0)
            return trimmed;

        if (trimmed.Length == 1)
            return trimmed.ToUpperInvariant();

        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
    }

    private void WriteFallback(string reason)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_FALLBACK",
            Result = reason
        });
    }

    private static async Task<T?> RunBoundedAsync<T>(
        Func<CancellationToken, Task<T?>> step,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        where T : class
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);

        try
        {
            return await step(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeDialogueOrRoleplayTask(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        return lower.Contains("roleplay", StringComparison.Ordinal) ||
               lower.Contains("role-play", StringComparison.Ordinal) ||
               lower.Contains("write a dialogue", StringComparison.Ordinal) ||
               lower.Contains("write dialogue", StringComparison.Ordinal) ||
               lower.Contains("script between", StringComparison.Ordinal) ||
               lower.Contains("fictional conversation", StringComparison.Ordinal);
    }
}

internal sealed record GuardrailsTriggerDecision(
    bool Triggered,
    string Risk,
    string Why,
    string Source,
    int LlmRoundTrips);

internal sealed class GuardrailsDetector
{
    private readonly ILlmClient _llm;

    private static readonly Regex DistanceCueRegex = new(
        @"\b\d+\s*(?:m|meter|meters|km|minute|minutes|min|away)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ServiceCueRegex = new(
        @"\b(?:gas station|car wash|airport|pharmacy|ups store|post office|bank|hardware store|library|garage|hotel|dry[-\s]?clean(?:ing|er)|repair shop|mechanic)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RequiredObjectCueRegex = new(
        @"\b(?:car|passport|prescription|package|key|id|license|ticket|jacket|laptop|device)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChoicePatternRegex = new(
        @"\b(?:should i|do i|would it be better to|is it better to)\b[\s\S]{0,120}\bor\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GuardrailsDetector(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<GuardrailsTriggerDecision?> DetectAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return new GuardrailsTriggerDecision(false, "low", "empty message", "heuristic", 0);
        }

        var normalized = StripMarkdownMarkers(userMessage);
        var lower = normalized.ToLowerInvariant();
        var hasChoicePattern = ChoicePatternRegex.IsMatch(normalized);
        var hasServiceCue = ServiceCueRegex.IsMatch(normalized);
        var hasRequiredObjectCue = RequiredObjectCueRegex.IsMatch(normalized);
        var hasDistanceCue = DistanceCueRegex.IsMatch(normalized);
        var hasNeedsCue = lower.Contains("needs ", StringComparison.Ordinal) ||
                          lower.Contains("requires ", StringComparison.Ordinal) ||
                          lower.Contains("before ", StringComparison.Ordinal);

        if (hasChoicePattern && (hasServiceCue || hasRequiredObjectCue || hasDistanceCue || hasNeedsCue))
        {
            var risk = hasDistanceCue || hasServiceCue ? "high" : "medium";
            var why = hasDistanceCue
                ? "Detected goal-choice conflict with distance/time cue."
                : "Detected service/object precondition conflict between options.";
            return new GuardrailsTriggerDecision(true, risk, why, "heuristic", 0);
        }

        // Tiny-model fallback in Auto mode for subtle cases.
        if (!hasChoicePattern)
            return new GuardrailsTriggerDecision(false, "low", "No choice conflict pattern found.", "heuristic", 0);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Classify whether this user question is a goal-conflict trick prompt. " +
                "Return STRICT JSON only with keys: risk, why, suggest_guardrails. " +
                "risk must be low|medium|high. suggest_guardrails must be true or false."),
            ChatMessage.User(normalized)
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 96, cancellationToken);
            var parsed = ParseTinyTrigger(llm.Content);
            if (parsed is null)
            {
                return new GuardrailsTriggerDecision(false, "low", "Tiny trigger returned malformed JSON.", "tiny_llm", 1);
            }

            return new GuardrailsTriggerDecision(
                parsed.SuggestGuardrails,
                parsed.Risk,
                string.IsNullOrWhiteSpace(parsed.Why) ? "Tiny trigger classified as non-trick." : parsed.Why,
                "tiny_llm",
                1);
        }
        catch
        {
            return new GuardrailsTriggerDecision(false, "low", "Tiny trigger unavailable.", "tiny_llm", 0);
        }
    }

    private static TinyTriggerResult? ParseTinyTrigger(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = StripCodeFence(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var risk = ReadString(root, "risk");
            var why = ReadString(root, "why");
            var suggest = ReadBool(root, "suggest_guardrails");
            risk = risk?.ToLowerInvariant() switch
            {
                "high" => "high",
                "medium" => "medium",
                _ => "low"
            };

            return new TinyTriggerResult(
                Risk: risk,
                Why: why ?? "",
                SuggestGuardrails: suggest);
        }
        catch
        {
            return null;
        }
    }

    private sealed record TinyTriggerResult(string Risk, string Why, bool SuggestGuardrails);

    private static string StripMarkdownMarkers(string text)
        => (text ?? "").Replace("**", "", StringComparison.Ordinal);

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];

        return inner.Trim();
    }

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.String)
            return node.GetString();
        return null;
    }

    private static bool ReadBool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return false;
        return node.ValueKind == JsonValueKind.True;
    }
}

internal sealed record GoalInference(
    string PrimaryGoal,
    IReadOnlyList<string> AlternativeGoals,
    double Confidence,
    int LlmRoundTrips);

internal sealed class GoalInferencer
{
    private readonly ILlmClient _llm;

    public GoalInferencer(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<GoalInference?> InferAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Infer the practical real-world goal behind the user's question. " +
                "Return STRICT JSON only: " +
                "{\"primary_goal\":\"...\",\"alternative_goals\":[\"...\"],\"confidence\":0.0}"),
            ChatMessage.User(userMessage)
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 140, cancellationToken);
            var parsed = ParseGoalInference(llm.Content);
            if (parsed is not null)
                return parsed with { LlmRoundTrips = 1 };
        }
        catch
        {
            // Heuristic fallback below.
        }

        var heuristic = InferHeuristically(userMessage);
        return heuristic is null
            ? null
            : heuristic with { LlmRoundTrips = 0 };
    }

    private static GoalInference? ParseGoalInference(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = StripCodeFence(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var primary = ReadString(root, "primary_goal");
            if (string.IsNullOrWhiteSpace(primary))
                return null;

            var alternatives = ReadStringArray(root, "alternative_goals");
            var confidence = ReadDouble(root, "confidence");
            return new GoalInference(
                PrimaryGoal: primary.Trim(),
                AlternativeGoals: alternatives,
                Confidence: confidence,
                LlmRoundTrips: 0);
        }
        catch
        {
            return null;
        }
    }

    private static GoalInference? InferHeuristically(string message)
    {
        var lower = (message ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return null;

        var goal = lower switch
        {
            var s when s.Contains("gas station", StringComparison.Ordinal) => "Refuel the vehicle.",
            var s when s.Contains("airport", StringComparison.Ordinal) => "Complete airport/travel requirements before departure.",
            var s when s.Contains("pharmacy", StringComparison.Ordinal) => "Pick up the prescription from the pharmacy.",
            var s when s.Contains("library hold", StringComparison.Ordinal) => "Collect the held library item before it expires.",
            var s when s.Contains("repair", StringComparison.Ordinal) => "Collect the repaired item in person.",
            var s when s.Contains("check-in", StringComparison.Ordinal) => "Complete check-in with the required ID.",
            var s when s.Contains("key cut", StringComparison.Ordinal) => "Bring the physical key to get a duplicate cut.",
            var s when s.Contains("dry-clean", StringComparison.Ordinal) => "Collect the dry-cleaning item before close.",
            _ => "Choose the option that actually completes the real-world goal."
        };

        return new GoalInference(
            PrimaryGoal: goal,
            AlternativeGoals: [],
            Confidence: 0.58,
            LlmRoundTrips: 0);
    }

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];
        return inner.Trim();
    }

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
            return [];

        var items = new List<string>();
        foreach (var child in node.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.String)
                continue;
            var value = child.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                items.Add(value);
        }
        return items;
    }

    private static double ReadDouble(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return 0.5;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out var value))
            return Math.Clamp(value, 0.0, 1.0);
        return 0.5;
    }
}

internal sealed record EntityFact(string Name, string Kind, bool Required);

internal sealed record ActionOption(
    string Label,
    IReadOnlyList<string> Preconditions,
    IReadOnlyList<string> Effects);

internal sealed record EntityExtraction(
    IReadOnlyList<EntityFact> Entities,
    IReadOnlyList<ActionOption> Options,
    int LlmRoundTrips);

internal static class EntityRequirementHeuristics
{
    private static readonly Dictionary<string, string[]> CanonicalToAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["car"] =
        [
            "car", "cars", "vehicle", "vehicles", "automobile", "automobiles", "auto", "autos", "suv", "van", "truck"
        ],
        ["id"] =
        [
            "id", "i.d.", "photo id", "identification", "license", "driver license", "drivers license", "driver's license"
        ],
        ["package"] = ["package", "packages", "parcel", "parcels", "box", "boxes"],
        ["key"] = ["key", "keys"],
        ["ticket"] = ["ticket", "tickets", "boarding pass", "pass"],
        ["jacket"] = ["jacket", "jackets", "coat", "coats"],
        ["laptop"] = ["laptop", "laptops", "notebook", "notebooks", "computer", "computers"],
        ["device"] = ["device", "devices", "phone", "phones", "tablet", "tablets"]
    };

    private static readonly Dictionary<string, string[]> CanonicalToActionImplications = new(StringComparer.OrdinalIgnoreCase)
    {
        ["car"] = ["drive", "driving", "park", "parking", "refuel", "gas up"],
        ["key"] = ["unlock", "start ignition"],
        ["ticket"] = ["board", "boarding"],
        ["id"] = ["check in", "check-in", "security line", "tsa"]
    };

    private static readonly Dictionary<string, string> AliasToCanonical = BuildAliasMap();

    public static IReadOnlyList<string> DetectRequiredEntities(string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return [];

        var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliases) in CanonicalToAliases)
        {
            if (aliases.Any(alias => ContainsPhraseOrToken(lower, alias)))
                detected.Add(canonical);
        }

        return detected.ToList();
    }

    public static bool OptionMentionsEntity(string optionLabelLower, string entityName)
    {
        var aliases = GetEntityAliases(entityName);
        foreach (var alias in aliases)
        {
            if (ContainsPhraseOrToken(optionLabelLower, alias))
                return true;
        }

        return false;
    }

    public static bool OptionImpliesEntityUsage(string optionLabelLower, string entityName)
    {
        var canonical = CanonicalizeEntityName(entityName);
        if (!CanonicalToActionImplications.TryGetValue(canonical, out var implications))
            return false;

        foreach (var implication in implications)
        {
            if (ContainsPhraseOrToken(optionLabelLower, implication))
                return true;
        }

        return false;
    }

    public static string CanonicalizeEntityName(string entityName)
    {
        var normalized = NormalizeEntityText(entityName);
        if (normalized.Length == 0)
            return normalized;

        if (AliasToCanonical.TryGetValue(normalized, out var canonical))
            return canonical;

        var singular = normalized.EndsWith('s') ? normalized[..^1] : normalized;
        if (AliasToCanonical.TryGetValue(singular, out canonical))
            return canonical;

        return singular;
    }

    private static IReadOnlyList<string> GetEntityAliases(string entityName)
    {
        var canonical = CanonicalizeEntityName(entityName);
        if (canonical.Length == 0)
            return [];

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { canonical };
        if (CanonicalToAliases.TryGetValue(canonical, out var knownAliases))
        {
            foreach (var alias in knownAliases)
                aliases.Add(alias);
        }

        var normalizedEntity = NormalizeEntityText(entityName);
        if (!string.IsNullOrWhiteSpace(normalizedEntity))
            aliases.Add(normalizedEntity);

        return aliases.ToList();
    }

    private static Dictionary<string, string> BuildAliasMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canonical, aliases) in CanonicalToAliases)
        {
            map[canonical] = canonical;
            foreach (var alias in aliases)
                map[NormalizeEntityText(alias)] = canonical;
        }

        return map;
    }

    private static string NormalizeEntityText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(" ",
            value.Trim()
                .ToLowerInvariant()
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsPhraseOrToken(string haystackLower, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystackLower) || string.IsNullOrWhiteSpace(needle))
            return false;

        var needleLower = NormalizeEntityText(needle);
        if (needleLower.Length == 0)
            return false;

        var index = 0;
        while (true)
        {
            index = haystackLower.IndexOf(needleLower, index, StringComparison.Ordinal);
            if (index < 0)
                return false;

            var beforeOk = index == 0 || !char.IsLetterOrDigit(haystackLower[index - 1]);
            var afterIndex = index + needleLower.Length;
            var afterOk = afterIndex >= haystackLower.Length || !char.IsLetterOrDigit(haystackLower[afterIndex]);
            if (beforeOk && afterOk)
                return true;

            index++;
        }
    }
}

internal sealed class EntityExtractor
{
    private readonly ILlmClient _llm;

    private static readonly Regex ChoiceRegex = new(
        @"\b(?:should\s+(?:i|you|we|he|she|they)|do\s+(?:i|you|we)|is it better to|would it be better to)\s+(?<a>.+?)\s+or\s+(?<b>.+?)(?:[?.!]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public EntityExtractor(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<EntityExtraction?> ExtractAsync(
        string userMessage,
        CancellationToken cancellationToken)
    {
        var heuristic = ExtractHeuristically(userMessage);
        if (heuristic.Options.Count >= 2)
            return heuristic with { LlmRoundTrips = 0 };

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Extract entities and action options from the user question. " +
                "Return STRICT JSON only with schema: " +
                "{\"entities\":[{\"name\":\"...\",\"kind\":\"required_object|destination|other\",\"required\":true}]," +
                "\"options\":[{\"label\":\"...\",\"preconditions\":[\"...\"],\"effects\":[\"...\"]}]}"),
            ChatMessage.User(userMessage)
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 220, cancellationToken);
            var parsed = ParseExtraction(llm.Content);
            if (parsed is not null && parsed.Options.Count >= 2)
                return parsed with { LlmRoundTrips = 1 };
        }
        catch
        {
            // Return heuristic fallback.
        }

        return heuristic.Options.Count >= 2
            ? heuristic with { LlmRoundTrips = 0 }
            : null;
    }

    private static EntityExtraction ExtractHeuristically(string text)
    {
        var cleaned = (text ?? "").Replace("**", "", StringComparison.Ordinal);
        var options = new List<ActionOption>();

        var choice = ChoiceRegex.Match(cleaned);
        if (choice.Success)
        {
            var first = NormalizeOption(choice.Groups["a"].Value);
            var second = NormalizeOption(choice.Groups["b"].Value);
            if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
            {
                options.Add(new ActionOption(first, [], []));
                options.Add(new ActionOption(second, [], []));
            }
        }
        else
        {
            var choiceClause = ExtractChoiceClause(cleaned);
            var lower = choiceClause.ToLowerInvariant();
            var split = lower.IndexOf(" or ", StringComparison.Ordinal);
            if (split > 0)
            {
                var left = NormalizeOption(choiceClause[..split]);
                var right = NormalizeOption(choiceClause[(split + 4)..]);
                if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                {
                    options.Add(new ActionOption(left, [], []));
                    options.Add(new ActionOption(right, [], []));
                }
            }
        }

        var entities = ExtractEntityFacts(cleaned);
        return new EntityExtraction(entities, options, 0);
    }

    private static IReadOnlyList<EntityFact> ExtractEntityFacts(string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        var entities = new List<EntityFact>();

        var detected = EntityRequirementHeuristics.DetectRequiredEntities(lower);
        foreach (var entity in detected)
        {
            entities.Add(new EntityFact(
                Name: entity,
                Kind: "required_object",
                Required: true));
        }

        AddIfContains("passport");
        AddIfContains("prescription");

        return entities;

        void AddIfContains(string value)
        {
            if (!lower.Contains(value, StringComparison.Ordinal))
                return;

            entities.Add(new EntityFact(
                Name: EntityRequirementHeuristics.CanonicalizeEntityName(value),
                Kind: "required_object",
                Required: true));
        }
    }

    private static EntityExtraction? ParseExtraction(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = StripCodeFence(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var entities = new List<EntityFact>();
            if (root.TryGetProperty("entities", out var entitiesNode) &&
                entitiesNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in entitiesNode.EnumerateArray())
                {
                    var name = ReadString(node, "name");
                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var kind = ReadString(node, "kind") ?? "other";
                    var required = ReadBool(node, "required");
                    var normalizedName = required
                        ? EntityRequirementHeuristics.CanonicalizeEntityName(name)
                        : name.Trim();
                    entities.Add(new EntityFact(normalizedName, kind.Trim(), required));
                }
            }

            var options = new List<ActionOption>();
            if (root.TryGetProperty("options", out var optionsNode) &&
                optionsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in optionsNode.EnumerateArray())
                {
                    var label = ReadString(node, "label");
                    if (string.IsNullOrWhiteSpace(label))
                        continue;

                    options.Add(new ActionOption(
                        Label: NormalizeOption(label),
                        Preconditions: ReadStringArray(node, "preconditions"),
                        Effects: ReadStringArray(node, "effects")));
                }
            }

            return new EntityExtraction(entities, options, 0);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeOption(string value)
    {
        var cleaned = (value ?? "")
            .Trim()
            .Trim('"', '\'', '*')
            .TrimEnd('.', '?', '!');

        string[] prefixes =
        [
            "should i ",
            "should you ",
            "should we ",
            "should he ",
            "should she ",
            "should they ",
            "do i ",
            "do you ",
            "do we ",
            "is it better to ",
            "would it be better to "
        ];

        foreach (var prefix in prefixes)
        {
            if (!cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            cleaned = cleaned[prefix.Length..];
            break;
        }

        return cleaned.Trim();
    }

    private static string ExtractChoiceClause(string text)
    {
        var candidate = (text ?? "").Trim();
        if (candidate.Length == 0)
            return candidate;

        var questionMark = candidate.LastIndexOf('?');
        if (questionMark >= 0)
            candidate = candidate[..questionMark].Trim();

        var sentenceBreak = Math.Max(
            candidate.LastIndexOf('.'),
            Math.Max(candidate.LastIndexOf('!'), candidate.LastIndexOf(';')));

        if (sentenceBreak >= 0 && sentenceBreak + 1 < candidate.Length)
            candidate = candidate[(sentenceBreak + 1)..].Trim();

        return candidate;
    }

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];
        return inner.Trim();
    }

    private static string? ReadString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return null;
        return node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    }

    private static bool ReadBool(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node))
            return false;
        return node.ValueKind == JsonValueKind.True;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                continue;
            var value = item.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }
        return values;
    }
}

internal sealed record ConstraintSet(IReadOnlyList<string> Constraints, int LlmRoundTrips);

internal sealed class ConstraintBuilder
{
    private readonly ILlmClient _llm;

    public ConstraintBuilder(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    public async Task<ConstraintSet?> BuildAsync(
        string userMessage,
        GoalInference goal,
        EntityExtraction entities,
        CancellationToken cancellationToken)
    {
        var optionsText = string.Join(" | ", entities.Options.Select(o => o.Label));
        var entityText = string.Join(", ", entities.Entities.Where(e => e.Required).Select(e => e.Name));

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Build first-principles constraints for selecting the correct option. " +
                "Return STRICT JSON only: {\"constraints\":[\"...\"]}. " +
                "Each constraint must be short and testable."),
            ChatMessage.User(
                $"question={userMessage}\n" +
                $"goal={goal.PrimaryGoal}\n" +
                $"required_entities={entityText}\n" +
                $"options={optionsText}")
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 160, cancellationToken);
            var parsed = ParseConstraintSet(llm.Content);
            if (parsed is not null && parsed.Constraints.Count > 0)
                return parsed with { LlmRoundTrips = 1 };
        }
        catch
        {
            // Heuristic fallback below.
        }

        var fallback = BuildHeuristicConstraints(userMessage, goal, entities);
        return fallback.Constraints.Count == 0 ? null : fallback;
    }

    private static ConstraintSet BuildHeuristicConstraints(
        string userMessage,
        GoalInference goal,
        EntityExtraction entities)
    {
        var constraints = new List<string>
        {
            $"Apply first-principles checks: the option must be physically feasible and directly satisfy the goal: {goal.PrimaryGoal}"
        };

        var requiredEntities = entities.Entities
            .Where(e => e.Required)
            .Select(e => e.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (requiredEntities.Count > 0)
        {
            constraints.Add(
                $"Required objects must be physically available: {string.Join(", ", requiredEntities)}");
        }

        var lower = (userMessage ?? "").ToLowerInvariant();
        if (lower.Contains("before", StringComparison.Ordinal))
            constraints.Add("Respect any ordering requirement implied by 'before'.");
        if (lower.Contains("needs", StringComparison.Ordinal) || lower.Contains("requires", StringComparison.Ordinal))
            constraints.Add("Respect explicit prerequisites stated in the question.");

        return new ConstraintSet(constraints, 0);
    }

    private static ConstraintSet? ParseConstraintSet(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var cleaned = StripCodeFence(raw);
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var constraints = new List<string>();
            if (root.TryGetProperty("constraints", out var node) &&
                node.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in node.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        continue;
                    var value = item.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        constraints.Add(value);
                }
            }

            return new ConstraintSet(constraints, 0);
        }
        catch
        {
            return null;
        }
    }

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];
        return inner.Trim();
    }
}

internal sealed record EvaluatedOption(
    string Label,
    double Score,
    string Notes,
    int PrinciplePassCount);

internal sealed record EvaluationDecision(
    string SelectedAction,
    string ConstraintSummary,
    IReadOnlyList<EvaluatedOption> EvaluatedOptions);

internal sealed class OptionEvaluator
{
    private static readonly HashSet<string> NonActionOptionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "preconditions",
        "precondition",
        "action options",
        "options",
        "constraints",
        "constraint",
        "goal",
        "goals",
        "decision",
        "analysis",
        "steps",
        "step",
        "reasoning",
        "rationale"
    };

    private static readonly string[] PhysicalActionHints =
    [
        "go ",
        "go to",
        "bring",
        "take",
        "drive",
        "walk",
        "collect",
        "pick up",
        "pay",
        "check in",
        "downstairs",
        "kiosk",
        "desk",
        "in person"
    ];

    private static readonly string[] IndirectActionHints =
    [
        "call ",
        "text ",
        "email ",
        "send ",
        "message "
    ];

    private static readonly string[] StallingActionHints =
    [
        "wait ",
        "stay ",
        "later",
        "eventually"
    ];

    private static readonly string[] GoalCompletionHints =
    [
        "collect",
        "pick up",
        "bring",
        "take",
        "go ",
        "pay",
        "check in",
        "refuel",
        "submit",
        "arrive"
    ];

    private static readonly string[] SequenceLeadingHints =
    [
        "first",
        "before",
        "pay",
        "bring"
    ];

    private static readonly string[] SequenceTrailingHints =
    [
        "after",
        "later",
        "wait "
    ];

    private static readonly HashSet<string> GoalStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the",
        "and",
        "for",
        "with",
        "from",
        "that",
        "this",
        "must",
        "option",
        "choose",
        "real-world"
    };

    public EvaluationDecision? Evaluate(
        string userMessage,
        GoalInference goal,
        EntityExtraction entities,
        ConstraintSet constraints)
    {
        if (entities.Options.Count == 0)
            return null;

        var candidateOptions = entities.Options
            .Where(o => IsActionLikeOptionLabel(o.Label))
            .ToList();
        if (candidateOptions.Count < 2)
            return null;

        var results = new List<EvaluatedOption>(candidateOptions.Count);
        var lowerQuestion = (userMessage ?? "").ToLowerInvariant();
        var lowerGoal = (goal.PrimaryGoal ?? "").ToLowerInvariant();
        var requiredEntities = entities.Entities
            .Where(e => e.Required)
            .ToList();

        foreach (var option in candidateOptions)
        {
            var score = 0.0;
            var principlePassCount = 0;
            var notes = new List<string>();
            var labelLower = option.Label.ToLowerInvariant();

            // Principle 1: physical feasibility over symbolic/remote shortcuts.
            var hasPhysicalAction = ContainsAny(labelLower, PhysicalActionHints);
            var hasIndirectAction = ContainsAny(labelLower, IndirectActionHints);
            var violatesPhysicalFeasibility = IsLikelyRemoteSubstitute(labelLower, requiredEntities);

            if (hasPhysicalAction)
            {
                score += 2.2;
                notes.Add("feasible_physical_step");
            }

            if (hasIndirectAction)
            {
                score -= 1.6;
                notes.Add("indirect_action");
            }

            if (violatesPhysicalFeasibility)
            {
                score -= 2.8;
                notes.Add("fails_physical_principle");
            }

            if ((hasPhysicalAction || !hasIndirectAction) && !violatesPhysicalFeasibility)
            {
                principlePassCount++;
                notes.Add("principle_feasibility_pass");
            }

            // Principle 2: required prerequisites must be satisfied.
            if (requiredEntities.Count == 0)
            {
                score += 0.4;
                principlePassCount++;
                notes.Add("principle_prerequisites_pass");
            }
            else
            {
                var missingRequiredCount = 0;
                foreach (var entity in requiredEntities)
                {
                    if (OptionSatisfiesRequiredEntity(labelLower, entity.Name))
                    {
                        score += 1.4;
                        notes.Add($"uses_{entity.Name}");
                    }
                    else
                    {
                        score -= 1.1;
                        missingRequiredCount++;
                        notes.Add($"missing_{entity.Name}");
                    }
                }

                if (missingRequiredCount == 0)
                {
                    principlePassCount++;
                    notes.Add("principle_prerequisites_pass");
                }
            }

            // Principle 3: action should directly advance the practical goal.
            var advancesGoal = AdvancesGoalDirectly(labelLower, lowerGoal);
            if (advancesGoal)
            {
                score += 2.0;
                principlePassCount++;
                notes.Add("principle_goal_progress_pass");
            }
            else if (ContainsAny(labelLower, StallingActionHints))
            {
                score -= 1.0;
                notes.Add("goal_progress_risk");
            }

            // Ordering constraints ("before X") should prefer prerequisite-first steps.
            if (lowerQuestion.Contains("before", StringComparison.Ordinal))
            {
                if (ContainsAny(labelLower, SequenceLeadingHints))
                {
                    score += 1.5;
                    notes.Add("sequence_respected");
                }

                if (ContainsAny(labelLower, SequenceTrailingHints))
                {
                    score -= 0.9;
                    notes.Add("sequence_risk");
                }
            }

            if (lowerQuestion.Contains("gate", StringComparison.Ordinal) &&
                lowerQuestion.Contains("pay", StringComparison.Ordinal) &&
                labelLower.Contains("pay", StringComparison.Ordinal))
            {
                score += 1.6;
                notes.Add("gate_payment_precondition");
            }

            if (lowerQuestion.Contains("check-in", StringComparison.Ordinal) ||
                lowerQuestion.Contains("check in", StringComparison.Ordinal))
            {
                if (labelLower.Contains("id", StringComparison.Ordinal) ||
                    labelLower.Contains("desk", StringComparison.Ordinal))
                {
                    score += 1.3;
                    notes.Add("checkin_id_precondition");
                }
            }

            if (lowerQuestion.Contains("key cut", StringComparison.Ordinal) &&
                labelLower.Contains("key", StringComparison.Ordinal))
            {
                score += 1.6;
                notes.Add("key_required");
            }

            if (lowerQuestion.Contains("pickup", StringComparison.Ordinal) ||
                lowerQuestion.Contains("pick up", StringComparison.Ordinal))
            {
                if (labelLower.Contains("collect", StringComparison.Ordinal) ||
                    labelLower.Contains("pick up", StringComparison.Ordinal) ||
                    labelLower.Contains("go", StringComparison.Ordinal))
                {
                    score += 1.4;
                    notes.Add("pickup_goal_alignment");
                }
            }

            // Tiny deterministic tiebreaker: options satisfying more principles win close calls.
            score += principlePassCount * 0.25;
            notes.Add($"principles={principlePassCount}/3");
            results.Add(new EvaluatedOption(option.Label, score, string.Join(",", notes), principlePassCount));
        }

        var selected = results
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.PrinciplePassCount)
            .ThenByDescending(r => ContainsAny(r.Label.ToLowerInvariant(), PhysicalActionHints))
            .FirstOrDefault();

        if (selected is null)
            return null;

        var constraintSummary = BuildFirstPrinciplesConstraintSummary(
            goal,
            constraints,
            requiredEntities,
            lowerQuestion);

        return new EvaluationDecision(
            SelectedAction: selected.Label,
            ConstraintSummary: constraintSummary,
            EvaluatedOptions: results);
    }

    private static bool IsActionLikeOptionLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return false;

        var trimmed = label.Trim().Trim('"', '\'', '*').TrimEnd('.', '!', '?', ':', ';');
        if (trimmed.Length < 3)
            return false;

        var lower = trimmed.ToLowerInvariant();
        if (NonActionOptionLabels.Contains(lower))
            return false;

        // Reject obvious scaffold headings even when prefixed.
        if (lower.StartsWith("preconditions", StringComparison.Ordinal) ||
            lower.StartsWith("constraints", StringComparison.Ordinal) ||
            lower.StartsWith("action options", StringComparison.Ordinal) ||
            lower.StartsWith("decision", StringComparison.Ordinal) ||
            lower.StartsWith("analysis", StringComparison.Ordinal))
        {
            return false;
        }

        return ContainsAny(lower, PhysicalActionHints) ||
               ContainsAny(lower, IndirectActionHints) ||
               ContainsAny(lower, StallingActionHints) ||
               ContainsAny(lower, GoalCompletionHints);
    }

    private static bool IsLikelyRemoteSubstitute(
        string optionLabelLower,
        IReadOnlyList<EntityFact> requiredEntities)
    {
        if (requiredEntities.Count == 0)
            return false;
        if (!ContainsAny(optionLabelLower, IndirectActionHints))
            return false;

        foreach (var entity in requiredEntities)
        {
            if (EntityRequirementHeuristics.OptionMentionsEntity(optionLabelLower, entity.Name))
                return false;
        }

        return true;
    }

    private static bool AdvancesGoalDirectly(string optionLabelLower, string goalLower)
    {
        if (ContainsAny(optionLabelLower, GoalCompletionHints))
            return true;

        if (string.IsNullOrWhiteSpace(goalLower))
            return false;

        var goalTokens = goalLower
            .Split([' ', '.', ',', ':', ';', '-', '(', ')'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 4 && !GoalStopWords.Contains(token));

        var overlapCount = 0;
        foreach (var token in goalTokens)
        {
            if (!optionLabelLower.Contains(token, StringComparison.Ordinal))
                continue;

            overlapCount++;
            if (overlapCount >= 1)
                return true;
        }

        return false;
    }

    private static bool OptionSatisfiesRequiredEntity(string optionLabelLower, string entityName)
    {
        return EntityRequirementHeuristics.OptionMentionsEntity(optionLabelLower, entityName) ||
               EntityRequirementHeuristics.OptionImpliesEntityUsage(optionLabelLower, entityName);
    }

    private static string BuildFirstPrinciplesConstraintSummary(
        GoalInference goal,
        ConstraintSet constraints,
        IReadOnlyList<EntityFact> requiredEntities,
        string lowerQuestion)
    {
        var summary = $"Choose the physically feasible option that directly completes the goal ({goal.PrimaryGoal})";

        if (requiredEntities.Count > 0)
        {
            var requiredNames = requiredEntities
                .Select(e => e.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            summary += $"; required objects must be available ({string.Join(", ", requiredNames)})";
        }

        if (lowerQuestion.Contains("before", StringComparison.Ordinal))
            summary += "; obey prerequisite-first order";

        var modelConstraint = constraints.Constraints.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(modelConstraint))
        {
            var lowerModelConstraint = modelConstraint.ToLowerInvariant();
            var overlapsCoreConstraint =
                lowerModelConstraint.Contains("first-principles", StringComparison.Ordinal) ||
                lowerModelConstraint.Contains("physically feasible", StringComparison.Ordinal) ||
                lowerModelConstraint.Contains("physically feasible option", StringComparison.Ordinal) ||
                lowerModelConstraint.Contains("directly satisfy the goal", StringComparison.Ordinal) ||
                lowerModelConstraint.Contains("directly advances the goal", StringComparison.Ordinal) ||
                lowerModelConstraint.Contains("directly completes the goal", StringComparison.Ordinal);

            if (!overlapsCoreConstraint)
                summary += $"; {modelConstraint.Trim()}";
        }

        return summary;
    }

    private static bool ContainsAny(string value, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}

internal sealed record ComposedAnswer(string AnswerText, IReadOnlyList<string> RationaleLines);

internal static class AnswerComposer
{
    public static ComposedAnswer Compose(
        GoalInference goal,
        ConstraintSet constraints,
        EvaluationDecision decision)
    {
        var selected = NormalizeActionPhrase(decision.SelectedAction);
        var answer = $"{selected}.";

        var decisionLine = $"Decision: {selected}";
        var selectedEval = decision.EvaluatedOptions
            .FirstOrDefault(o =>
                string.Equals(o.Label, decision.SelectedAction, StringComparison.OrdinalIgnoreCase))
            ?? decision.EvaluatedOptions.OrderByDescending(o => o.Score).FirstOrDefault();
        var alternativeEval = decision.EvaluatedOptions
            .Where(o => selectedEval is null ||
                        !string.Equals(o.Label, selectedEval.Label, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(o => o.Score)
            .FirstOrDefault();

        if (selectedEval is not null && alternativeEval is not null)
        {
            var alternative = NormalizeActionPhrase(alternativeEval.Label);
            var contrast = SummarizeAlternativeGap(selectedEval, alternativeEval);
            decisionLine = $"Decision: {selected} (alternative considered: {alternative}; {contrast})";
        }

        var rationale = new List<string>
        {
            $"Goal: {goal.PrimaryGoal}",
            $"Constraint: {decision.ConstraintSummary}",
            decisionLine
        };

        return new ComposedAnswer(answer, rationale);
    }

    private static string SummarizeAlternativeGap(
        EvaluatedOption selected,
        EvaluatedOption alternative)
    {
        var altNotes = (alternative.Notes ?? "").ToLowerInvariant();
        if (altNotes.Contains("missing_", StringComparison.Ordinal))
            return "it misses required prerequisites";
        if (altNotes.Contains("fails_physical_principle", StringComparison.Ordinal))
            return "it is less physically feasible";
        if (altNotes.Contains("indirect_action", StringComparison.Ordinal))
            return "it is more indirect";
        if (altNotes.Contains("goal_progress_risk", StringComparison.Ordinal))
            return "it advances the goal less directly";
        if (altNotes.Contains("sequence_risk", StringComparison.Ordinal))
            return "it is weaker on ordering constraints";

        if (selected.PrinciplePassCount > alternative.PrinciplePassCount)
            return "it satisfies fewer core checks";

        return "it scored lower on first-principles checks";
    }

    private static string NormalizeActionPhrase(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Choose the option that completes the task in person";

        var trimmed = text.Trim();
        if (char.IsLetter(trimmed[0]))
            return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        return trimmed;
    }
}
