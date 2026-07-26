using System.Text.RegularExpressions;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Builds an intent preview only for turns with deterministic multi-step or
/// consequential-action signals. It performs no model call and never chooses
/// concrete tools; normal routing, permissions, and audited execution remain
/// authoritative after approval.
/// </summary>
public static partial class WorkPlanBuilder
{
    private static readonly string[] ContextSignals =
    [
        "read", "review", "inspect", "open", "screen", "window", "file", "document",
        "attached", "wiki", "notes",
    ];

    private static readonly string[] ResearchSignals =
    [
        "research", "search", "look up", "find", "investigate", "compare", "current",
        "latest", "sources", "evidence",
    ];

    private static readonly string[] OutputSignals =
    [
        "save", "create", "write", "rewrite", "update", "publish", "export", "send",
        "delete", "remove", "move", "rename", "wiki", "report", "brief", "document",
    ];

    /// <summary>
    /// Verbs that transform gathered context into an answer. Paired with a
    /// context signal these describe a genuinely multi-step turn ("read the
    /// screen, then synthesize"), which is what makes plan-first oversight
    /// worth showing even though no durable output is produced.
    /// </summary>
    private static readonly string[] SynthesisSignals =
    [
        "summarize", "summarise", "analyze", "analyse", "compare", "explain",
        "review", "draft", "outline", "critique", "translate", "extract",
    ];

    private static readonly string[] HighRiskSignals =
    [
        "delete", "remove", "overwrite", "publish", "send", "execute", "run command",
        "system", "always allow",
    ];

    private static readonly string[] ActionVerbs =
    [
        "analyze", "build", "check", "compare", "create", "delete", "draft", "edit",
        "export", "find", "inspect", "investigate", "open", "publish", "read", "remove",
        "research", "review", "rewrite", "save", "search", "send", "summarize", "update",
        "verify", "write",
    ];

    public static WorkPlan? TryBuild(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;

        var normalized = Normalize(userText);
        var hasContext = ContainsAny(normalized, ContextSignals);
        var hasResearch = ContainsAny(normalized, ResearchSignals);
        var hasOutput = ContainsAny(normalized, OutputSignals);
        var highRisk = ContainsAny(normalized, HighRiskSignals);
        var actionCount = ActionVerbs.Count(verb => ContainsWordOrPhrase(normalized, verb));
        var explicitSequence = SequenceSignal().IsMatch(normalized);
        var compoundOutcome = (hasContext || hasResearch) && hasOutput;
        var longStructuredAsk = normalized.Length >= 220 && actionCount >= 2;
        // Gathering permissioned context and then synthesizing over it is a
        // multi-step, permission-bearing turn even with no durable output —
        // e.g. "summarize the active window". Without this the flagship
        // read-then-synthesize case skipped plan review entirely and the user
        // met a bare permission prompt with no stated strategy behind it.
        var contextSynthesis = (hasContext || hasResearch) && ContainsAny(normalized, SynthesisSignals);

        if (!highRisk && !explicitSequence && !compoundOutcome && !longStructuredAsk &&
            !contextSynthesis && actionCount < 3)
            return null;

        var now = DateTimeOffset.UtcNow;
        var risk = highRisk ? WorkPlanRisk.High : hasOutput ? WorkPlanRisk.Medium : WorkPlanRisk.Low;
        var steps = new List<WorkPlanStep>();

        if (hasContext)
        {
            steps.Add(NewStep(
                "Review the requested local context",
                WorkPlanCapability.Context,
                hasOutput ? WorkPlanRisk.Medium : WorkPlanRisk.Low,
                requiresPermission: true));
        }

        if (hasResearch)
        {
            steps.Add(NewStep(
                "Gather and check relevant evidence",
                WorkPlanCapability.Research,
                WorkPlanRisk.Low,
                requiresPermission: true));
        }

        steps.Add(NewStep(
            hasResearch || hasContext
                ? "Synthesize the findings for the requested outcome"
                : "Complete the requested work",
            WorkPlanCapability.Compose,
            WorkPlanRisk.Low,
            requiresPermission: false));

        if (hasOutput)
        {
            steps.Add(NewStep(
                DescribeOutput(normalized),
                WorkPlanCapability.DurableOutput,
                risk,
                requiresPermission: true));
        }

        if (hasOutput || highRisk)
        {
            steps.Add(NewStep(
                "Verify the result and report exactly what changed",
                WorkPlanCapability.Verify,
                WorkPlanRisk.Low,
                requiresPermission: false));
        }

        var intent = userText.Trim().ReplaceLineEndings(" ");
        if (intent.Length > 180)
            intent = intent[..177].TrimEnd() + "...";

        return new WorkPlan(
            PlanId: $"plan_{Guid.NewGuid():N}"[..29],
            Version: 1,
            Intent: intent,
            Steps: steps,
            Risk: risk,
            RiskSummary: risk switch
            {
                WorkPlanRisk.High => "Consequential or potentially irreversible action; permission remains required.",
                WorkPlanRisk.Medium => "Creates or changes durable work; permission remains required.",
                _ => "Read and synthesis work with normal permission boundaries.",
            },
            CreatedAt: now,
            UpdatedAt: now);
    }

