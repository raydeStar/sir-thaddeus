using System.Text.Json;

namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Deterministic contract checks for deep-dive payloads.
/// Keeps validation lightweight without introducing a JSON schema runtime dependency.
/// </summary>
public static class DeepDiveBriefingValidator
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        DeepDiveConstants.KindPlace,
        DeepDiveConstants.KindProduct
    };

    private static readonly HashSet<string> AllowedConfidence = new(StringComparer.OrdinalIgnoreCase)
    {
        DeepDiveConstants.ConfidenceHigh,
        DeepDiveConstants.ConfidenceMedium,
        DeepDiveConstants.ConfidenceLow
    };

    public static bool TryValidate(DeepDiveBriefing? briefing, out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        if (briefing is null)
        {
            list.Add("Briefing payload is null.");
            errors = list;
            return false;
        }

        if (briefing.Version != DeepDiveConstants.ContractVersion)
            list.Add($"Unsupported briefing version '{briefing.Version}'.");

        if (briefing.Topic is null)
            list.Add("Topic is required.");
        else
            ValidateTopic(briefing.Topic, list);

        if (briefing.Hero is null)
            list.Add("Hero is required.");
        else
            ValidateHero(briefing.Hero, list);

        if (briefing.Cards is null || briefing.Cards.Count == 0)
        {
            list.Add("At least one card is required.");
        }
        else
        {
            ValidateCards(briefing.Cards, list);
            ValidateCardRulesByKind(briefing.Topic?.Kind ?? "", briefing.Cards, list);
        }

        if (briefing.Audit is not null)
            ValidateAudit(briefing.Audit, list);

        if (briefing.Map is not null)
            ValidateMap(briefing.Map, list);

        errors = list;
        return list.Count == 0;
    }

    public static bool TryParseAndValidateJson(
        string json,
        out DeepDiveBriefing? briefing,
        out IReadOnlyList<string> errors)
    {
        briefing = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            errors = ["JSON payload is empty."];
            return false;
        }

        try
        {
            briefing = JsonSerializer.Deserialize<DeepDiveBriefing>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            errors = [$"Invalid JSON: {ex.Message}"];
            return false;
        }

        return TryValidate(briefing, out errors);
    }

    private static void ValidateTopic(DeepDiveTopic topic, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(topic.Kind))
            errors.Add("Topic.kind is required.");
        else if (!AllowedKinds.Contains(topic.Kind))
            errors.Add($"Topic.kind '{topic.Kind}' is not supported.");

        if (string.IsNullOrWhiteSpace(topic.Query))
            errors.Add("Topic.query is required.");

        if (string.IsNullOrWhiteSpace(topic.Timezone))
            errors.Add("Topic.timezone should be set (use 'unknown' when unavailable).");

        if (string.IsNullOrWhiteSpace(topic.Locale))
            errors.Add("Topic.locale should be set (use 'en-US' fallback when unknown).");
    }

    private static void ValidateHero(DeepDiveHero hero, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(hero.Title))
            errors.Add("Hero.title is required.");

        if (string.IsNullOrWhiteSpace(hero.Confidence))
        {
            errors.Add("Hero.confidence is required.");
        }
        else if (!AllowedConfidence.Contains(hero.Confidence))
        {
            errors.Add($"Hero.confidence '{hero.Confidence}' is invalid.");
        }

        if (!DateTimeOffset.TryParse(hero.LastCheckedIso, out _))
            errors.Add("Hero.last_checked_iso must be a valid ISO timestamp.");
    }

    private static void ValidateCards(IReadOnlyList<DeepDiveCard> cards, List<string> errors)
    {
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (string.IsNullOrWhiteSpace(card.Type))
                errors.Add($"Card[{i}].type is required.");

            if (string.IsNullOrWhiteSpace(card.Title))
                errors.Add($"Card[{i}].title is required.");

            if (card.Bullets is null || card.Bullets.Count == 0)
                errors.Add($"Card[{i}] must contain at least one bullet.");

            if (card.Sources is null || card.Sources.Count == 0)
                errors.Add($"Card[{i}] must contain at least one source.");
            else
                ValidateSources(card.Sources, $"Card[{i}].sources", errors);
        }
    }

    private static void ValidateCardRulesByKind(string kind, IReadOnlyList<DeepDiveCard> cards, List<string> errors)
    {
        if (!kind.Equals(DeepDiveConstants.KindPlace, StringComparison.OrdinalIgnoreCase))
            return;

        if (cards.Count < 3)
            errors.Add("Place briefings must include at least three cards.");

        var types = new HashSet<string>(cards.Select(c => c.Type), StringComparer.OrdinalIgnoreCase);
        foreach (var requiredType in new[] { "hours", "reviews", "summary" })
        {
            if (!types.Contains(requiredType))
                errors.Add($"Place briefings must include a '{requiredType}' card.");
        }
    }

    private static void ValidateAudit(IReadOnlyList<DeepDiveAuditStep> audit, List<string> errors)
    {
        for (var i = 0; i < audit.Count; i++)
        {
            var step = audit[i];
            if (string.IsNullOrWhiteSpace(step.Step))
                errors.Add($"Audit[{i}].step is required.");

            if (string.IsNullOrWhiteSpace(step.Detail))
                errors.Add($"Audit[{i}].detail is required.");

            if (!DateTimeOffset.TryParse(step.TimestampIso, out _))
                errors.Add($"Audit[{i}].timestamp_iso is invalid.");

            if (step.Sources is not null && step.Sources.Count > 0)
                ValidateSources(step.Sources, $"Audit[{i}].sources", errors);
        }
    }

    private static void ValidateMap(DeepDiveMap map, List<string> errors)
    {
        if (map.Latitude is < -90 or > 90)
            errors.Add("Map.latitude out of range.");

        if (map.Longitude is < -180 or > 180)
            errors.Add("Map.longitude out of range.");
    }

    private static void ValidateSources(IReadOnlyList<SourceRef> sources, string path, List<string> errors)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            var source = sources[i];
            if (string.IsNullOrWhiteSpace(source.Name))
                errors.Add($"{path}[{i}].name is required.");
            if (string.IsNullOrWhiteSpace(source.Url))
                errors.Add($"{path}[{i}].url is required.");
            if (!DateTimeOffset.TryParse(source.FetchedIso, out _))
                errors.Add($"{path}[{i}].fetched_iso is invalid.");
        }
    }
}
