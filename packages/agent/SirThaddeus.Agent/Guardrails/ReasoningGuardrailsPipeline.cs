using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.Agent.Routing;
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
    private readonly ILlmClient _llm;
    private readonly IAuditLogger _audit;

    private static readonly TimeSpan DetectorStepTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExtractionStepTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SynthesisStepTimeout = TimeSpan.FromSeconds(10);

    private sealed record FirstPrinciplesBreakdownResult(
        string Need,
        string Pieces,
        string Assembly,
        int LlmRoundTrips);

    public ReasoningGuardrailsPipeline(ILlmClient llm, IAuditLogger audit)
    {
        _detector = new GuardrailsDetector(llm);
        _goalInferencer = new GoalInferencer(llm);
        _entityExtractor = new EntityExtractor(llm);
        _constraintBuilder = new ConstraintBuilder(llm);
        _llm = llm;
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<GuardrailsPipelineResult?> TryRunAsync(
        string userMessage,
        string mode,
        string? extraContext,
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
        if (entities is null)
        {
            WriteFallback("entity_extraction_failed");
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

        var effectiveConstraints = MergeDeterministicConstraintHints(
            constraints.Constraints,
            userMessage);

        var evaluator = new OptionEvaluator();
        var evaluation = evaluator.Evaluate(userMessage, goal, entities, constraints);

        var breakdown = await RunBoundedAsync(
            ct => BuildFirstPrinciplesBreakdownAsync(userMessage, goal, entities, constraints, ct),
            ExtractionStepTimeout,
            cancellationToken);
        if (breakdown is null)
        {
            WriteFallback("first_principles_breakdown_failed");
            return null;
        }

        llmRoundTrips += breakdown.LlmRoundTrips;

        var entitySummary = entities.Entities.Count > 0
            ? string.Join(", ", entities.Entities.Select(e => e.Name))
            : "none extracted";

        var contextText = new StringBuilder();
        contextText.AppendLine($"- Goal: {goal.PrimaryGoal}");
        contextText.AppendLine($"- Key entities: {entitySummary}");
        contextText.AppendLine($"- Constraints: {string.Join("; ", effectiveConstraints)}");
        contextText.AppendLine($"- Need: {breakdown.Need}");
        contextText.AppendLine($"- Pieces: {breakdown.Pieces}");
        contextText.AppendLine($"- Assembly: {breakdown.Assembly}");

        if (evaluation is not null)
        {
            if (evaluation.Eliminations.Count > 0)
            {
                foreach (var elim in evaluation.Eliminations)
                    contextText.AppendLine($"- ELIMINATED: '{elim.Label}' — {elim.Reason}");
            }

            var viableList = string.Join(", ", evaluation.ViableLabels.Select(l => $"'{l}'"));
            if (evaluation.ViableLabels.Count == 1)
                contextText.AppendLine($"- Remaining: {viableList} is the ONLY viable option");
            else if (evaluation.ViableLabels.Count > 1)
                contextText.AppendLine($"- Remaining viable options: {viableList} (best: '{evaluation.SelectedAction}')");

            contextText.AppendLine($"- Scoring basis: {evaluation.ConstraintSummary}");
        }

        if (!string.IsNullOrWhiteSpace(extraContext))
        {
            contextText.AppendLine();
            contextText.AppendLine("[SUPPLEMENTAL CONTEXT]");
            contextText.AppendLine(extraContext);
            contextText.AppendLine("[/SUPPLEMENTAL CONTEXT]");
        }

        var userAsksForReasoning = IntentFeatureExtractor.LooksLikeReasoningFollowUp(
            (userMessage ?? string.Empty).Trim().ToLowerInvariant());

        var finalMessages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are Sir Thaddeus, a witty and pragmatic agent.\n" +
                "Use first-principles logic internally, but keep private reasoning hidden.\n" +
                "Return a direct answer.\n" +
                "Only include a brief explanation when the user explicitly asks for it."),
            ChatMessage.User(
                $"Question:\n{userMessage}\n\n" +
                $"Decomposed Context:\n" +
                contextText + "\n" +
                (userAsksForReasoning
                    ? "The user explicitly asked for reasoning. Give the direct answer first, then add a short 'Why:' section using Need/Pieces/Assembly in 2-4 bullets."
                    : "The user did not ask for reasoning. Give the direct answer in one short sentence (max 15 words). " +
                      "Mention the key object or entity involved. " +
                      "If the decomposed context reveals that ALL presented options are factually wrong, say NEITHER is correct and state the actual fact. " +
                      "If options were ELIMINATED, explain that the remaining option is the only one that works — not merely better or more efficient. " +
                      "If multiple options remain viable, recommend the best one."))
        };

        var finalLlm = await RunBoundedAsync(
            async ct => (LlmResponse?) await _llm.ChatAsync(finalMessages, tools: null, maxTokensOverride: 400, ct),
            SynthesisStepTimeout,
            cancellationToken);

        if (finalLlm is null || string.IsNullOrWhiteSpace(finalLlm.Content))
        {
            WriteFallback("final_synthesis_failed");
            return null;
        }

        llmRoundTrips++;

        var answerText = finalLlm.Content;
        var deterministicDecisionLine = string.Empty;
        if (TryApplyDeterministicFeasibilityDecision(
                evaluation,
                entities,
                answerText,
                out var corrected,
                out var decisionLine))
        {
            answerText = corrected;
            deterministicDecisionLine = decisionLine;
        }

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = "GUARDRAILS_DECISION",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["goal"] = goal.PrimaryGoal,
                ["triggerRisk"] = triggerDecision.Risk
            }
        });

        return new GuardrailsPipelineResult
        {
            AnswerText = answerText,
            RationaleLines = [
                $"Goal: {goal.PrimaryGoal}",
                $"Constraint: {string.Join("; ", effectiveConstraints)}",
                $"Need: {breakdown.Need}",
                $"Pieces: {breakdown.Pieces}",
                $"Assembly: {breakdown.Assembly}",
                string.IsNullOrWhiteSpace(deterministicDecisionLine)
                    ? "Decision: synthesized from decomposed context"
                    : $"Decision: {deterministicDecisionLine}"
            ],
            TriggerRisk = triggerDecision.Risk,
            TriggerWhy = triggerDecision.Why,
            TriggerSource = triggerDecision.Source,
            LlmRoundTrips = llmRoundTrips
        };
    }

    private static bool TryApplyDeterministicFeasibilityDecision(
        EvaluationDecision? evaluation,
        EntityExtraction entities,
        string answerText,
        out string correctedAnswer,
        out string decisionLine)
    {
        correctedAnswer = answerText;
        decisionLine = string.Empty;

        if (evaluation is null || evaluation.EvaluatedOptions.Count < 2)
            return false;

        var sorted = evaluation.EvaluatedOptions
            .OrderByDescending(o => o.Score)
            .ToList();
        var best = sorted[0];
        var runnerUp = sorted[1];

        // Only correct if there's a clear scoring gap
        if (best.Score - runnerUp.Score < 1.5)
            return false;

        // Check if the LLM answer already mentions the best option with correct framing.
        // When alternatives were eliminated, also verify the answer conveys necessity
        // (not just preference) before skipping correction.
        var answerLower = (answerText ?? "").ToLowerInvariant();
        var bestLower = best.Label.ToLowerInvariant();
        if (answerLower.Contains(bestLower, StringComparison.Ordinal))
        {
            if (evaluation.Eliminations.Count > 0 && evaluation.ViableLabels.Count == 1)
            {
                var hasNecessityFraming =
                    answerLower.Contains("only option", StringComparison.Ordinal) ||
                    answerLower.Contains("only viable", StringComparison.Ordinal) ||
                    answerLower.Contains("only choice", StringComparison.Ordinal) ||
                    answerLower.Contains("only way", StringComparison.Ordinal) ||
                    answerLower.Contains("must ", StringComparison.Ordinal) ||
                    answerLower.Contains("have to", StringComparison.Ordinal) ||
                    answerLower.Contains("no other", StringComparison.Ordinal);
                if (hasNecessityFraming)
                    return false;
                // Falls through to correction — LLM chose the right action but with wrong framing.
            }
            else
            {
                return false;
            }
        }

        var bestLabel = best.Label.Trim();
        var entityNames = entities.Entities
            .Where(e => e.Required)
            .Select(e => e.Name)
            .ToList();

        var capitalizedBest = $"{char.ToUpperInvariant(bestLabel[0])}{bestLabel[1..]}";

        if (evaluation.Eliminations.Count > 0 && evaluation.ViableLabels.Count == 1)
        {
            var eliminated = evaluation.Eliminations[0];
            correctedAnswer = entityNames.Count > 0
                ? $"{capitalizedBest} \u2014 '{eliminated.Label}' {eliminated.Reason}, so it's the only option."
                : $"{capitalizedBest} \u2014 it's the only viable option.";
        }
        else if (entityNames.Count > 0)
        {
            var entityText = string.Join(" and ", entityNames);
            correctedAnswer = $"{capitalizedBest} \u2014 you need the {entityText} there.";
        }
        else
        {
            correctedAnswer = $"{capitalizedBest}.";
        }
        decisionLine = $"deterministic evaluation selected '{best.Label}' over '{runnerUp.Label}' ({evaluation.ConstraintSummary})";
        return true;
    }

    private async Task<FirstPrinciplesBreakdownResult?> BuildFirstPrinciplesBreakdownAsync(
        string userMessage,
        GoalInference goal,
        EntityExtraction entities,
        ConstraintSet constraints,
        CancellationToken cancellationToken)
    {
        var entitySummary = entities.Entities.Count > 0
            ? string.Join(", ", entities.Entities.Select(e => e.Name))
            : "none";
        var optionSummary = entities.Options.Count > 0
            ? string.Join(", ", entities.Options.Select(o => o.Label))
            : "none";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "Break this into first principles. " +
                "Consider what must physically be present at the destination for the service to succeed. " +
                "Return strict JSON only with schema: {\"need\":\"...\",\"pieces\":[\"...\"],\"assembly\":\"...\"}"),
            ChatMessage.User(
                "Question:\n" + userMessage + "\n\n" +
                "Answer these internal questions:\n" +
                "1) What do we need to make this work?\n" +
                "2) What are the pieces involved?\n" +
                "3) How do the pieces combine to decide between options?\n\n" +
                $"Goal: {goal.PrimaryGoal}\n" +
                $"Entities: {entitySummary}\n" +
                $"Options: {optionSummary}\n" +
                $"Constraints: {string.Join("; ", constraints.Constraints)}")
        };

        var response = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 500, cancellationToken);
        if (string.IsNullOrWhiteSpace(response.Content))
            return null;

        var cleaned = StripCodeFence(response.Content.Trim());
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var need = root.TryGetProperty("need", out var needEl) ? needEl.GetString() : null;
            var pieces = root.TryGetProperty("pieces", out var piecesEl)
                ? piecesEl.ValueKind == JsonValueKind.Array
                    ? string.Join(", ", piecesEl.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? (e.GetString() ?? "") : e.GetRawText()))
                    : (piecesEl.ValueKind == JsonValueKind.String ? piecesEl.GetString() : piecesEl.GetRawText())
                : null;
            var assembly = root.TryGetProperty("assembly", out var assemblyEl) ? assemblyEl.GetString() : null;

            if (string.IsNullOrWhiteSpace(need) ||
                string.IsNullOrWhiteSpace(pieces) ||
                string.IsNullOrWhiteSpace(assembly))
            {
                return null;
            }

            return new FirstPrinciplesBreakdownResult(need.Trim(), pieces.Trim(), assembly.Trim(), 1);
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

    private static IReadOnlyList<string> MergeDeterministicConstraintHints(
        IReadOnlyList<string> baseConstraints,
        string userMessage)
    {
        var merged = new List<string>(baseConstraints.Where(c => !string.IsNullOrWhiteSpace(c)));
        var lower = (userMessage ?? string.Empty).Trim().ToLowerInvariant();

        var requiredEntities = EntityRequirementHeuristics.DetectRequiredEntities(lower);
        if (requiredEntities.Count > 0)
        {
            var entityList = string.Join(", ", requiredEntities);
            var feasibilityConstraint =
                $"Feasibility: the required object(s) ({entityList}) must physically arrive at the destination; prefer the action that transports them there.";

            if (!merged.Any(c => c.Contains("physically arrive", StringComparison.OrdinalIgnoreCase) ||
                                 c.Contains("must physically", StringComparison.OrdinalIgnoreCase)))
            {
                merged.Add(feasibilityConstraint);
            }
        }

        if (merged.Count == 0)
            merged.Add("Choose the physically feasible option that directly completes the goal.");

        return merged;
    }

    public GuardrailsPipelineResult? TryRunDeterministicSpecialCase(string userMessage)
        => null;

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
        @"\b\d+\s*(?:m|meter|meters|km|mile|miles|mi|block|blocks|minute|minutes|min|hour|hours|hr|hrs|feet|ft|yard|yards|away)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ServiceCueRegex = new(
        @"\b(?:gas station|car wash|airport|pharmacy|ups store|post office|bank|hardware store|library|garage|hotel|dry[-\s]?clean(?:ing|er)|repair shop|mechanic|grocery store|supermarket|hospital|clinic|dentist|gym|restaurant|mall|laundromat|vet(?:erinarian)?|school|theater|cinema|barber|salon)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RequiredObjectCueRegex = new(
        @"\b(?:car|passport|prescription|package|key|id|license|ticket|jacket|laptop|device|phone|wallet|bag|luggage|suitcase|document|briefcase|umbrella|glasses|medication|camera|book)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChoicePatternRegex = new(
        @"\b(?:" +
            @"should (?:i|you|we|he|she|they)|" +
            @"do (?:i|you|we)|" +
            @"would (?:it be better to|you rather)|" +
            @"is it better to|" +
            @"which (?:is|are|was|were|would be) \w+|" +
            @"which (?:one|option)" +
        @")\b[\s\S]{0,120}\b(?:or|vs\.?|versus)\b",
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
        var hasEvaluativeCue =
            lower.Contains("which is correct", StringComparison.Ordinal) ||
            lower.Contains("which is right", StringComparison.Ordinal) ||
            lower.Contains("which is true", StringComparison.Ordinal) ||
            lower.Contains("which are correct", StringComparison.Ordinal) ||
            lower.Contains("true or false", StringComparison.Ordinal) ||
            lower.Contains("weighs more", StringComparison.Ordinal) ||
            lower.Contains("heavier", StringComparison.Ordinal) ||
            lower.Contains("more expensive", StringComparison.Ordinal);

        if (hasChoicePattern && (hasServiceCue || hasRequiredObjectCue || hasDistanceCue || hasNeedsCue || hasEvaluativeCue))
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
                "Infer the practical real-world goal. \n" +
                "IMPORTANT: Consider what the destination or service fundamentally acts upon. " +
                "If a destination provides a service for a specific object (e.g. a car wash services cars, " +
                "a gas station services vehicles, a repair shop services items), the goal includes " +
                "bringing that object to the destination.\n" +
                "Return STRICT JSON only: " +
                "{\"primary_goal\":\"...\",\"alternative_goals\":[\"...\"],\"confidence\":0.0}"),
            ChatMessage.User(userMessage)
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 400, cancellationToken);
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
        @"\b(?:should\s+(?:i|you|we|he|she|they)|do\s+(?:i|you|we)|is it better to|would it be better to|would you rather|which\s+(?:is|are|was|were|would be)\s+\w+[:\s]+)\s*(?<a>.+?)\s+or\s+(?<b>.+?)(?:[?.!]|$)",
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
                "Extract entities and action options. Look for implied objects (e.g. 'car' for 'car wash', 'license' for 'drive'). " +
                "Return STRICT JSON only with schema: " +
                "{\"entities\":[{\"name\":\"...\",\"kind\":\"required_object|destination|other\",\"required\":true}]," +
                "\"options\":[{\"label\":\"...\",\"preconditions\":[\"...\"],\"effects\":[\"...\"]}]}"),
            ChatMessage.User(userMessage)
        };

        try
        {
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 500, cancellationToken);
            var parsed = ParseExtraction(llm.Content);
            if (parsed is not null && parsed.Options.Count >= 2)
                return parsed with { LlmRoundTrips = 1 };
        }
        catch
        {
            // Return heuristic fallback.
        }

        // Return heuristic even with < 2 options — the pipeline can still
        // use entities + breakdown for synthesis. The evaluator handles 0 options.
        return heuristic with { LlmRoundTrips = 0 };
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
                "Consider whether each option can physically deliver required objects to the destination. " +
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
            var llm = await _llm.ChatAsync(messages, tools: null, maxTokensOverride: 500, cancellationToken);
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

