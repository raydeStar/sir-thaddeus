using SirThaddeus.Agent.Dialogue;
using System.Text.Json;

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

        if (string.Equals(utilityResult.Category, "meta", StringComparison.OrdinalIgnoreCase))
        {
            if (utilityResult.McpToolName is not null &&
                utilityResult.McpToolArgs is not null &&
                request.ExecuteGenericToolCallAsync is not null)
            {
                await request.ExecuteGenericToolCallAsync(
                    utilityResult,
                    request.ToolCallsMade,
                    cancellationToken);
            }

            var summary = BuildMetaCapabilitiesSummary(request.ToolCallsMade);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return new AgentResponse
                {
                    Text = summary,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade.ToList(),
                    LlmRoundTrips = request.RoundTrips
                };
            }

            return null;
        }

        if (string.Equals(utilityResult.Category, "time_local", StringComparison.OrdinalIgnoreCase))
        {
            if (utilityResult.McpToolName is not null &&
                utilityResult.McpToolArgs is not null &&
                request.ExecuteGenericToolCallAsync is not null)
            {
                await request.ExecuteGenericToolCallAsync(
                    utilityResult,
                    request.ToolCallsMade,
                    cancellationToken);
            }

            var summary = BuildLocalTimeSummary(request.ToolCallsMade);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return new AgentResponse
                {
                    Text = summary,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade.ToList(),
                    LlmRoundTrips = request.RoundTrips
                };
            }

            return null;
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

    private static string? BuildMetaCapabilitiesSummary(IList<ToolCallRecord> calls)
    {
        var capabilityCall = calls
            .LastOrDefault(call => string.Equals(
                call.ToolName,
                "tool_list_capabilities",
                StringComparison.OrdinalIgnoreCase));
        if (capabilityCall is null || string.IsNullOrWhiteSpace(capabilityCall.Result))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(capabilityCall.Result);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                if (!item.TryGetProperty("category", out var categoryEl) ||
                    categoryEl.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var category = categoryEl.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(category))
                    groups.Add(category);
            }

            if (groups.Count == 0)
                return "Capability groups are currently unavailable from the manifest response.";

            var ordered = groups
                .OrderBy(group => CategoryRank(group))
                .ThenBy(group => group, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var listText = string.Join(", ", ordered);
            return $"Available capability groups: {listText}.\n\n" +
                   "Use `tool_list_capabilities` for per-tool aliases, limits, and permission requirements.";
        }
        catch
        {
            return null;
        }
    }

    private static int CategoryRank(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "memory" => 0,
            "web" => 1,
            "file" => 2,
            "system" => 3,
            "screen" => 4,
            "meta" => 5,
            "time" => 6,
            _ => 100
        };
    }

    private static string? BuildLocalTimeSummary(IList<ToolCallRecord> calls)
    {
        var timeCall = calls
            .LastOrDefault(call => string.Equals(
                call.ToolName,
                "time_now",
                StringComparison.OrdinalIgnoreCase));
        if (timeCall is null || string.IsNullOrWhiteSpace(timeCall.Result))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(timeCall.Result);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var iso = root.TryGetProperty("iso", out var isoEl) && isoEl.ValueKind == JsonValueKind.String
                ? isoEl.GetString()
                : null;
            var timezone = root.TryGetProperty("timezone", out var timezoneEl) && timezoneEl.ValueKind == JsonValueKind.String
                ? timezoneEl.GetString()
                : "local timezone";
            var offset = root.TryGetProperty("offset", out var offsetEl) && offsetEl.ValueKind == JsonValueKind.String
                ? offsetEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(iso) &&
                DateTimeOffset.TryParse(iso, out var parsed))
            {
                var formatted = parsed.ToString("h:mm:ss tt");
                var offsetText = !string.IsNullOrWhiteSpace(offset) ? $"UTC{offset}" : "current UTC offset";
                return $"The current local time is {formatted} in {timezone} ({offsetText}).";
            }

            return $"The current local time is available for {timezone}.";
        }
        catch
        {
            return null;
        }
    }
}
