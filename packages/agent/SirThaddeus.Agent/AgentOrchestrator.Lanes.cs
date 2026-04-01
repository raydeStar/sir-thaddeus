using SirThaddeus.Agent.Lanes;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
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
}