internal sealed record EliminationEntry(string Label, string Reason);

internal sealed record EvaluationDecision(
    string SelectedAction,
    string ConstraintSummary,
    IReadOnlyList<EvaluatedOption> EvaluatedOptions,
    IReadOnlyList<string> ViableLabels,
    IReadOnlyList<EliminationEntry> Eliminations)
{
    public bool AlternativeNonViable => Eliminations.Count > 0;
}

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

        var eliminations = new List<EliminationEntry>();
        var viableLabels = new List<string>();

        foreach (var opt in results)
        {
            var reason = DeriveEliminationReason(opt, requiredEntities);
            if (reason is not null)
                eliminations.Add(new EliminationEntry(opt.Label, reason));
            else
                viableLabels.Add(opt.Label);
        }

        var constraintSummary = BuildFirstPrinciplesConstraintSummary(
            goal,
            constraints,
            requiredEntities,
            lowerQuestion);

        return new EvaluationDecision(
            SelectedAction: selected.Label,
            ConstraintSummary: constraintSummary,
            EvaluatedOptions: results,
            ViableLabels: viableLabels,
            Eliminations: eliminations);
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

    private static string? DeriveEliminationReason(
        EvaluatedOption option,
        IReadOnlyList<EntityFact> requiredEntities)
    {
        var notes = option.Notes ?? "";

        var missingEntities = requiredEntities
            .Where(e => notes.Contains($"missing_{e.Name}", StringComparison.Ordinal))
            .Select(e => e.Name)
            .ToList();

        if (missingEntities.Count > 0)
        {
            var entityList = string.Join(" and ", missingEntities);
            return $"does not bring the {entityList} to the destination";
        }

        if (notes.Contains("fails_physical_principle", StringComparison.Ordinal))
            return "fails physical feasibility";

        return null;
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
            return "it cannot accomplish the goal without the required object";
        if (altNotes.Contains("fails_physical_principle", StringComparison.Ordinal))
            return "it fails to satisfy physical feasibility";
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
