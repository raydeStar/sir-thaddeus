using SirThaddeus.Agent.Lanes;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private readonly Lanes.ExplainLane _explainLane;

    private async Task<AgentResponse?> TryExecuteLaneFastPathAsync(
        string userMessage,
        RouterOutput route,
        bool allowLookupLane,
        LaneRoutingResult laneResult,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        return laneResult.Lane switch
        {
            TaskLane.Lookup when allowLookupLane => await TryExecuteCheckLaneAsync(
                userMessage,
                laneResult,
                memoryPackText,
                toolCallsMade,
                roundTrips,
                cancellationToken),
            TaskLane.Explain when ShouldFastPathExplainLane(userMessage, route, allowLookupLane) => await TryExecuteExplainLaneAsync(
                userMessage,
                memoryPackText,
                toolCallsMade,
                roundTrips,
                cancellationToken),
            _ => null
        };
    }

    private static bool ShouldFastPathExplainLane(
        string userMessage,
        RouterOutput route,
        bool allowLookupLane)
    {
        if (!allowLookupLane)
            return false;

        if (!route.Intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) &&
            !route.Intent.Equals(Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase))
            return false;

        var lower = userMessage.Trim().ToLowerInvariant();
        return lower.Contains("summarize", StringComparison.Ordinal) ||
               lower.Contains("describe", StringComparison.Ordinal) ||
               lower.Contains("explain", StringComparison.Ordinal) ||
               lower.Contains("tell me about", StringComparison.Ordinal) ||
               lower.Contains("what is this", StringComparison.Ordinal) ||
               lower.Contains("what's this", StringComparison.Ordinal) ||
               lower.Contains("what does this mean", StringComparison.Ordinal) ||
               lower.Contains("is this legit", StringComparison.Ordinal);
    }

    /// <summary>
    /// Attempts a fast Check Lane execution for simple Lookup-lane queries.
    /// Returns null if the Check Lane doesn't apply or can't produce an answer,
    /// allowing the caller to fall through to the standard search path.
    /// </summary>
    private async Task<AgentResponse?> TryExecuteCheckLaneAsync(
        string userMessage,
        LaneRoutingResult laneResult,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (laneResult.Lane != TaskLane.Lookup)
            return null;

        LogEvent("CHECK_LANE_START", "Attempting fast check-lane path.");

        // Step 1: Extract entity/attribute.
        var extraction = await _checkLane.ExtractEntityAsync(userMessage, cancellationToken);

        // If entity is unclear, return a clarifying question.
        if (CheckLane.NeedsClarification(extraction))
        {
            var clarification = CheckLane.BuildClarifyingQuestion(userMessage);
            LogEvent("CHECK_LANE_CLARIFICATION", $"Entity unclear: {clarification}");
            AppendAssistantMessage(clarification);
            return new AgentResponse
            {
                Text = clarification,
                Success = true,
                LlmRoundTrips = roundTrips + 1
            };
        }

        LogEvent("CHECK_LANE_ENTITY",
            $"entity={extraction!.Entity}, attribute={extraction.Attribute}" +
            (extraction.Qualifier is not null ? $", qualifier={extraction.Qualifier}" : ""));

        // Step 2: Run targeted web search via SearchOrchestrator.
        var searchQuery = CheckLane.BuildSearchQuery(extraction);

        try
        {
            var searchResponse = await _searchOrchestrator.ExecuteAsync(
                searchQuery,
                memoryPackText,
                _history,
                toolCallsMade,
                LookupModeHint.Fact,
                cancellationToken);

            if (!searchResponse.Success || string.IsNullOrWhiteSpace(searchResponse.Text))
            {
                LogEvent("CHECK_LANE_SEARCH_FAILED", "Search returned no results — falling through.");
                return null;
            }

            // Step 3: Format with source citation + confidence caveat.
            var formattedResponse = await _checkLane.FormatResponseAsync(
                userMessage, extraction, searchResponse.Text, cancellationToken);

            LogEvent("CHECK_LANE_COMPLETE", $"Returning formatted check-lane response.");
            AppendAssistantMessage(formattedResponse);

            return searchResponse with
            {
                Text = formattedResponse,
                LlmRoundTrips = roundTrips + 2 // extraction + format
            };
        }
        catch (Exception ex)
        {
            LogEvent("CHECK_LANE_ERROR", $"Check lane failed: {ex.Message} — falling through to standard path.");
            return null;
        }
    }

    /// <summary>
    /// Attempts a focused Explain Lane execution for web-backed explanation
    /// requests. Chat-only, screen, and file explanations continue to use the
    /// existing route-specific handlers.
    /// </summary>
    private async Task<AgentResponse?> TryExecuteExplainLaneAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        LogEvent("EXPLAIN_LANE_START", "Attempting explain-lane path.");

        var request = await _explainLane.ExtractRequestAsync(userMessage, cancellationToken);
        if (ExplainLane.NeedsClarification(request))
        {
            var clarification = ExplainLane.BuildClarifyingQuestion(userMessage);
            LogEvent("EXPLAIN_LANE_CLARIFICATION", $"Topic unclear: {clarification}");
            AppendAssistantMessage(clarification);
            return new AgentResponse
            {
                Text = clarification,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips + 1
            };
        }

        LogEvent("EXPLAIN_LANE_TOPIC",
            $"topic={request!.Topic}, goal={request.Goal}" +
            (request.Context is not null ? $", context={request.Context}" : ""));

        try
        {
            var searchResponse = await _searchOrchestrator.ExecuteAsync(
                ExplainLane.BuildSearchQuery(request),
                memoryPackText,
                _history,
                toolCallsMade,
                LookupModeHint.DeepDive,
                cancellationToken);

            if (!searchResponse.Success || string.IsNullOrWhiteSpace(searchResponse.Text))
            {
                LogEvent("EXPLAIN_LANE_SEARCH_FAILED", "Search returned no results — falling through.");
                return null;
            }

                var formattedResponse = await _explainLane.FormatSearchSummaryAsync(
                    userMessage,
                    request,
                    searchResponse.Text,
                    _personalityRuntime.BuildSystemPrompt(_systemPrompt),
                    cancellationToken);

            LogEvent("EXPLAIN_LANE_COMPLETE", "Returning web-grounded explanation.");
            AppendAssistantMessage(formattedResponse);

            return searchResponse with
            {
                Text = formattedResponse,
                LlmRoundTrips = roundTrips + 2
            };
        }
        catch (Exception ex)
        {
            LogEvent("EXPLAIN_LANE_ERROR", $"Explain lane failed: {ex.Message} — falling through to standard path.");
            return null;
        }
    }
}
