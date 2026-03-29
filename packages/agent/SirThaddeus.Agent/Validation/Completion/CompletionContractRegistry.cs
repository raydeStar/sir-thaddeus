namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Static registry mapping intent strings to their completion contracts.
/// Deterministic — no LLM, no configuration. Add new intents here
/// before adding them to the router.
///
/// Intents without an explicit contract get <see cref="CompletionContract.AlwaysSatisfied"/>.
/// </summary>
public static class CompletionContractRegistry
{
    // ── Shared field sets ────────────────────────────────────────────

    private static readonly FieldRequirement Name = new()
    {
        FieldName = "name",
        Description = "business or entity name",
        Necessity = FieldNecessity.Required
    };

    private static readonly FieldRequirement Address = new()
    {
        FieldName = "address",
        Description = "street address or location",
        Necessity = FieldNecessity.Required,
        Aliases = ["formatted_address", "location"]
    };

    private static readonly FieldRequirement Phone = new()
    {
        FieldName = "phone",
        Description = "phone number",
        Necessity = FieldNecessity.Optional,
        Aliases = ["phone_number", "telephone"]
    };

    private static readonly FieldRequirement Website = new()
    {
        FieldName = "website",
        Description = "website URL",
        Necessity = FieldNecessity.Optional,
        Aliases = ["url", "homepage"]
    };

    private static readonly FieldRequirement Hours = new()
    {
        FieldName = "hours",
        Description = "business hours",
        Necessity = FieldNecessity.Optional,
        Aliases = ["opening_hours", "business_hours"]
    };

    private static readonly FieldRequirement SourceUrl = new()
    {
        FieldName = "source_url",
        Description = "source URL for verification",
        Necessity = FieldNecessity.Optional,
        Aliases = ["url", "source", "link"]
    };

    private static readonly FieldRequirement Answer = new()
    {
        FieldName = "answer",
        Description = "direct answer to the query",
        Necessity = FieldNecessity.Required
    };

    // ── Contract definitions ─────────────────────────────────────────

    private static readonly Dictionary<string, CompletionContract> Contracts =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Nearby business lookup (e.g. "bakeries near me")
            [Intents.LookupFact] = new CompletionContract
            {
                Intent = Intents.LookupFact,
                Label = "fact_lookup",
                Fields = [Name, Address, Phone, Website],
                Evidence = EvidenceRequirement.AtLeastOneUrl,
                MinItems = 1
            },

            // News lookup
            [Intents.LookupNews] = new CompletionContract
            {
                Intent = Intents.LookupNews,
                Label = "news_lookup",
                Fields = [Answer, SourceUrl],
                Evidence = EvidenceRequirement.AtLeastOneUrl,
                MinItems = 1
            },

            // Product recommendation lookup
            [Intents.LookupProduct] = new CompletionContract
            {
                Intent = Intents.LookupProduct,
                Label = "product_lookup",
                Fields = [Answer, SourceUrl],
                Evidence = EvidenceRequirement.AtLeastOneUrl,
                MinItems = 1
            },

            // Deep-dive briefing
            [Intents.LookupDeepDive] = new CompletionContract
            {
                Intent = Intents.LookupDeepDive,
                Label = "deep_dive",
                Fields = [Answer, SourceUrl],
                Evidence = EvidenceRequirement.NamedWithUrl,
                MinItems = 1
            },

            // Web search (generic)
            [Intents.LookupSearch] = new CompletionContract
            {
                Intent = Intents.LookupSearch,
                Label = "web_search",
                Fields = [Answer],
                Evidence = EvidenceRequirement.AtLeastOneUrl,
                MinItems = 0
            },

            // Browse once — user gave a URL
            [Intents.BrowseOnce] = new CompletionContract
            {
                Intent = Intents.BrowseOnce,
                Label = "browse_once",
                Fields = [Answer],
                Evidence = EvidenceRequirement.None,
                MinItems = 0
            },

            // Screen observe — deterministic capture path
            [Intents.ScreenObserve] = new CompletionContract
            {
                Intent = Intents.ScreenObserve,
                Label = "screen_observe",
                Fields = [Answer],
                Evidence = EvidenceRequirement.None,
                MinItems = 0
            },

            // Memory write — just needs confirmation
            [Intents.MemoryWrite] = new CompletionContract
            {
                Intent = Intents.MemoryWrite,
                Label = "memory_write",
                Fields = [Answer],
                Evidence = EvidenceRequirement.None,
                MinItems = 0
            },
        };

    // ── Lookup ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the completion contract for the given intent.
    /// Intents without an explicit contract get <see cref="CompletionContract.AlwaysSatisfied"/>.
    /// </summary>
    public static CompletionContract For(string intent) =>
        Contracts.TryGetValue(intent, out var contract)
            ? contract
            : CompletionContract.AlwaysSatisfied;

    /// <summary>
    /// Returns all registered contracts (for diagnostics / tests).
    /// </summary>
    public static IReadOnlyDictionary<string, CompletionContract> All => Contracts;
}
