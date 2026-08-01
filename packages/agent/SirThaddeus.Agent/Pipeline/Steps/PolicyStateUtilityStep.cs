using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Answers narrow, explicit questions about one field of the live runtime
/// policy through the audited read-only MCP boundary. Ambiguous, compound,
/// conceptual, historical, hypothetical, deferred, and mutation requests pass
/// through unchanged.
/// </summary>
public sealed class PolicyStateUtilityStep : ITurnStep
{
    private const string ToolName = "policy.get_state";
    private static readonly Regex BoundaryPattern = new(
        @"\b(?:do\s+not|don't|without\s+(?:checking|looking|inspecting)|hypothetical(?:ly)?|suppose|tomorrow|later|not\s+now|yesterday|last\s+(?:week|month|tuesday)|what\s+does|what\s+(?:does|is).+mean|explain|describe)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex MutationPattern = new(
        @"^\s*(?:please\s+)?(?:set|change|disable|enable|turn\s+(?:on|off)|switch\s+(?:on|off))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CurrentCuePattern = new(
        @"\b(?:current(?:ly)?|right\s+now|now|live|active|runtime\s+policy|set\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IMcpToolClient _mcp;
    private readonly IChatEventSink _sink;
    private readonly Action<string, string>? _log;

    public PolicyStateUtilityStep(IMcpToolClient mcp, IChatEventSink? sink = null, Action<string, string>? log = null)
    {
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _sink = sink ?? NullChatEventSink.Instance;
        _log = log;
    }

    public string Name => "PolicyStateUtility";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var reason = "tool_unavailable";
        PolicyRequest request = default;
        if (!HasTool(context) || !TryClassify(context.UserText, out request, out reason))
        {
            LogActivation(context, false, reason);
            return new StepResult.Continue(context);
        }

        var activityId = $"policy-state-{Guid.NewGuid():N}";
        var started = Stopwatch.GetTimestamp();
        await _sink.ToolStartedAsync(activityId, context.ThreadId, context.MessageId, ToolName, "meta", "{}", cancellationToken).ConfigureAwait(false);

        string result;
        try
        {
            result = await _mcp.CallToolAsync(ToolName, "{}", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            await _sink.ToolCompletedAsync(activityId, context.ThreadId, context.MessageId, ToolName, false, elapsed, null, ex.Message, cancellationToken).ConfigureAwait(false);
            LogActivation(context, true, "tool_error");
            return TerminateFailure(ex.Message);
        }

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var success = TryProject(result, request, out var answer);
        await _sink.ToolCompletedAsync(activityId, context.ThreadId, context.MessageId, ToolName, success, durationMs, Truncate(result, 240), success ? null : "invalid_policy_state", cancellationToken).ConfigureAwait(false);

        var record = new ToolCallRecord { ToolName = ToolName, Arguments = "{}", Result = result, Success = success };
        LogActivation(context, true, success ? request.Field.ToString() : "invalid_policy_state");
        if (!success)
            return TerminateFailure("The live policy response was unavailable.", record);

        return new StepResult.Terminate(new AgentResponse
        {
            Text = answer,
            Success = true,
            ToolCallsMade = [record],
            LlmRoundTrips = 0,
            SuppressSourceCardsUi = true,
        });
    }

    private static bool TryClassify(string? text, out PolicyRequest request, out string reason)
    {
        request = default;
        reason = "not_live_single_field";
        if (string.IsNullOrWhiteSpace(text) || !CurrentCuePattern.IsMatch(text) || BoundaryPattern.IsMatch(text) || MutationPattern.IsMatch(text))
            return false;

        var fields = new List<PolicyField>();
        var lower = text.ToLowerInvariant();
        AddIf(fields, lower.Contains("panic mode") || lower.Contains("panic flag"), PolicyField.PanicMode);
        AddIf(fields, lower.Contains("safe mode") || lower.Contains("safe-mode") || lower.Contains("safe mode flag"), PolicyField.SafeMode);
        AddIf(fields, Regex.IsMatch(lower, @"(?:budgets?|budget\s+limits?).{0,24}(?:enabled|disabled|switched|active)|(?:enabled|disabled).{0,24}(?:budgets?|budget\s+limits?)"), PolicyField.BudgetsEnabled);
        AddIf(fields, Regex.IsMatch(lower, @"(?:max(?:imum)?\s+)?tool[- ]?calls?.{0,24}per[- ]?turn|per[- ]?turn.{0,24}tool[- ]?call"), PolicyField.MaxToolCallsPerTurn);
        AddIf(fields, Regex.IsMatch(lower, @"(?:max(?:imum)?\s+)?tool[- ]?calls?.{0,24}per[- ]?session|session[- ]?wide.{0,24}tool[- ]?call"), PolicyField.MaxToolCallsPerSession);
        AddIf(fields, Regex.IsMatch(lower, @"(?:max[_ ]?)?web[_ -]?pulls?.{0,24}per[- ]?turn|web[_ -]?pulls[_ -]?per[_ -]?turn"), PolicyField.MaxWebPullsPerTurn);
        AddIf(fields, Regex.IsMatch(lower, @"(?:max[_ ]?)?file[_ -]?(?:operations|ops).{0,24}per[_ -]?minute|file[_ -]?ops[_ -]?per[_ -]?minute"), PolicyField.MaxFileOpsPerMinute);
        AddPermission(fields, lower, "screen", PolicyField.ScreenPermission);
        AddPermission(fields, lower, "files", PolicyField.FilesPermission);
        AddPermission(fields, lower, "system", PolicyField.SystemPermission);
        AddPermission(fields, lower, "web", PolicyField.WebPermission);
        AddPermission(fields, lower, "memory_read", PolicyField.MemoryReadPermission);
        AddPermission(fields, lower, "memory_write", PolicyField.MemoryWritePermission);

        fields = fields.Distinct().ToList();
        if (fields.Count != 1 || IsCompound(text))
            return false;

        request = new PolicyRequest(fields[0], SelectFormat(text));
        reason = fields[0].ToString();
        return true;
    }

    private static bool TryProject(string json, PolicyRequest request, out string answer)
    {
        answer = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
                return false;
            if (TryBoolean(root, request.Field, out var boolean))
            {
                answer = FormatBoolean(boolean, request.Format);
                return true;
            }
            if (TryNumber(root, request.Field, out var number))
            {
                answer = number.ToString(CultureInfo.InvariantCulture);
                return true;
            }
            if (TryPermission(root, request.Field, out var permission) && permission is "always" or "ask" or "off")
            {
                answer = permission;
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool TryBoolean(JsonElement root, PolicyField field, out bool value)
    {
        value = false;
        JsonElement element;
        if (field == PolicyField.BudgetsEnabled)
        {
            if (!root.TryGetProperty("budgets", out var budgets) || !budgets.TryGetProperty("enabled", out element)) return false;
        }
        else
        {
            var name = field switch { PolicyField.PanicMode => "panic_mode", PolicyField.SafeMode => "safe_mode", _ => null };
            if (name is null || !root.TryGetProperty(name, out element)) return false;
        }
        if (element.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = element.GetBoolean();
        return true;
    }

    private static bool TryNumber(JsonElement root, PolicyField field, out int value)
    {
        value = 0;
        var name = field switch
        {
            PolicyField.MaxToolCallsPerTurn => "max_tool_calls_per_turn",
            PolicyField.MaxToolCallsPerSession => "max_tool_calls_per_session",
            PolicyField.MaxWebPullsPerTurn => "max_web_pulls_per_turn",
            PolicyField.MaxFileOpsPerMinute => "max_file_ops_per_minute",
            _ => null,
        };
        return name is not null && root.TryGetProperty("budgets", out var budgets) && budgets.TryGetProperty(name, out var element) && element.TryGetInt32(out value);
    }

    private static bool TryPermission(JsonElement root, PolicyField field, out string value)
    {
        value = string.Empty;
        var name = field switch
        {
            PolicyField.ScreenPermission => "screen", PolicyField.FilesPermission => "files",
            PolicyField.SystemPermission => "system", PolicyField.WebPermission => "web",
            PolicyField.MemoryReadPermission => "memory_read", PolicyField.MemoryWritePermission => "memory_write", _ => null,
        };
        if (name is null || !root.TryGetProperty("enabled_tool_groups", out var groups) || !groups.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
        return value.Length > 0;
    }

    private static string FormatBoolean(bool value, AnswerFormat format) => format switch
    {
        AnswerFormat.OnOff => value ? "ON" : "OFF",
        AnswerFormat.ActiveInactive => value ? "ACTIVE" : "INACTIVE",
        AnswerFormat.EnabledDisabled => value ? "ENABLED" : "DISABLED",
        _ => value ? "YES" : "NO",
    };

    private static AnswerFormat SelectFormat(string text)
    {
        if (Regex.IsMatch(text, @"\bon\s+or\s+off\b", RegexOptions.IgnoreCase)) return AnswerFormat.OnOff;
        if (Regex.IsMatch(text, @"\bactive\s+or\s+inactive\b", RegexOptions.IgnoreCase)) return AnswerFormat.ActiveInactive;
        if (Regex.IsMatch(text, @"\benabled\s+or\s+disabled\b", RegexOptions.IgnoreCase)) return AnswerFormat.EnabledDisabled;
        return AnswerFormat.YesNo;
    }

    private static bool IsCompound(string text) => Regex.IsMatch(text, @"\b(?:and\s+then|and\s+(?:explain|calculate|compute|also)|both)\b", RegexOptions.IgnoreCase);
    private static void AddIf(List<PolicyField> fields, bool condition, PolicyField field) { if (condition) fields.Add(field); }
    private static void AddPermission(List<PolicyField> fields, string text, string group, PolicyField field)
    {
        var label = Regex.Escape(group).Replace("_", "[_ -]");
        if (Regex.IsMatch(text, $@"(?:{label}).{{0,24}}permission|permission.{{0,24}}(?:{label})|value\s+for\s+{label}")) fields.Add(field);
    }
    private static bool HasTool(TurnContext context) => context.ToolDefs.Any(def => string.Equals(def.Function?.Name, ToolName, StringComparison.OrdinalIgnoreCase));
    private void LogActivation(TurnContext context, bool activated, string reason) => _log?.Invoke("EXPERIMENT_ACTIVATION", $"thread_id={context.ThreadId} turn_id={context.MessageId} event=policy_state_deterministic_utility decision={(activated ? "activated" : "inactive")} reason={reason}");
    private static StepResult TerminateFailure(string error, ToolCallRecord? record = null) => new StepResult.Terminate(new AgentResponse { Text = "I couldn't read the live runtime policy just now.", Success = false, Error = error, ToolCallsMade = record is null ? [] : [record], LlmRoundTrips = 0 });
    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    private readonly record struct PolicyRequest(PolicyField Field, AnswerFormat Format);
    private enum AnswerFormat { YesNo, OnOff, ActiveInactive, EnabledDisabled }
    private enum PolicyField { PanicMode, SafeMode, BudgetsEnabled, MaxToolCallsPerTurn, MaxToolCallsPerSession, MaxWebPullsPerTurn, MaxFileOpsPerMinute, ScreenPermission, FilesPermission, SystemPermission, WebPermission, MemoryReadPermission, MemoryWritePermission }
}