    public static bool TryValidateEditedSteps(
        IReadOnlyList<WorkPlanStep>? steps,
        out string? error)
    {
        if (steps is null || steps.Count is < 1 or > 12)
        {
            error = "A plan must contain between 1 and 12 steps.";
            return false;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.StepId) ||
                step.StepId.Length > 80 ||
                !ids.Add(step.StepId))
            {
                error = "Every plan step must have a unique stable id.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(step.Label) || step.Label.Trim().Length > 180)
            {
                error = "Every plan step needs a label of 180 characters or fewer.";
                return false;
            }

            if (!Enum.IsDefined(step.Capability) || !Enum.IsDefined(step.Risk))
            {
                error = "A plan step contains an unsupported capability or risk.";
                return false;
            }

            if (step.Status != WorkPlanStepStatus.Pending)
            {
                error = "A plan cannot be edited with pre-completed execution states.";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static string ComposeApprovedPrompt(string originalPrompt, WorkPlan plan)
    {
        var steps = string.Join(
            "\n",
            plan.Steps.Select((step, index) =>
                $"{index + 1}. {step.Label} [capability={step.Capability}]"));
        return $"{originalPrompt.TrimEnd()}\n\n" +
               "[USER-APPROVED WORK PLAN]\n" +
               $"{steps}\n" +
               "Follow the approved order for remaining work. Normal safety, tool policy, " +
               "permission checks, and verification still apply. If a step is impossible or " +
               "unsafe, stop that step and report it rather than silently changing scope.";
    }

    private static WorkPlanStep NewStep(
        string label,
        WorkPlanCapability capability,
        WorkPlanRisk risk,
        bool requiresPermission) =>
        new(
            StepId: $"step_{Guid.NewGuid():N}"[..29],
            Label: label,
            Capability: capability,
            Risk: risk,
            RequiresPermission: requiresPermission,
            Status: WorkPlanStepStatus.Pending);

    private static string DescribeOutput(string normalized)
    {
        if (normalized.Contains("wiki", StringComparison.Ordinal))
            return "Create or update the durable Wiki output";
        if (normalized.Contains("send", StringComparison.Ordinal))
            return "Prepare and send the approved outbound result";
        if (normalized.Contains("delete", StringComparison.Ordinal) ||
            normalized.Contains("remove", StringComparison.Ordinal))
            return "Apply the approved removal";
        return "Create or update the requested durable output";
    }

    private static bool ContainsAny(string text, IEnumerable<string> signals) =>
        signals.Any(signal => ContainsWordOrPhrase(text, signal));

    private static bool ContainsWordOrPhrase(string text, string value)
    {
        if (value.Contains(' '))
            return text.Contains(value, StringComparison.Ordinal);
        return Regex.IsMatch(text, $@"\b{Regex.Escape(value)}(?:s|ed|ing)?\b", RegexOptions.CultureInvariant);
    }

    private static string Normalize(string text) =>
        Whitespace().Replace(text.Trim().ToLowerInvariant(), " ");

    [GeneratedRegex(@"\b(?:then|after that|next|finally|and then|before you|once you)\b|(?:^|\s)\d+[.)]\s", RegexOptions.CultureInvariant)]
    private static partial Regex SequenceSignal();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
