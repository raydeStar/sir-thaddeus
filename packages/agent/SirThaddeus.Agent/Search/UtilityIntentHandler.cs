using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Handles utility intent execution (weather/time/holiday/feed/status + inline).
/// </summary>
public sealed class UtilityIntentHandler : IUtilityIntentHandler
{
    public async Task<AgentResponse?> TryHandleAsync(
        UtilityIntentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = request.UserMessage ?? "";
        var route = request.Route ?? new RouterOutput { Intent = Intents.ChatOnly };
        var toolPlan = request.ToolPlan ?? new ToolPlanDecision();
        var deterministicRouteRequested = string.Equals(
            route.Intent,
            Intents.UtilityDeterministic,
            StringComparison.OrdinalIgnoreCase);

        UtilityRouter.UtilityResult? utilityResult = null;

        if (request.TryDeterministicMatch is not null &&
            request.ToUtilityResult is not null)
        {
            var deterministicMatch = request.TryDeterministicMatch(message);
            if (deterministicMatch is not null)
            {
                utilityResult = request.ToUtilityResult(deterministicMatch);
                request.LogEvent?.Invoke(
                    "DETERMINISTIC_INLINE_ROUTE",
                    $"confidence={deterministicMatch.Confidence}, category={deterministicMatch.Result.Category}");
            }
        }

        if (utilityResult is null && request.BuildFromToolPlan is not null)
            utilityResult = request.BuildFromToolPlan(toolPlan, message);

        if (utilityResult is null && request.TryContextFollowUp is not null)
            utilityResult = request.TryContextFollowUp(message) ?? UtilityRouter.TryHandle(message);
        else
            utilityResult ??= UtilityRouter.TryHandle(message);

        if (utilityResult is null &&
            !deterministicRouteRequested &&
            request.TryInferWithLlmAsync is not null)
        {
            utilityResult = await request.TryInferWithLlmAsync(message, cancellationToken);
        }

        if (utilityResult is null && deterministicRouteRequested)
        {
            request.LogEvent?.Invoke(
                "DETERMINISTIC_INLINE_MISS",
                "Pre-router selected deterministic path, but utility parse failed at execution.");
            return null;
        }

        if (utilityResult is null)
            return null;

        request.LogEvent?.Invoke("UTILITY_BYPASS", $"category={utilityResult.Category}");
        request.RememberUtilityContext?.Invoke(utilityResult);

        if (string.Equals(utilityResult.Category, "weather", StringComparison.OrdinalIgnoreCase) &&
            request.ExecuteWeatherAsync is not null)
        {
            return await request.ExecuteWeatherAsync(
                message,
                utilityResult,
                request.ToolCallsMade,
                request.RoundTrips,
                cancellationToken,
                request.ValidatedSlots);
        }

        if (string.Equals(utilityResult.Category, "time", StringComparison.OrdinalIgnoreCase) &&
            request.ExecuteTimeAsync is not null)
        {
            return await request.ExecuteTimeAsync(
                message,
                utilityResult,
                request.ToolCallsMade,
                request.RoundTrips,
                cancellationToken,
                request.ValidatedSlots);
        }

        if (string.Equals(utilityResult.Category, "holiday", StringComparison.OrdinalIgnoreCase) &&
            request.ExecuteHolidayAsync is not null)
        {
            return await request.ExecuteHolidayAsync(
                utilityResult,
                request.ToolCallsMade,
                request.RoundTrips,
                cancellationToken);
        }

        if (string.Equals(utilityResult.Category, "feed", StringComparison.OrdinalIgnoreCase) &&
            request.ExecuteFeedAsync is not null)
        {
            return await request.ExecuteFeedAsync(
                utilityResult,
                request.ToolCallsMade,
                request.RoundTrips,
                cancellationToken);
        }

        if (string.Equals(utilityResult.Category, "status", StringComparison.OrdinalIgnoreCase) &&
            request.ExecuteStatusAsync is not null)
        {
            return await request.ExecuteStatusAsync(
                utilityResult,
                request.ToolCallsMade,
                request.RoundTrips,
                cancellationToken);
        }

        if (utilityResult.McpToolName is not null && utilityResult.McpToolArgs is not null)
        {
            if (request.ExecuteGenericToolCallAsync is not null)
            {
                await request.ExecuteGenericToolCallAsync(
                    utilityResult,
                    request.ToolCallsMade,
                    cancellationToken);
            }

            return null;
        }

        var text = request.BuildInlineResponse is not null
            ? request.BuildInlineResponse(utilityResult)
            : utilityResult.Answer ?? "Done.";
        var suppressUiArtifacts = request.ShouldSuppressUiArtifacts?.Invoke(utilityResult.Category) ?? false;

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = request.ToolCallsMade.ToList(),
            LlmRoundTrips = 0,
            SuppressSourceCardsUi = suppressUiArtifacts,
            SuppressToolActivityUi = suppressUiArtifacts
        };
    }
}
