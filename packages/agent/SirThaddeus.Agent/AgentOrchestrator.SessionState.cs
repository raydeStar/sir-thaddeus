using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    /// <inheritdoc />
    public void ResetConversation()
    {
        var preserveLock = _dialogueStore.Get().ContextLocked;
        _history.Clear();
        _history.Add(ChatMessage.System(BuildEffectiveSystemPrompt()));
        _searchOrchestrator.Session.Clear();
        _dialogueStore.Reset();
        if (preserveLock)
            _dialogueStore.Update(_dialogueStore.Get() with { ContextLocked = true });
        _lastPlaceContextName = null;
        _lastPlaceContextCountryCode = null;
        _lastPlaceContextAt = default;
        _lastUtilityContextKey = null;
        _lastUtilityContextAt = default;
        _lastFirstPrinciplesRationale = [];
        _lastFirstPrinciplesAt = default;
        LogEvent("AGENT_RESET", "Conversation history and search session cleared.");
    }

    /// <inheritdoc />
    public void SeedDialogueState(DialogueState state)
    {
        _dialogueStore.Seed(state);
    }

    /// <inheritdoc />
    public DialogueContextSnapshot GetContextSnapshot() =>
        _dialogueStore.Get().ToSnapshot();

    /// <inheritdoc />
    public async Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _mcp.ListToolsAsync(cancellationToken);
            return tools.Count;
        }
        catch
        {
            return 0;
        }
    }

    private void UpdateDialogueStateFromValidatedSlots(ValidatedSlots slots)
    {
        var current = _dialogueStore.Get();

        var locationName = current.LocationName;
        var countryCode = current.CountryCode;
        var regionCode = current.RegionCode;
        if (!current.ContextLocked || slots.ExplicitLocationChange)
        {
            if (!string.IsNullOrWhiteSpace(slots.LocationText))
                locationName = slots.LocationText;
            if (!string.IsNullOrWhiteSpace(slots.CountryCode))
                countryCode = slots.CountryCode;
            if (!string.IsNullOrWhiteSpace(slots.RegionCode))
                regionCode = slots.RegionCode;
        }

        _dialogueStore.Update(current with
        {
            Topic = string.IsNullOrWhiteSpace(slots.Topic) ? current.Topic : slots.Topic!,
            LocationName = locationName,
            CountryCode = countryCode,
            RegionCode = regionCode,
            TimeScope = string.IsNullOrWhiteSpace(slots.TimeScope) ? current.TimeScope : slots.TimeScope,
            LocationInferred = slots.LocationInferred,
            GeocodeMismatch = slots.GeocodeMismatch
        });
    }

    private UtilityRouter.UtilityResult? BuildUtilityResultFromToolPlan(
        ToolPlanDecision plan,
        string normalizedMessage)
    {
        if (plan is null || string.Equals(plan.Category, "none", StringComparison.OrdinalIgnoreCase))
            return null;

        var plannerMessage = string.IsNullOrWhiteSpace(plan.PlannerMessage)
            ? normalizedMessage
            : plan.PlannerMessage;

        var utility = UtilityRouter.TryHandle(plannerMessage, UserLocationHint, PreferredUnits);
        if (utility is null)
        {
            utility = new UtilityRouter.UtilityResult
            {
                Category = plan.Category,
                Answer = plan.InlineAnswer ?? $"[{plan.Category}]"
            };
        }

        if (!string.IsNullOrWhiteSpace(plan.InlineAnswer))
            utility = utility with { Answer = plan.InlineAnswer };

        if (plan.ToolCalls.Count > 0)
        {
            var first = plan.ToolCalls[0];
            utility = utility with
            {
                McpToolName = first.ToolName,
                McpToolArgs = first.ArgumentsJson
            };
        }

        return utility;
    }

    private AgentResponse AddLocationInferenceDisclosure(
        AgentResponse response,
        ValidatedSlots? validatedSlots)
    {
        if (validatedSlots is null ||
            !validatedSlots.LocationInferred ||
            string.IsNullOrWhiteSpace(validatedSlots.LocationText))
        {
            return response;
        }

        var note = $"Using your previous location context (**{validatedSlots.LocationText}**).";
        if (response.Text.Contains(note, StringComparison.OrdinalIgnoreCase))
            return response;

        return response with
        {
            Text = $"{note}\n\n{response.Text}"
        };
    }

    private AgentResponse AttachContextSnapshot(
        AgentResponse response,
        LlmUsageSnapshot? usageBaseline = null)
    {
        var latestUserMessage = _history.LastOrDefault(m => m.Role == "user")?.Content;
        var sanitizedText = _postProcessor.SanitizeFinalResponse(
            response.Text,
            response.ToolCallsMade,
            latestUserMessage,
            allowToolResultPersonalityPresentation: response.AllowToolResultPersonalityPresentation);
        if (!string.Equals(sanitizedText, response.Text, StringComparison.Ordinal))
        {
            LogEvent("RESPONSE_SANITIZED",
                "Removed leaked markers or unsupported capability claims.");
            response = response with { Text = sanitizedText };
        }

        if (response.ToolCallsMade.Any(call =>
                call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase)))
        {
            _lastLookupToolCallAt = _timeProvider.GetUtcNow();
        }

        var current = _dialogueStore.Get();
        var summaryText = BuildRollingSummary(response.Text);
        _dialogueStore.Update(current with { RollingSummary = summaryText });
        var contextSnapshot = _dialogueStore.Get().ToSnapshot();
        var tokenUsage = BuildTurnTokenUsage(usageBaseline) ?? response.TokenUsage;
        return response with
        {
            ContextSnapshot = contextSnapshot,
            TokenUsage = tokenUsage
        };
    }

    private LlmUsageSnapshot? CaptureUsageSnapshot()
    {
        if (_llm is not ILlmUsageTelemetry telemetry)
            return null;

        return telemetry.GetUsageSnapshot();
    }

    private AgentTokenUsage? BuildTurnTokenUsage(LlmUsageSnapshot? usageBaseline)
    {
        if (usageBaseline is null || _llm is not ILlmUsageTelemetry telemetry)
            return null;

        var current = telemetry.GetUsageSnapshot();
        var tokensInDelta = Math.Max(0L, current.PromptTokens - usageBaseline.PromptTokens);
        var tokensOutDelta = Math.Max(0L, current.CompletionTokens - usageBaseline.CompletionTokens);
        var totalTokensDelta = Math.Max(0L, current.TotalTokens - usageBaseline.TotalTokens);

        if (tokensInDelta == 0 && tokensOutDelta == 0 && totalTokensDelta == 0)
            return null;

        var contextWindow = current.ContextWindowTokens > 0
            ? current.ContextWindowTokens
            : usageBaseline.ContextWindowTokens;
        if (contextWindow <= 0)
            contextWindow = 8192;

        var contextFillPercent = (int)Math.Clamp(
            Math.Round(tokensInDelta * 100d / contextWindow),
            0,
            100);

        return new AgentTokenUsage
        {
            TokensIn = ClampLongToInt(tokensInDelta),
            TokensOut = ClampLongToInt(tokensOutDelta),
            TotalTokens = ClampLongToInt(totalTokensDelta),
            ContextWindowTokens = contextWindow,
            ContextFillPercent = contextFillPercent
        };
    }

    private static int ClampLongToInt(long value)
        => value >= int.MaxValue ? int.MaxValue : (int)value;

    private static string BuildRollingSummary(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (compact.Length > 180)
            compact = compact[..180].TrimEnd() + "...";
        return compact;
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string BuildInlineUtilityResponse(UtilityRouter.UtilityResult utilityResult)
    {
        var primary = (utilityResult.Answer ?? "").Trim();
        if (string.IsNullOrWhiteSpace(primary))
            primary = "Done.";

        var personalityLine = utilityResult.Category.ToLowerInvariant() switch
        {
            "calculator"  => "Need another quick one? Toss over the next math step.",
            "conversion"  => "Need another unit converted?",
            "fact"        => "Want a quick benchmark comparison next?",
            "weather"     => "Stay prepared out there — anything else weather-related?",
            "time"        => "Time waits for no one. Need a timezone comparison?",
            "time_local"  => "Right on the clock. Anything else time-sensitive?",
            "holiday"     => "Mark your calendar. Want to check another date?",
            "feed"        => "That's the latest from the feed. Want me to dig deeper into any item?",
            "status"      => "Status confirmed. Want me to keep an eye on it?",
            "meta"        => "That's the toolkit rundown. Want details on any specific capability?",
            "text"        => "Quick count complete. Another text analysis?",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(personalityLine)
            ? primary
            : $"{primary}\n\n{personalityLine}";
    }

    private static bool ShouldSuppressUtilityUiArtifacts(string category) =>
        category.Equals("calculator", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("conversion", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("fact", StringComparison.OrdinalIgnoreCase) ||
        category.Equals("text", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength] + "\u2026";

    private void LogEvent(string action, string detail)
    {
        try
        {
            _audit.Append(new AuditEvent
            {
                Actor = "agent",
                Action = action,
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["detail"] = detail
                }
            });
        }
        catch
        {
            // Agent logic must proceed even if audit I/O temporarily fails.
        }
    }
}
