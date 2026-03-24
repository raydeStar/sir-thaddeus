using System.Collections.Generic;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Search;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private async Task<AgentResponse?> TryHandleExplicitSearchContinuationAsync(
        string lowerIncoming,
        string contextualUserMessage,
        string memoryPackText,
        string personalityAnchor,
        string personalityTurnTag,
        RouterOutput route,
        ValidatedSlots validatedSlots,
        List<ToolCallRecord> toolCallsMade,
        LlmUsageSnapshot? usageBaseline,
        CancellationToken cancellationToken)
    {
        var explicitSearchContinuation =
            lowerIncoming.Contains("anything else", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what else", StringComparison.Ordinal);
        if (!explicitSearchContinuation ||
            IntentFeatureExtractor.LooksLikeScreenRequest(lowerIncoming) ||
            IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(memoryPackText))
            InjectMemoryIntoHistoryInPlace(_history, memoryPackText);
        InjectPersonalityAnchorIntoHistoryInPlace(_history, personalityAnchor, personalityTurnTag);

        var continuitySearchResponse = await _searchOrchestrator.ExecuteAsync(
            contextualUserMessage,
            memoryPackText,
            _history,
            toolCallsMade,
            ResolveLookupModeHint(route),
            cancellationToken);

        var hasLookupToolCall = toolCallsMade.Any(call =>
            call.ToolName.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
            call.ToolName.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase));
        if (!hasLookupToolCall)
        {
            var dialogueLocation = _dialogueStore.Get().LocationName;
            var followUpQuery = !string.IsNullOrWhiteSpace(dialogueLocation)
                ? $"{dialogueLocation} more news"
                : "more news";
            var followUpArgs = System.Text.Json.JsonSerializer.Serialize(new
            {
                query = followUpQuery,
                maxResults = 5,
                recency = "week"
            });

            var followUpToolName = "web_search";
            var followUpToolOk = false;
            string followUpToolResult;
            try
            {
                followUpToolResult = await _mcp.CallToolAsync(followUpToolName, followUpArgs, cancellationToken);
                followUpToolOk = true;
            }
            catch (Exception ex)
            {
                followUpToolResult = $"Tool error: {ex.Message}";
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName = followUpToolName,
                Arguments = followUpArgs,
                Result = followUpToolResult,
                Success = followUpToolOk
            });
        }

        var continuityText = SearchOrchestrator.StripOfflineReasoningPrefix(continuitySearchResponse.Text);
        if (!string.Equals(continuityText, continuitySearchResponse.Text, StringComparison.Ordinal))
            continuitySearchResponse = continuitySearchResponse with { Text = continuityText };

        if (continuitySearchResponse.Success)
            AppendAssistantMessage(continuitySearchResponse.Text);

        LogEvent("AGENT_RESPONSE", continuitySearchResponse.Text);
        return AttachContextSnapshot(
            _contextAnchoringService.AddLocationInferenceDisclosure(continuitySearchResponse, validatedSlots),
            usageBaseline);
    }

    private async Task<AgentResponse?> TryHandleUtilityIntentAsync(
        string lowerIncoming,
        string contextualUserMessage,
        RouterOutput route,
        ToolPlanDecision toolPlan,
        ValidatedSlots validatedSlots,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        bool hasRecentSearchContext,
        CancellationToken cancellationToken)
    {
        var activePersonality = _personalityRuntime.Snapshot.Profile;
        var utilityLocationHint = UserLocationHint;
        if (string.IsNullOrWhiteSpace(utilityLocationHint))
        {
            var dialogueLocation = _dialogueStore.Get().LocationName;
            if (!LocationContextHeuristics.IsClearlyNonPlace(dialogueLocation))
                utilityLocationHint = dialogueLocation;
        }

        AgentResponse? utilityResponse = null;
        var isSearchContinuationPrompt =
            hasRecentSearchContext &&
            SearchModeRouter.IsFollowUpMessage(lowerIncoming);
        if (!(isSearchContinuationPrompt && RouteArbitrationPolicy.IsLookupIntent(route.Intent)))
        {
            utilityResponse = await _utilityIntentHandler.TryHandleAsync(
                new UtilityIntentExecutionRequest
                {
                    UserMessage = contextualUserMessage,
                    Route = route,
                    ToolPlan = toolPlan,
                    ActivePersonalityId = activePersonality.Id,
                    ActivePersonalityDisplayName = activePersonality.DisplayName,
                    ActivePersonalitySelfName = activePersonality.Identity.SelfName,
                    ActivePersonalitySelfDescription = activePersonality.Identity.SelfDescription,
                    ValidatedSlots = validatedSlots,
                    ToolCallsMade = toolCallsMade,
                    RoundTrips = roundTrips,
                    UserLocationHint = utilityLocationHint,
                    PreferredUnits = PreferredUnits,
                    TryDeterministicMatch = _deterministicUtilityEngine.TryMatch,
                    ToUtilityResult = ToUtilityResult,
                    BuildFromToolPlan = BuildUtilityResultFromToolPlan,
                    TryContextFollowUp = _contextAnchoringService.TryHandleUtilityFollowUpWithContext,
                    TryInferWithLlmAsync = TryInferUtilityRouteWithLlmAsync,
                    RememberUtilityContext = utilityResult =>
                    {
                        var utilityPatch = _contextAnchoringService.TryBuildUtilityPatch(utilityResult);
                        if (utilityPatch is not null)
                            _contextAnchoringService.ApplyPatch(utilityPatch);
                    },
                    ExecuteWeatherAsync = async (message, utilityResult, calls, trips, token, slots) =>
                        await ExecuteWeatherUtilityAsync(
                            message,
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token,
                            slots),
                    ExecuteTimeAsync = async (message, utilityResult, calls, trips, token, slots) =>
                        await ExecuteTimeUtilityAsync(
                            message,
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token,
                            slots),
                    ExecuteHolidayAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteHolidayUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteFeedAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteFeedUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteStatusAsync = async (utilityResult, calls, trips, token) =>
                        await ExecuteStatusUtilityAsync(
                            utilityResult,
                            calls as List<ToolCallRecord> ?? toolCallsMade,
                            trips,
                            token),
                    ExecuteGenericToolCallAsync = async (utilityResult, calls, token) =>
                    {
                        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
                            return;

                        try
                        {
                            var toolResult = await _mcp.CallToolAsync(
                                utilityResult.McpToolName,
                                utilityResult.McpToolArgs,
                                token);
                            calls.Add(new ToolCallRecord
                            {
                                ToolName = utilityResult.McpToolName,
                                Arguments = utilityResult.McpToolArgs,
                                Result = toolResult,
                                Success = true
                            });
                        }
                        catch
                        {
                        }
                    },
                    BuildInlineResponse = BuildInlineUtilityResponse,
                    ShouldSuppressUiArtifacts = ShouldSuppressUtilityUiArtifacts,
                    LogEvent = LogEvent
                },
                cancellationToken);
        }

        return utilityResponse;
    }
}
