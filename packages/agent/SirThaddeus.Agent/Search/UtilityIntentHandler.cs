using SirThaddeus.Agent.Dialogue;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Handles utility intent execution (weather/time/holiday/feed/status + inline).
/// </summary>
public sealed class UtilityIntentHandler : IUtilityIntentHandler
{
    private static readonly Regex MultiSpaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

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

        var identityResponse = TryBuildIdentityResponse(request, message);
        if (identityResponse is not null)
            return identityResponse;

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
            utilityResult = request.TryContextFollowUp(message) ?? UtilityRouter.TryHandle(message, request.UserLocationHint, request.PreferredUnits);
        else
            utilityResult ??= UtilityRouter.TryHandle(message, request.UserLocationHint, request.PreferredUnits);

        if (utilityResult is null &&
            !deterministicRouteRequested &&
            request.TryInferWithLlmAsync is not null &&
            ShouldUseLlmUtilityInference(route, message))
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

        if (string.Equals(utilityResult.Category, "meta_health", StringComparison.OrdinalIgnoreCase))
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

            var summary = BuildMetaToolPingSummary(request.ToolCallsMade);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return new AgentResponse
                {
                    Text = summary,
                    Success = true,
                    ToolCallsMade = request.ToolCallsMade.ToList(),
                    LlmRoundTrips = request.RoundTrips,
                    SuppressSourceCardsUi = true,
                    SuppressToolActivityUi = true
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

    private static AgentResponse? TryBuildIdentityResponse(
        UtilityIntentExecutionRequest request,
        string message)
    {
        var normalized = NormalizeIdentityPrompt(message);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        var asksName = IsNamePrompt(normalized);
        var asksAboutSelf = !asksName && IsSelfDescriptionPrompt(normalized);
        if (!asksName && !asksAboutSelf)
            return null;

        var assistantName = ResolveAssistantName(request);
        var selfDescription = (request.ActivePersonalitySelfDescription ?? "").Trim();

        var text = asksName
            ? BuildNameAnswer(assistantName, selfDescription)
            : BuildSelfDescriptionAnswer(assistantName, selfDescription);

        request.LogEvent?.Invoke("UTILITY_BYPASS", "category=identity");

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = request.ToolCallsMade.ToList(),
            LlmRoundTrips = request.RoundTrips,
            SuppressSourceCardsUi = true,
            SuppressToolActivityUi = true
        };
    }

    private static string NormalizeIdentityPrompt(string message)
    {
        var normalized = (message ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return "";

        normalized = normalized
            .Replace("?", " ", StringComparison.Ordinal)
            .Replace("!", " ", StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(":", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        normalized = MultiSpaceRegex.Replace(normalized, " ").Trim();

        var leadIns = new[]
        {
            "hey ",
            "hi ",
            "hello ",
            "can you ",
            "could you ",
            "would you ",
            "please "
        };

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var lead in leadIns)
            {
                if (!normalized.StartsWith(lead, StringComparison.Ordinal))
                    continue;

                normalized = normalized[lead.Length..].TrimStart();
                changed = true;
            }
        }

        return normalized;
    }

    private static bool IsNamePrompt(string normalized)
        => normalized is
            "what is your name" or
            "what s your name" or
            "whats your name" or
            "tell me your name" or
            "your name";

    private static bool IsSelfDescriptionPrompt(string normalized)
        => normalized is
            "who are you" or
            "describe yourself" or
            "tell me about yourself" or
            "introduce yourself" or
            "describe your personality" or
            "what are you like";

    private static string ResolveAssistantName(UtilityIntentExecutionRequest request)
    {
        var selfName = (request.ActivePersonalitySelfName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(selfName))
            return selfName;

        var displayName = (request.ActivePersonalityDisplayName ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(displayName))
            return displayName;

        var profileId = (request.ActivePersonalityId ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(profileId))
            return profileId.Replace("_", " ", StringComparison.Ordinal);

        return "your assistant";
    }

    private static string BuildNameAnswer(string assistantName, string selfDescription)
    {
        var headline = $"My name is {assistantName}.";
        var tagline = ExtractIdentityTagline(selfDescription);
        if (string.IsNullOrWhiteSpace(tagline))
            return headline;

        return $"{headline}\n\n{tagline}";
    }

    private static string BuildSelfDescriptionAnswer(string assistantName, string selfDescription)
    {
        if (string.IsNullOrWhiteSpace(selfDescription))
            return $"I am {assistantName}, and I focus on clear, practical help.";

        if (selfDescription.StartsWith("I ", StringComparison.OrdinalIgnoreCase))
            return selfDescription;

        return $"I am {assistantName}. {selfDescription}";
    }

    private static string ExtractIdentityTagline(string selfDescription)
    {
        if (string.IsNullOrWhiteSpace(selfDescription))
            return "";

        var trimmed = selfDescription.Trim();
        var sentenceEnd = trimmed.IndexOfAny(new[] { '.', '!', '?' });
        if (sentenceEnd > 0 && sentenceEnd < 220)
            return trimmed[..(sentenceEnd + 1)].Trim();

        return trimmed.Length <= 220
            ? trimmed
            : trimmed[..220].TrimEnd() + "...";
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

    private static string? BuildMetaToolPingSummary(IList<ToolCallRecord> calls)
    {
        var pingCall = calls
            .LastOrDefault(call => string.Equals(
                call.ToolName,
                "tool_ping",
                StringComparison.OrdinalIgnoreCase));
        if (pingCall is null || string.IsNullOrWhiteSpace(pingCall.Result))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(pingCall.Result);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString()
                : null;
            var toolCount = root.TryGetProperty("tool_count", out var toolCountEl) && toolCountEl.ValueKind == JsonValueKind.Number
                ? toolCountEl.GetInt32()
                : (int?)null;

            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return toolCount is > 0
                    ? $"MCP server is responding and tool execution is healthy with {toolCount.Value} tools available."
                    : "MCP server is responding and tool execution is healthy.";
            }

            return string.IsNullOrWhiteSpace(status)
                ? "MCP server is responding, but the health status was not clearly reported."
                : $"MCP server is responding, but reported status '{status}'.";
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

    private static bool ShouldUseLlmUtilityInference(RouterOutput route, string message)
    {
        var intent = (route.Intent ?? "").Trim();
        if (intent.Length == 0)
            return false;

        // Primary path: broad fallback route.
        if (intent.Equals(Intents.GeneralTool, StringComparison.OrdinalIgnoreCase))
            return true;

        // Secondary path: lookup-routed turns that still read like a
        // utility request (e.g. flexible weather phrasing). This prevents
        // accidental web fallback when deterministic utility matching
        // misses the first pass.
        if (!OrchestratorMessageHelpers.MightBeUtilityIntent(message))
            return false;

        return intent.Equals(Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
               intent.Equals(Intents.LookupSearch, StringComparison.OrdinalIgnoreCase);
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
