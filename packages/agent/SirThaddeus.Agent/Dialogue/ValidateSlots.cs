using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Dialogue;

public sealed record ValidationOptions
{
    public string GeocodeMismatchMode { get; init; } = "fallback_previous";
}

public sealed record ValidatedSlots
{
    public string Intent { get; init; } = "none";
    public string? Topic { get; init; }
    public string? LocationText { get; init; }
    public string? CountryCode { get; init; }
    public string? RegionCode { get; init; }
    public string? TimeScope { get; init; }
    public bool LocationInferred { get; init; }
    public bool GeocodeMismatch { get; init; }
    public bool RequiresLocationConfirmation { get; init; }
    public string? MismatchWarning { get; init; }

    public bool ExplicitLocationChange { get; init; }
    public string NormalizedMessage { get; init; } = "";
}

/// <summary>
/// Hard validation/gating for merged slots before any tool planning.
/// </summary>
public sealed class ValidateSlots
{
    private readonly ValidationOptions _options;

    private static readonly Regex TemporalOnlyLocationPattern = new(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocationTemporalTailPattern = new(
        @"\s+(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ValidateSlots(ValidationOptions options)
    {
        _options = options ?? new ValidationOptions();
    }

    public ValidatedSlots Run(
        DialogueState currentState,
        MergedSlots merged)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(merged);

        var intent = NormalizeIntent(merged.Intent);
        var topic = Normalize(merged.Topic);
        var location = Normalize(merged.LocationText);
        var timeScope = Normalize(merged.TimeScope);

        if (!string.IsNullOrWhiteSpace(location))
        {
            location = LocationTemporalTailPattern.Replace(location, "").Trim();
            if (TemporalOnlyLocationPattern.IsMatch(location))
                location = null;
            else if (IsNoneLikeLiteral(location))
                location = null;
        }

        var normalizedMessage = BuildNormalizedMessage(merged.RawMessage, intent, location, merged.LocationInferredFromState);
        return new ValidatedSlots
        {
            Intent = intent,
            Topic = topic,
            LocationText = location,
            CountryCode = NormalizeUpper(merged.CountryCode),
            RegionCode = NormalizeUpper(merged.RegionCode),
            TimeScope = timeScope,
            LocationInferred = merged.LocationInferredFromState,
            GeocodeMismatch = false,
            RequiresLocationConfirmation = false,
            MismatchWarning = null,
            ExplicitLocationChange = merged.ExplicitLocationChange,
            NormalizedMessage = normalizedMessage
        };
    }

    public bool ShouldRequireConfirm() =>
        string.Equals(_options.GeocodeMismatchMode, "require_confirm", StringComparison.OrdinalIgnoreCase);

    public static bool IsStronglyDivergent(
        DialogueState currentState,
        string? candidateCountryCode,
        string? candidateRegionCode,
        double? candidateLatitude,
        double? candidateLongitude,
        out string reason)
    {
        reason = "";
        if (currentState is null || string.IsNullOrWhiteSpace(currentState.LocationName))
            return false;

        var currentCountry = NormalizeUpper(currentState.CountryCode);
        var nextCountry = NormalizeUpper(candidateCountryCode);

        if (!string.IsNullOrWhiteSpace(currentCountry) &&
            !string.IsNullOrWhiteSpace(nextCountry) &&
            !string.Equals(currentCountry, nextCountry, StringComparison.OrdinalIgnoreCase))
        {
            reason = "country_mismatch";
            return true;
        }

        var currentRegion = NormalizeUpper(currentState.RegionCode);
        var nextRegion = NormalizeUpper(candidateRegionCode);
        if (!string.IsNullOrWhiteSpace(currentRegion) &&
            !string.IsNullOrWhiteSpace(nextRegion) &&
            !string.Equals(currentRegion, nextRegion, StringComparison.OrdinalIgnoreCase))
        {
            reason = "region_mismatch";
            return true;
        }

        if (currentState.Latitude.HasValue && currentState.Longitude.HasValue &&
            candidateLatitude.HasValue && candidateLongitude.HasValue)
        {
            var km = HaversineKm(
                currentState.Latitude.Value,
                currentState.Longitude.Value,
                candidateLatitude.Value,
                candidateLongitude.Value);
            if (km >= 250)
            {
                reason = "distance_mismatch";
                return true;
            }
        }

        return false;
    }

    private static string BuildNormalizedMessage(
        string rawMessage,
        string intent,
        string? location,
        bool locationInferred)
    {
        var trimmed = (rawMessage ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return "";

        if (string.IsNullOrWhiteSpace(location) || !locationInferred)
            return trimmed;

        // Carry-forward injection for underspecified follow-ups.
        return intent switch
        {
            "weather" => $"weather in {location}",
            "time" => $"time in {location}",
            "news" or "search" => $"{trimmed.TrimEnd('?', '.', '!')} in {location}",
            _ => trimmed
        };
    }

    private static string NormalizeIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return "none";
        return intent.Trim().ToLowerInvariant();
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        return IsNoneLikeLiteral(trimmed) ? null : trimmed;
    }

    private static string? NormalizeUpper(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim().ToUpperInvariant();
    }

    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double radiusKm = 6371.0;
        static double ToRad(double d) => d * Math.PI / 180.0;

        var dLat = ToRad(lat2 - lat1);
        var dLon = ToRad(lon2 - lon1);
        var a =
            Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
            Math.Cos(ToRad(lat1)) * Math.Cos(ToRad(lat2)) *
            Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return radiusKm * c;
    }

    private static bool IsNoneLikeLiteral(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("null", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("na", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("(none)", StringComparison.OrdinalIgnoreCase);
    }
}
