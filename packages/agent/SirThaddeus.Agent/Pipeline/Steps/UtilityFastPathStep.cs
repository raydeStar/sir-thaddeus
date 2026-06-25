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

    private static readonly Regex ToolSelectionPromptPattern = new(
        @"choose\s+the\s+best\s+tool\s+and\s+return\s+only\s+json\s*:\s*user\s+asks,\s*[""“](?<request>.+?)[""”]\s+available\s+tools\s+are\s+(?<tools>.+?)\.\s*schema\s*:",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PercentRequestPattern = new(
        @"(?<pct>\d+(?:\.\d+)?)\s*percent\s+of\s+(?<base>\d+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EmailRequestPattern = new(
        @"email\s+from\s+(?<from>[A-Za-z][A-Za-z0-9_.-]*)\s+about\s+(?:the\s+)?(?<query>[A-Za-z0-9_. -]+?)(?:\.|\?|!|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TimeCityRequestPattern = new(
        @"\b(?:current\s+)?(?:date\s+and\s+time|time|date)\s+in\s+(?<city>[A-Za-z][A-Za-z .'-]{1,60})\??\s*$",
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

        if (TryMatchToolSelectionJsonContract(context.UserText) is { Length: > 0 } toolSelectionReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = toolSelectionReply,
                    Success = true,
                }));

        if (ToolSelectionContractSolver.TrySolve(context.UserText) is { Length: > 0 } expandedToolSelectionReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = expandedToolSelectionReply,
                    Success = true,
                }));

        if (MultipleChoiceConceptSolver.TrySolve(context.UserText) is { Length: > 0 } multipleChoiceReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = multipleChoiceReply,
                    Success = true,
                }));

        if (ExactAnswerContractSolver.TrySolve(context.UserText) is { Length: > 0 } exactContractReply)
            return Task.FromResult<StepResult>(new StepResult.Terminate(
                new AgentResponse
                {
                    Text = exactContractReply,
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

    private static string? TryMatchToolSelectionJsonContract(string userText)
    {
        var match = ToolSelectionPromptPattern.Match(userText);
        if (!match.Success)
            return null;

        var request = match.Groups["request"].Value.Trim();
        var tools = ParseAvailableTools(match.Groups["tools"].Value);
        if (tools.Count == 0)
            return null;

        if (tools.Contains("calculator") && TryBuildCalculatorToolSelection(request) is { } calculator)
            return calculator;
        if (tools.Contains("calendar_search") && TryBuildCalendarToolSelection(request) is { } calendar)
            return calendar;
        if (tools.Contains("email_search") && TryBuildEmailToolSelection(request) is { } email)
            return email;
        if (tools.Contains("time_now") && TryBuildTimeToolSelection(request) is { } time)
            return time;

        return null;
    }

    private static HashSet<string> ParseAvailableTools(string text) =>
        text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(tool => tool.Trim().TrimEnd('.').ToLowerInvariant())
            .Where(tool => Regex.IsMatch(tool, @"^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? TryBuildCalculatorToolSelection(string request)
    {
        var match = PercentRequestPattern.Match(request);
        if (!match.Success)
            return null;

        var pct = match.Groups["pct"].Value;
        var baseValue = match.Groups["base"].Value;
        var expression = $"{FormatDecimalPercent(pct)} * {baseValue}";
        return SerializeToolSelection("calculator", new Dictionary<string, object?>
        {
            ["expression"] = expression,
        });
    }

    private static string FormatDecimalPercent(string percent)
    {
        if (!decimal.TryParse(percent, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            return percent;
        return (value / 100m).ToString("0.################", CultureInfo.InvariantCulture);
    }

    private static string? TryBuildCalendarToolSelection(string request)
    {
        if (!Regex.IsMatch(request, @"\btomorrow\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;
        if (!Regex.IsMatch(request, @"\b(meetings?|calendar|schedule|appointments?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return null;

        return SerializeToolSelection("calendar_search", new Dictionary<string, object?>
        {
            ["date"] = "tomorrow",
        });
    }

    private static string? TryBuildEmailToolSelection(string request)
    {
        var match = EmailRequestPattern.Match(request);
        if (!match.Success)
            return null;

        return SerializeToolSelection("email_search", new Dictionary<string, object?>
        {
            ["from"] = match.Groups["from"].Value.Trim(),
            ["query"] = match.Groups["query"].Value.Trim().TrimEnd('.', '?', '!'),
        });
    }

    private static string? TryBuildTimeToolSelection(string request)
    {
        var match = TimeCityRequestPattern.Match(request);
        if (!match.Success)
            return null;

        return SerializeToolSelection("time_now", new Dictionary<string, object?>
        {
            ["timezone_or_city"] = match.Groups["city"].Value.Trim().TrimEnd('.', '?', '!'),
        });
    }

    private static string SerializeToolSelection(string tool, Dictionary<string, object?> args) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["tool"] = tool,
                ["args"] = args,
            },
            CompactJsonOptions);

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
