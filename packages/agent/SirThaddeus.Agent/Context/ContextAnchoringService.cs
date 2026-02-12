using System.Text.RegularExpressions;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Context;

/// <summary>
/// Anchors conversational context using typed context patches.
/// </summary>
public sealed partial class ContextAnchoringService : IContextAnchoringService
{
    private readonly IDialogueStateStore _dialogueStore;
    private readonly SearchOrchestrator _searchOrchestrator;
    private readonly TimeProvider _timeProvider;

    private readonly TimeSpan _placeContextTtl = TimeSpan.FromMinutes(30);
    private readonly TimeSpan _utilityContextTtl = TimeSpan.FromMinutes(20);

    private string? _lastPlaceContextName;
    private DateTimeOffset _lastPlaceContextAt;
    private string? _lastUtilityContextKey;
    private DateTimeOffset _lastUtilityContextAt;

    public ContextAnchoringService(
        IDialogueStateStore dialogueStore,
        SearchOrchestrator searchOrchestrator,
        TimeProvider? timeProvider = null)
    {
        _dialogueStore = dialogueStore ?? throw new ArgumentNullException(nameof(dialogueStore));
        _searchOrchestrator = searchOrchestrator ?? throw new ArgumentNullException(nameof(searchOrchestrator));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public UtilityContextPatch? TryBuildUtilityPatch(UtilityRouter.UtilityResult utilityResult)
    {
        if (utilityResult is null || string.IsNullOrWhiteSpace(utilityResult.ContextKey))
            return null;

        return new UtilityContextPatch(utilityResult.ContextKey.Trim());
    }

    public PlaceContextPatch CreatePlacePatch(
        string placeName,
        string countryCode,
        string? regionCode = null,
        double? latitude = null,
        double? longitude = null,
        bool locationInferred = false,
        bool geocodeMismatch = false,
        bool explicitLocationChange = true)
        => new(
            placeName,
            countryCode,
            regionCode,
            latitude,
            longitude,
            locationInferred,
            geocodeMismatch,
            explicitLocationChange);

    public void ApplyPatch(ContextPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        switch (patch)
        {
            case UtilityContextPatch utilityPatch:
                ApplyUtilityPatch(utilityPatch);
                break;
            case PlaceContextPatch placePatch:
                ApplyPlacePatch(placePatch);
                break;
        }
    }

    public UtilityRouter.UtilityResult? TryHandleUtilityFollowUpWithContext(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        if (!TryGetActiveUtilityContext(out var contextKey))
            return null;

        var lower = userMessage.Trim().ToLowerInvariant();
        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            TryResolveMoonFollowUpUnit(lower, out var requestedUnit))
        {
            return BuildMoonUnitFollowUpResult(requestedUnit);
        }

        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            LooksLikePrecisionFollowUp(lower))
        {
            return BuildMoonPrecisionFollowUpResult(lower);
        }

