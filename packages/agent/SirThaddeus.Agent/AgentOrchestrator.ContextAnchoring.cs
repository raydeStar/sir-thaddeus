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

    private void RememberPlaceContext(
        string placeName,
        string countryCode,
        string? regionCode = null,
        double? latitude = null,
        double? longitude = null,
        bool locationInferred = false,
        bool geocodeMismatch = false,
        bool explicitLocationChange = true)
    {
        if (string.IsNullOrWhiteSpace(placeName))
            return;

        var normalizedName = placeName.Trim();
        var normalizedCountryCode = string.IsNullOrWhiteSpace(countryCode)
            ? ""
            : countryCode.Trim().ToUpperInvariant();
        var normalizedRegionCode = string.IsNullOrWhiteSpace(regionCode)
            ? null
            : regionCode.Trim().ToUpperInvariant();

        var now = _timeProvider.GetUtcNow();

        var current = _dialogueStore.Get();
        if (!current.ContextLocked || explicitLocationChange)
        {
            _dialogueStore.Update(current with
            {
                Topic = string.IsNullOrWhiteSpace(current.Topic) ? "location" : current.Topic,
                LocationName = normalizedName,
                CountryCode = string.IsNullOrWhiteSpace(normalizedCountryCode) ? null : normalizedCountryCode,
                RegionCode = normalizedRegionCode,
                Latitude = latitude ?? current.Latitude,
                Longitude = longitude ?? current.Longitude,
                LocationInferred = locationInferred,
                GeocodeMismatch = geocodeMismatch
            });
        }

        _lastPlaceContextName = normalizedName;
        _lastPlaceContextCountryCode = normalizedCountryCode;
        _lastPlaceContextAt = now;

        // Also mirror into the search session so entity-aware query building
        // can reuse place context on short follow-up turns.
        _searchOrchestrator.Session.LastEntityCanonical = normalizedName;
        _searchOrchestrator.Session.LastEntityType = "Place";
        _searchOrchestrator.Session.LastEntityDisambiguation =
            string.IsNullOrWhiteSpace(normalizedCountryCode) ? "Place" : normalizedCountryCode;
        _searchOrchestrator.Session.UpdatedAt = now;
    }

    private bool TryGetActivePlaceContext(out string placeName)
    {
        placeName = "";
        var state = _dialogueStore.Get();
        if (!string.IsNullOrWhiteSpace(state.LocationName))
        {
            placeName = state.LocationName!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_lastPlaceContextName))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastPlaceContextAt) > PlaceContextTtl)
            return false;

        placeName = _lastPlaceContextName!;
        return true;
    }

    private string ApplyPlaceContextIfHelpful(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return userMessage;

        if (!TryGetActivePlaceContext(out var place))
            return userMessage;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

        // Do not inject when the user already scoped to a topic/location.
        if (HasExplicitNonTemporalScope(lower))
            return userMessage;

        var weatherFollowUp = LooksLikeWeatherFollowUp(lower);
        var genericNewsFollowUp = SearchModeRouter.LooksLikeNewsIntent(lower);
        if (!weatherFollowUp && !genericNewsFollowUp)
            return userMessage;

        if (weatherFollowUp)
        {
            if (LooksLikeWeatherActivityAdviceRequest(lower))
                return $"{trimmed.TrimEnd('?', '.', '!')} in {place}";

            return $"weather in {place}";
        }

        return $"{trimmed.TrimEnd('?', '.', '!')} in {place}";
    }

    private static bool LooksLikeWeatherFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        // "forecast" alone can be non-weather ("stock forecast"), so guard.
        if (lowerMessage.Contains("stock forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("earnings forecast", StringComparison.Ordinal))
            return false;

        return lowerMessage.Contains("weather", StringComparison.Ordinal) ||
               lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
               lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
               lowerMessage.Contains("temp", StringComparison.Ordinal) ||
               lowerMessage.Contains("humidity", StringComparison.Ordinal) ||
               lowerMessage.Contains("rain", StringComparison.Ordinal) ||
               lowerMessage.Contains("snow", StringComparison.Ordinal);
    }

    private static bool LooksLikeWeatherActivityAdviceRequest(string message) =>
        Utilities.WeatherResponseBuilder.LooksLikeActivityAdviceRequest(message);

    private static bool HasExplicitNonTemporalScope(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        var match = ContextScopeRegex().Match(lowerMessage);
        if (!match.Success)
            return false;

        var scope = match.Groups["scope"].Value
            .Trim()
            .TrimEnd('?', '.', '!', ',');

        if (string.IsNullOrWhiteSpace(scope))
            return false;

        var scopeLower = scope.ToLowerInvariant();
        if (scopeLower.Contains("this weather", StringComparison.Ordinal) ||
            scopeLower.Contains("that weather", StringComparison.Ordinal) ||
            scopeLower.Contains("this kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("that kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("kind of weather", StringComparison.Ordinal) ||
            scopeLower.Contains("current weather", StringComparison.Ordinal) ||
            scopeLower.Contains("these conditions", StringComparison.Ordinal) ||
            scopeLower.Contains("those conditions", StringComparison.Ordinal))
        {
            return false;
        }

        return !TemporalScopeRegex().IsMatch(scope);
    }

    [GeneratedRegex(
        @"\b(?:in|for|at|near|about|on|regarding)\s+(?<scope>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ContextScopeRegex();

    [GeneratedRegex(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex TemporalScopeRegex();
}
