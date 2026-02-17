namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Stable card ordering rules for deep-dive briefings.
/// </summary>
public static class DeepDiveCardOrdering
{
    public static IReadOnlyList<DeepDiveCard> Apply(
        string topicKind,
        IReadOnlyList<DeepDiveCard> cards)
    {
        if (cards.Count <= 1)
            return cards;

        return topicKind.Equals(DeepDiveConstants.KindPlace, StringComparison.OrdinalIgnoreCase)
            ? OrderPlace(cards)
            : OrderProduct(cards);
    }

    private static IReadOnlyList<DeepDiveCard> OrderPlace(IReadOnlyList<DeepDiveCard> cards)
    {
        // Warnings float near the top so ambiguity is visible immediately.
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["warnings"] = 0,
            ["hours"] = 1,
            ["reviews"] = 2,
            ["summary"] = 3,
            ["links"] = 4,
            ["alternatives"] = 5
        };

        return cards
            .Select((card, index) => (card, index))
            .OrderBy(x => rank.TryGetValue(x.card.Type, out var value) ? value : 99)
            .ThenBy(x => x.index)
            .Select(x => x.card)
            .ToList();
    }

    private static IReadOnlyList<DeepDiveCard> OrderProduct(IReadOnlyList<DeepDiveCard> cards)
    {
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["listings"] = 0,
            ["price_bands"] = 1,
            ["reviews"] = 2,
            ["gotchas"] = 3,
            ["links"] = 4,
            ["warnings"] = 5
        };

        return cards
            .Select((card, index) => (card, index))
            .OrderBy(x => rank.TryGetValue(x.card.Type, out var value) ? value : 99)
            .ThenBy(x => x.index)
            .Select(x => x.card)
            .ToList();
    }
}
