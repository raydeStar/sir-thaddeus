using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Dialogue;

public sealed record MergedSlots
{
    public string Intent { get; init; } = "none";
    public string? Topic { get; init; }
    public string? LocationText { get; init; }
    public string? CountryCode { get; init; }
    public string? RegionCode { get; init; }
    public string? TimeScope { get; init; }
    public bool LocationInferredFromState { get; init; }
    public bool ExplicitLocationChange { get; init; }

    public string RawMessage { get; init; } = "";
}

/// <summary>
/// Deterministic carry-forward merge of newly extracted slots with current state.
/// </summary>
public sealed class MergeSlots
{
    private static readonly Regex TemporalOnlyRegex = new(
        @"^(?:for\s+)?(?:today|tomorrow|tonight|now|right now|currently|this\s+(?:morning|afternoon|evening|week|weekend)|last\s+(?:week|month)|next\s+week|yesterday)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FollowUpCueRegex = new(
        @"^(?:anything\s+else|what\s+else|else|more|and\s+what\s+else|and\??)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MergedSlots Run(
        DialogueState currentState,
        ExtractedSlots extracted,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        ArgumentNullException.ThrowIfNull(extracted);

        var intent = NormalizeIntent(extracted.Intent);
        var hasTemporalCue = TemporalOnlyRegex.IsMatch(extracted.RawMessage.Trim());
        var hasFollowUpCue = FollowUpCueRegex.IsMatch(extracted.RawMessage.Trim().TrimEnd('?', '!', '.'));

        if (string.Equals(intent, "none", StringComparison.Ordinal))
        {
            var priorIntent = NormalizeIntent(currentState.Topic);
            if (IsCarryForwardIntent(priorIntent) &&
                (extracted.RefersToPriorLocation ||
                 hasTemporalCue ||
                 hasFollowUpCue ||
                 !string.IsNullOrWhiteSpace(extracted.TimeScope)))
            {
                intent = priorIntent;
            }
        }

        var location = NormalizeLocation(extracted.LocationText);
        var priorLocation = NormalizeLocation(currentState.LocationName);
        var inferred = false;

        var shouldCarryLocation =
            string.IsNullOrWhiteSpace(location) &&
            !string.IsNullOrWhiteSpace(priorLocation) &&
            (extracted.RefersToPriorLocation ||
             NeedsLocationForIntent(intent) ||
             hasTemporalCue ||
             hasFollowUpCue);

        if (shouldCarryLocation)
        {
            location = priorLocation;
            inferred = true;
        }

        var topic = Normalize(extracted.Topic)
            ?? Normalize(currentState.Topic)
            ?? "";

        var timeScope = Normalize(extracted.TimeScope);

        return new MergedSlots
        {
            Intent = intent,
            Topic = topic,
            LocationText = location,
            CountryCode = inferred ? currentState.CountryCode : null,
            RegionCode = inferred ? currentState.RegionCode : null,
            TimeScope = timeScope,
            LocationInferredFromState = inferred,
            ExplicitLocationChange = extracted.ExplicitLocationChange,
            RawMessage = extracted.RawMessage
        };
    }

    private static bool NeedsLocationForIntent(string intent) => intent is
        "weather" or "time" or "news" or "search";

    private static bool IsCarryForwardIntent(string intent) => intent is
        "weather" or "time" or "news" or "search";

    private static string NormalizeIntent(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            return "none";

        return intent.Trim().ToLowerInvariant();
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;

        var value = location.Trim().TrimEnd('.', ',', '!', '?');
        if (TemporalOnlyRegex.IsMatch(value))
            return null;
        if (IsNoneLikeLiteral(value))
            return null;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return value.Trim();
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
