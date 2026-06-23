using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Search;

internal static class EnumerableSetCounter
{
    private static readonly Regex InquiryPattern = new(
        @"\b(?:how\s+many|which|what|count|list|enumerate|extrapolate|expand)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExpansionIntentPattern = new(
        @"\b(?:extrapolate|expand|enumerate|list)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ContainsLetterPattern = new(
        @"\b(?:have|has|contain|contains|include|includes)\s+(?:the\s+)?(?:letter\s+)?[""'`“”]?(?<letter>[a-z])[""'`“”]?(?:\s+(?:in|inside)\s+(?:them|their\s+names?))?\b|\bwith\s+(?:the\s+)?letter\s+[""'`“”]?(?<letter2>[a-z])[""'`“”]?\b|\bletter\s+[""'`“”]?(?<letter3>[a-z])[""'`“”]?(?:\s+(?:in|inside)\s+(?:them|their\s+names?))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StartsWithLetterPattern = new(
        @"\b(?:start|starts|begin|begins)\s+with\s+(?:the\s+)?(?:letter\s+)?[""'`“”]?(?<letter>[a-z])[""'`“”]?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EndsWithLetterPattern = new(
        @"\b(?:end|ends)\s+with\s+(?:the\s+)?(?:letter\s+)?[""'`“”]?(?<letter>[a-z])[""'`“”]?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly KnownCollection[] KnownCollections =
    [
        new(
            DisplayName: "weekdays",
            ItemSingular: "weekday",
            ItemPlural: "weekdays",
            Scope: CollectionScope.Closed,
            Aliases:
            [
                new Regex(@"\bweekdays\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bweek\s+days\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]),

        new(
            DisplayName: "weekend days",
            ItemSingular: "weekend day",
            ItemPlural: "weekend days",
            Scope: CollectionScope.Closed,
            Aliases:
            [
                new Regex(@"\bweekend\s+days\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items: ["Saturday", "Sunday"]),

        new(
            DisplayName: "days of the week",
            ItemSingular: "day",
            ItemPlural: "days",
            Scope: CollectionScope.Closed,
            Aliases:
            [
                new Regex(@"\bdays?\s+of\s+the\s+week\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bdays?\s+in\s+the\s+week\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bweek\s+days?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items: ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]),

        new(
            DisplayName: "months of the year",
            ItemSingular: "month",
            ItemPlural: "months",
            Scope: CollectionScope.Closed,
            Aliases:
            [
                new Regex(@"\bmonths?\s+of\s+the\s+year\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bmonths?\s+in\s+the\s+year\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items:
            [
                "January", "February", "March", "April", "May", "June",
                "July", "August", "September", "October", "November", "December"
            ]),

        new(
            DisplayName: "kitchenware",
            ItemSingular: "kitchenware item",
            ItemPlural: "kitchenware items",
            Scope: CollectionScope.Representative,
            Aliases:
            [
                new Regex(@"\bkitchenware\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bkitchen\s+(?:items|tools|utensils|ware)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items:
            [
                "spoon", "fork", "knife", "plate", "bowl", "cup", "mug", "glass",
                "pot", "pan", "skillet", "saucepan", "baking sheet", "cutting board",
                "colander", "whisk", "spatula", "ladle", "tongs", "peeler", "grater",
                "measuring cup", "measuring spoon", "can opener", "mixing bowl", "strainer",
                "rolling pin", "kettle", "teapot", "blender"
            ]),

        new(
            DisplayName: "car parts",
            ItemSingular: "car part",
            ItemPlural: "car parts",
            Scope: CollectionScope.Representative,
            Aliases:
            [
                new Regex(@"\bcar\s+parts\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bauto(?:motive)?\s+parts\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bvehicle\s+parts\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items:
            [
                "engine", "transmission", "battery", "alternator", "radiator",
                "starter motor", "fuel pump", "spark plug", "brake pad", "brake rotor",
                "tire", "wheel", "axle", "suspension spring", "shock absorber",
                "steering wheel", "windshield", "headlight", "tail light", "bumper",
                "exhaust pipe", "muffler", "catalytic converter", "air filter", "oil filter",
                "belt", "hose", "door", "hood", "trunk"
            ]),

        new(
            DisplayName: "computer parts",
            ItemSingular: "computer part",
            ItemPlural: "computer parts",
            Scope: CollectionScope.Representative,
            Aliases:
            [
                new Regex(@"\bcomputer\s+parts\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bpc\s+parts\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bcomputer\s+components\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
                new Regex(@"\bpc\s+components\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
            ],
            Items:
            [
                "processor", "motherboard", "memory", "graphics card", "power supply",
                "storage drive", "solid-state drive", "hard drive", "case", "cooling fan",
                "heat sink", "power cable", "monitor", "keyboard", "mouse", "network card",
                "sound card", "USB port", "display cable", "battery", "touchpad", "screen"
            ])
    ];

    public static DeterministicUtilityResult? TryMatch(string message)
    {
        var result = TryEvaluate(message);
        if (result is null)
            return null;

        return new DeterministicUtilityResult
        {
            Category = result.Category,
            Answer = result.Answer
        };
    }

    public static UtilityRouter.UtilityResult? TryHandle(string message)
    {
        var result = TryEvaluate(message);
        if (result is null)
            return null;

        return new UtilityRouter.UtilityResult
        {
            Category = result.Category,
            Answer = result.Answer
        };
    }

    private static EvaluationResult? TryEvaluate(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || !InquiryPattern.IsMatch(message))
            return null;

        var collection = ResolveCollection(message);
        if (collection is null)
            return null;

        var criterion = ResolveCriterion(message);
        if (criterion is null)
        {
            if (!ExpansionIntentPattern.IsMatch(message))
                return null;

            return BuildExpansionResult(collection);
        }

        var matches = collection.Items
            .Where(criterion.Matches)
            .ToArray();

        return BuildFilteredResult(collection, criterion, matches);
    }

    private static EvaluationResult BuildExpansionResult(KnownCollection collection)
    {
        var answer = collection.Scope switch
        {
            CollectionScope.Closed =>
                $"The canonical set of {collection.DisplayName} contains **{collection.Items.Count}** {collection.ItemPlural}: {FormatList(collection.Items)}.",
            _ =>
                $"Using a representative common set for {collection.DisplayName}, I get **{collection.Items.Count}** {collection.ItemPlural}: {FormatList(collection.Items)}. This is not exhaustive."
        };

        return new EvaluationResult("enumeration", answer);
    }

    private static EvaluationResult BuildFilteredResult(
        KnownCollection collection,
        LetterCriterion criterion,
        IReadOnlyList<string> matches)
    {
        var itemLabel = matches.Count == 1 ? collection.ItemSingular : collection.ItemPlural;
        var answer = collection.Scope switch
        {
            CollectionScope.Closed when matches.Count == 0 =>
                $"There are **0** {collection.DisplayName} whose names {criterion.Description}.",
            CollectionScope.Closed =>
                $"There {(matches.Count == 1 ? "is" : "are")} **{matches.Count}** {itemLabel} in {collection.DisplayName} whose names {criterion.Description}: {FormatList(matches)}.",
            _ when matches.Count == 0 =>
                $"Using a representative common set for {collection.DisplayName}, there are **0** {itemLabel} whose names {criterion.Description}. This is not exhaustive.",
            _ =>
                $"Using a representative common set for {collection.DisplayName}, there {(matches.Count == 1 ? "is" : "are")} **{matches.Count}** {itemLabel} whose names {criterion.Description}: {FormatList(matches)}. This is not exhaustive."
        };

        return new EvaluationResult("enumeration_count", answer);
    }

    private static KnownCollection? ResolveCollection(string message)
    {
        foreach (var collection in KnownCollections)
        {
            if (collection.Aliases.Any(alias => alias.IsMatch(message)))
                return collection;
        }

        return null;
    }

    private static LetterCriterion? ResolveCriterion(string message)
    {
        var startsWithMatch = StartsWithLetterPattern.Match(message);
        if (startsWithMatch.Success && TryReadLetter(startsWithMatch, out var startLetter))
        {
            return new LetterCriterion(
                $"start with the letter '{char.ToLowerInvariant(startLetter)}'",
                value => StartsWithLetter(value, startLetter));
        }

        var endsWithMatch = EndsWithLetterPattern.Match(message);
        if (endsWithMatch.Success && TryReadLetter(endsWithMatch, out var endLetter))
        {
            return new LetterCriterion(
                $"end with the letter '{char.ToLowerInvariant(endLetter)}'",
                value => EndsWithLetter(value, endLetter));
        }

        var containsMatch = ContainsLetterPattern.Match(message);
        if (containsMatch.Success && TryReadLetter(containsMatch, out var containsLetter))
        {
            return new LetterCriterion(
                $"contain the letter '{char.ToLowerInvariant(containsLetter)}'",
                value => ContainsLetter(value, containsLetter));
        }

        return null;
    }

    private static bool TryReadLetter(Match match, out char letter)
    {
        var value = match.Groups["letter"].Success
            ? match.Groups["letter"].Value
            : match.Groups["letter2"].Success
                ? match.Groups["letter2"].Value
                : match.Groups["letter3"].Value;

        if (string.IsNullOrWhiteSpace(value))
        {
            letter = '\0';
            return false;
        }

        letter = char.ToLowerInvariant(value[0]);
        return letter is >= 'a' and <= 'z';
    }

    private static bool ContainsLetter(string value, char letter)
        => value.Any(character => char.ToLowerInvariant(character) == char.ToLowerInvariant(letter));

    private static bool StartsWithLetter(string value, char letter)
        => !string.IsNullOrEmpty(value) && char.ToLowerInvariant(value[0]) == char.ToLowerInvariant(letter);

    private static bool EndsWithLetter(string value, char letter)
        => !string.IsNullOrEmpty(value) && char.ToLowerInvariant(value[^1]) == char.ToLowerInvariant(letter);

    private static string FormatList(IReadOnlyList<string> items)
    {
        return items.Count switch
        {
            0 => "none",
            1 => items[0],
            2 => $"{items[0]} and {items[1]}",
            _ => string.Join(", ", items.Take(items.Count - 1)) + $", and {items[^1]}"
        };
    }

    private sealed record KnownCollection(
        string DisplayName,
        string ItemSingular,
        string ItemPlural,
        CollectionScope Scope,
        IReadOnlyList<Regex> Aliases,
        IReadOnlyList<string> Items);

    private sealed record LetterCriterion(string Description, Func<string, bool> Matches);

    private sealed record EvaluationResult(string Category, string Answer);

    private enum CollectionScope
    {
        Closed,
        Representative
    }
}