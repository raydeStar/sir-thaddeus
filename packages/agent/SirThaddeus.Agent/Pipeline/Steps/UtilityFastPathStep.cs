using SirThaddeus.Agent.Search;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that answers deterministic utility queries (unit
/// conversions, percent-of, simple arithmetic, temperature conversions,
/// classic reasoning tripwires) without ever calling the LLM. Wraps the
/// agent-package <see cref="IDeterministicUtilityEngine"/> — if the engine
/// matches, the step terminates the pipeline with the canned answer.
///
/// <para>Place this step <b>before</b> <c>FeatureExtractorStep</c> so
/// utility turns skip every downstream cost: feature extraction, footman
/// classification, LLM round-trip, tool loop. Turns that match answer in
/// microseconds regardless of gatekeeper latency or LM Studio model
/// swaps.</para>
///
/// <para>On a no-match the step returns <see cref="StepResult.Continue"/>
/// unchanged — the later steps handle anything the engine didn't claim.</para>
/// </summary>
public sealed class UtilityFastPathStep : ITurnStep
{
    private static readonly Regex LiteralReplyContractPattern = new(
        @"^\s*(?:reply|respond|answer)\s+with\s+exactly\s+(?:this\s+)?(?:text|phrase|string)\s+and\s+nothing\s+else\s*:\s*(?<literal>.+?)\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex QuotedLiteralReplyContractPattern = new(
        @"^\s*(?:reply|respond|answer)\s+exactly\s+[""“](?<literal>.+?)[""”]\s*(?:and\s+nothing\s+else)?\s*\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex JsonFieldsPattern = new(
        @"return\s+only\s+valid\s+json\b.*?\b(?:top-level\s+)?fields\s*:\s*(?<fields>[^.]+)\.",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex JsonFieldAssignmentPattern = new(
        @"(?:the\s+)?(?<field>[A-Za-z_][A-Za-z0-9_]*)\s+should\s+be\s+(?<value>[^,.\n]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IDeterministicUtilityEngine _engine;
    private readonly DeterministicMatchConfidence _minConfidence;

    /// <param name="engine">Utility engine. Defaults to the shared
    /// <see cref="DeterministicUtilityEngineAdapter"/> which wraps
    /// <c>DeterministicPreRouter</c>.</param>
    /// <param name="minConfidence">The lowest confidence the step will
    /// terminate on. Defaults to <see cref="DeterministicMatchConfidence.Medium"/>,
    /// which matches the orchestrator's current policy — high-confidence
    /// matches (strict regex hits) always fire, medium-confidence matches
    /// (conversational wrappers) still fire, only <see cref="DeterministicMatchConfidence.None"/>
    /// passes through.</param>
    public UtilityFastPathStep(
        IDeterministicUtilityEngine? engine = null,
        DeterministicMatchConfidence minConfidence = DeterministicMatchConfidence.Medium)
    {
        _engine = engine ?? new DeterministicUtilityEngineAdapter();
        _minConfidence = minConfidence;
    }

    public string Name => "UtilityFastPath";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.UserText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        // Ablation seam: when ST_HARNESS_DISABLE_FASTPATH is set, every prompt
        // skips the deterministic short-circuits below and is answered by the
        // model + tool loop instead. This lets the benchmark harness measure
        // the model's true ability (and quantify how much these pre-LLM
        // solvers contribute) without changing default behavior. Unset → the
        // normal fast-path runs exactly as before.
        if (FastPathDisabledByHarness())
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        if (TryMatchLiteralReplyContract(context.UserText) is { Length: > 0 } literalReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = literalReply,
                    Success = true,
                }));

        if (TryMatchExplicitJsonContract(context.UserText) is { Length: > 0 } jsonContractReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = jsonContractReply,
                    Success = true,
                }));

        var match = _engine.TryMatch(context.UserText);
        if (match is null || match.Confidence < _minConfidence)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        if (ShouldDeferPersonalPromptToMemory(context))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        // Deterministic answer wins the turn. No tools, no LLM; surface
        // the canned answer and stop the pipeline.
        var response = new AgentResponse
        {
            Text = match.Result.Answer,
            Success = true,
        };
        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }

    private static string? TryMatchLiteralReplyContract(string userText)
    {
        var match = LiteralReplyContractPattern.Match(userText);
        if (!match.Success)
            match = QuotedLiteralReplyContractPattern.Match(userText);
        if (!match.Success)
            return null;

        var literal = UnwrapInlineLiteral(match.Groups["literal"].Value.Trim());
        return IsSafeLiteralReply(literal) ? literal : null;
    }

    private static string? TryMatchExplicitJsonContract(string userText)
    {
        var fieldsMatch = JsonFieldsPattern.Match(userText);
        if (!fieldsMatch.Success)
            return null;

        var fields = fieldsMatch.Groups["fields"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(field => field.Trim())
            .Where(field => Regex.IsMatch(field, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant))
            .ToArray();
        if (fields.Length is 0 or > 12)
            return null;

        var assignments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (Match assignment in JsonFieldAssignmentPattern.Matches(userText))
        {
            var field = assignment.Groups["field"].Value.Trim();
            if (!fields.Contains(field, StringComparer.OrdinalIgnoreCase))
                continue;

            var rawValue = assignment.Groups["value"].Value.Trim();
            rawValue = Regex.Replace(rawValue, @"^\band\s+the\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            assignments[field] = ParseSimpleJsonValue(rawValue);
        }

        if (fields.Any(field => !assignments.ContainsKey(field)))
            return null;

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in fields)
            payload[field] = assignments[field];

        return JsonSerializer.Serialize(payload, CompactJsonOptions);
    }

    private static object? ParseSimpleJsonValue(string value)
    {
        var trimmed = value.Trim().Trim('"', '\'', '`');
        if (bool.TryParse(trimmed, out var boolean))
            return boolean;
        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return number;
        return trimmed;
    }

    private static string UnwrapInlineLiteral(string value)
    {
        if (value.Length >= 2)
        {
            var first = value[0];
            var last = value[^1];
            if ((first == '"' && last == '"') ||
                (first == '\'' && last == '\'') ||
                (first == '“' && last == '”') ||
                (first == '`' && last == '`'))
            {
                return value[1..^1].Trim();
            }
        }

        return value;
    }

    private static bool IsSafeLiteralReply(string literal)
    {
        if (string.IsNullOrWhiteSpace(literal))
            return false;
        if (literal.Length > 160)
            return false;
        if (literal.Any(char.IsControl))
            return false;
        if (literal.Contains('\n') || literal.Contains('\r'))
            return false;

        return true;
    }

    private static bool ShouldDeferPersonalPromptToMemory(TurnContext context)
    {
        var userText = context.UserText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        if (HasPersonalContextCue(userText))
            return true;

        var hasMemoryRetrieve = context.ToolDefs.Any(def =>
            string.Equals(def.Function?.Name, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Function?.Name, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));
        if (!hasMemoryRetrieve && !HarnessAllowsMemoryRetrieve())
            return false;

        return HasPersonalContextCue(userText);
    }

    private static bool HasPersonalContextCue(string userText)
    {
        var lower = " " + userText.Trim().ToLowerInvariant() + " ";
        return lower.Contains(" my ", StringComparison.Ordinal) ||
               lower.Contains(" i'm ", StringComparison.Ordinal) ||
               lower.Contains(" im ", StringComparison.Ordinal) ||
               lower.Contains(" i've ", StringComparison.Ordinal) ||
               lower.Contains(" we ", StringComparison.Ordinal) ||
               lower.Contains(" our ", StringComparison.Ordinal);
    }

    private static bool FastPathDisabledByHarness()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_DISABLE_FASTPATH")?.Trim();
        return string.Equals(raw, "1", StringComparison.Ordinal)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HarnessAllowsMemoryRetrieve()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(tool =>
                string.Equals(tool, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tool, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));
    }
}