        return null;
    }

    public string ApplyPlaceContextIfHelpful(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return userMessage;

        if (!TryGetActivePlaceContext(out var place))
            return userMessage;

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();

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

    public AgentResponse AddLocationInferenceDisclosure(
        AgentResponse response,
        ValidatedSlots? validatedSlots)
    {
        if (validatedSlots is null ||
            !validatedSlots.LocationInferred ||
            string.IsNullOrWhiteSpace(validatedSlots.LocationText) ||
            LocationContextHeuristics.IsClearlyNonPlace(validatedSlots.LocationText))
        {
            return response;
        }

        var note = $"Using your previous location context (**{validatedSlots.LocationText}**).";
        if (response.Text.Contains(note, StringComparison.OrdinalIgnoreCase))
            return response;

        return response with { Text = $"{note}\n\n{response.Text}" };
    }

    private void ApplyUtilityPatch(UtilityContextPatch patch)
    {
        _lastUtilityContextKey = patch.ContextKey.Trim();
        _lastUtilityContextAt = _timeProvider.GetUtcNow();

        var state = _dialogueStore.Get();
        _dialogueStore.Update(state with { Topic = patch.ContextKey.Trim() });
    }

    private void ApplyPlacePatch(PlaceContextPatch patch)
    {
        if (string.IsNullOrWhiteSpace(patch.PlaceName))
            return;

        var normalizedName = patch.PlaceName.Trim();
        var normalizedCountryCode = string.IsNullOrWhiteSpace(patch.CountryCode)
            ? ""
            : patch.CountryCode.Trim().ToUpperInvariant();
        var normalizedRegionCode = string.IsNullOrWhiteSpace(patch.RegionCode)
            ? null
            : patch.RegionCode.Trim().ToUpperInvariant();

        var now = _timeProvider.GetUtcNow();
        var current = _dialogueStore.Get();

        if (!current.ContextLocked || patch.ExplicitLocationChange)
        {
            _dialogueStore.Update(current with
            {
                Topic = string.IsNullOrWhiteSpace(current.Topic) ? "location" : current.Topic,
                LocationName = normalizedName,
                CountryCode = string.IsNullOrWhiteSpace(normalizedCountryCode) ? null : normalizedCountryCode,
                RegionCode = normalizedRegionCode,
                Latitude = patch.Latitude ?? current.Latitude,
                Longitude = patch.Longitude ?? current.Longitude,
                LocationInferred = patch.LocationInferred,
                GeocodeMismatch = patch.GeocodeMismatch
            });
        }

        _lastPlaceContextName = normalizedName;
        _lastPlaceContextAt = now;

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
            if (LocationContextHeuristics.IsClearlyNonPlace(state.LocationName))
            {
                _dialogueStore.Update(state with
                {
                    LocationName = null,
                    CountryCode = null,
                    RegionCode = null,
                    Latitude = null,
                    Longitude = null,
                    LocationInferred = false
                });
                return false;
            }

            placeName = state.LocationName!;
            return true;
        }

        if (string.IsNullOrWhiteSpace(_lastPlaceContextName))
            return false;

        if (LocationContextHeuristics.IsClearlyNonPlace(_lastPlaceContextName))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastPlaceContextAt) > _placeContextTtl)
            return false;

        placeName = _lastPlaceContextName!;
        return true;
    }

    private bool TryGetActiveUtilityContext(out string contextKey)
    {
        contextKey = "";
        if (string.IsNullOrWhiteSpace(_lastUtilityContextKey))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastUtilityContextAt) > _utilityContextTtl)
            return false;

        contextKey = _lastUtilityContextKey!;
        return true;
    }

    private static bool LooksLikePrecisionFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        return lowerMessage.Contains("more precise", StringComparison.Ordinal) ||
               lowerMessage.Contains("precise figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("more exact", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact value", StringComparison.Ordinal) ||
               lowerMessage.Contains("higher precision", StringComparison.Ordinal) ||
               lowerMessage.Contains("more accurate", StringComparison.Ordinal) ||
               lowerMessage.Contains("to the decimal", StringComparison.Ordinal) ||
               lowerMessage.Contains("more digits", StringComparison.Ordinal) ||
               lowerMessage.Contains("significant digit", StringComparison.Ordinal) ||
               lowerMessage.Contains("more detail", StringComparison.Ordinal) ||
               lowerMessage.Equals("i need a more precise figure!", StringComparison.Ordinal) ||
               lowerMessage.Equals("more precise", StringComparison.Ordinal) ||
               lowerMessage.Equals("exactly", StringComparison.Ordinal);
    }

    private static bool TryResolveMoonFollowUpUnit(string lowerMessage, out string unit)
    {
        unit = "";
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        var tokens = lowerMessage
            .Split(
                [' ', '\t', '\r', '\n', '?', '!', ',', '.', ';', ':', '(', ')', '/', '\\', '-'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool ContainsAnyToken(string[] source, params string[] candidates)
        {
            foreach (var token in source)
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (token.Equals(candidates[i], StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        var referencesPreviousValue = ContainsAnyToken(tokens, "that", "it", "distance", "moon");
        if (!referencesPreviousValue)
            return false;

        if (ContainsAnyToken(tokens, "feet", "foot", "ft"))
        {
            unit = "feet";
            return true;
        }

        if (ContainsAnyToken(tokens, "mile", "miles", "mi"))
        {
            unit = "miles";
            return true;
        }

        if (ContainsAnyToken(tokens, "kilometer", "kilometers", "km"))
        {
            unit = "kilometers";
            return true;
        }

        if (ContainsAnyToken(tokens, "meter", "meters", "m"))
        {
            unit = "meters";
            return true;
        }

        return false;
    }

    private static UtilityRouter.UtilityResult BuildMoonPrecisionFollowUpResult(string lowerMessage)
    {
        const double averageKm = 384_400.0;
        const double perigeeKm = 363_300.0;
        const double apogeeKm = 405_500.0;
        const double kmToMiles = 0.621371;

        var averageMiles = averageKm * kmToMiles;
        var perigeeMiles = perigeeKm * kmToMiles;
        var apogeeMiles = apogeeKm * kmToMiles;

        string answer;
        if (lowerMessage.Contains("mile", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageMiles:N1} miles**. " +
                $"Because the orbit is elliptical, it ranges from about **{perigeeMiles:N0} miles** " +
                $"(perigee) to **{apogeeMiles:N0} miles** (apogee).";
        }
        else if (lowerMessage.Contains("km", StringComparison.Ordinal) ||
                 lowerMessage.Contains("kilometer", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km**. " +
                $"It ranges from about **{perigeeKm:N0} km** (perigee) to **{apogeeKm:N0} km** (apogee).";
        }
        else
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km** " +
                $"(**{averageMiles:N1} miles**). The orbit varies between about **{perigeeKm:N0} km** " +
                $"(**{perigeeMiles:N0} miles**) and **{apogeeKm:N0} km** (**{apogeeMiles:N0} miles**).";
        }

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static UtilityRouter.UtilityResult BuildMoonUnitFollowUpResult(string unit)
    {
        const double averageKm = 384_400.0;
        const double kmToMiles = 0.621371;
        const double milesToFeet = 5_280.0;

        var averageMilesRounded = Math.Round(averageKm * kmToMiles);
        var averageFeetFromMiles = averageMilesRounded * milesToFeet;
        var averageMeters = averageKm * 1_000.0;

        var normalizedUnit = unit.Trim().ToLowerInvariant();
        var answer = normalizedUnit switch
        {
            "feet" =>
                $"That is about **{averageFeetFromMiles:N0} feet** " +
                $"(using **{averageMilesRounded:N0} miles * 5,280 ft/mile**, average Earth-Moon distance).",
            "meters" =>
                $"That is about **{averageMeters:N0} meters** (average Earth-Moon distance).",
            "miles" =>
                $"That is about **{averageMilesRounded:N0} miles** (average Earth-Moon distance).",
            _ =>
                $"That is about **{averageKm:N0} kilometers** (average Earth-Moon distance)."
        };

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static bool LooksLikeWeatherFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        if (lowerMessage.Contains("stock forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("earnings forecast", StringComparison.Ordinal))
        {
            return false;
        }

        return lowerMessage.Contains("weather", StringComparison.Ordinal) ||
               lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
               lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
               lowerMessage.Contains("temp", StringComparison.Ordinal) ||
               lowerMessage.Contains("humidity", StringComparison.Ordinal) ||
               lowerMessage.Contains("rain", StringComparison.Ordinal) ||
               lowerMessage.Contains("snow", StringComparison.Ordinal);
    }

    private static bool LooksLikeWeatherActivityAdviceRequest(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lowerMessage = message.ToLowerInvariant();

        var hasWeatherCue =
            lowerMessage.Contains("weather", StringComparison.Ordinal) ||
            lowerMessage.Contains("forecast", StringComparison.Ordinal) ||
            lowerMessage.Contains("temperature", StringComparison.Ordinal) ||
            lowerMessage.Contains("temp", StringComparison.Ordinal) ||
            lowerMessage.Contains("rain", StringComparison.Ordinal) ||
            lowerMessage.Contains("snow", StringComparison.Ordinal);

        if (!hasWeatherCue)
            return false;

        return lowerMessage.Contains("activity", StringComparison.Ordinal) ||
               lowerMessage.Contains("activities", StringComparison.Ordinal) ||
               lowerMessage.Contains("what can i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("could i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("what should i do", StringComparison.Ordinal) ||
               lowerMessage.Contains("kind of things", StringComparison.Ordinal) ||
               lowerMessage.Contains("things to do", StringComparison.Ordinal) ||
               lowerMessage.Contains("ideas", StringComparison.Ordinal) ||
               lowerMessage.Contains("recommend", StringComparison.Ordinal) ||
               lowerMessage.Contains("suggest", StringComparison.Ordinal);
    }

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
