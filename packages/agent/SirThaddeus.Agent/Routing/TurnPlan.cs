using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Orthogonal capability description proposed for a turn. In the initial
/// release this contract is shadow-only: it records what the existing pipeline
/// appears to need but cannot grant permissions or change execution.
/// </summary>
public sealed record TurnPlan
{
    public required TurnPrimaryKind PrimaryKind { get; init; }
    public bool DynamicMemoryRequired { get; init; }
    public bool FreshnessRequired { get; init; }
    public bool ToolsRequired { get; init; }
    public bool FilesOrUrlsRequired { get; init; }
    public bool DeepReasoningRequired { get; init; }
    public bool HighStakesHandlingRequired { get; init; }
    public bool StructuredResponseRequired { get; init; }
    public bool BackgroundPersistenceRequired { get; init; } = true;
    public bool RequiresExistingFullPath { get; init; }
    public double Confidence { get; init; }
    public int TimeBudgetMs { get; init; }
    public IReadOnlyList<TurnCapabilityReason> Reasons { get; init; } = [];
}

public enum TurnPrimaryKind
{
    Conversation,
    Reasoning,
    Research,
    Memory,
    ToolTask,
    Utility,
    Creative,
    Ambiguous
}

public sealed record TurnCapabilityReason(string Capability, string Code);

/// <summary>
/// Non-sticky footprint from the immediately preceding turn. It exists only
/// to resolve referential follow-ups; it is not a cached route or thread mode.
/// </summary>
public sealed record PreviousTurnCapabilityFootprint
{
    public bool UsedDynamicMemory { get; init; }
    public bool UsedFreshness { get; init; }
    public bool UsedTools { get; init; }
    public bool UsedFilesOrUrls { get; init; }
    public bool UsedDeepReasoning { get; init; }
}

public sealed record TurnPlanningInput
{
    public required string UserText { get; init; }
    public RoutingFeatures? Features { get; init; }
    public bool HasAttachments { get; init; }
    public PreviousTurnCapabilityFootprint? PreviousTurn { get; init; }
}

/// <summary>
/// Deterministic, side-effect-free compiler used by shadow planning. It does
/// not retrieve memory, discover tools, access the network, or call an LLM.
/// Uncertain turns explicitly retain the existing full path.
/// </summary>
public static class TurnPlanCompiler
{
    public static TurnPlan Compile(TurnPlanningInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = input.UserText?.Trim() ?? string.Empty;
        var lower = text.ToLowerInvariant();
        var features = input.Features ?? RoutingFeatures.Extract(text);
        var reasons = new List<TurnCapabilityReason>();

        var hasUrl = ContainsUrl(lower);
        var isEllipticalFollowUp = LooksLikeEllipticalFollowUp(lower, features.WordCount);
        var explicitMemory = features.LooksLikeMemoryWrite || ContainsAny(lower,
            "remember", "what do you remember", "what do you know about me",
            "i told you", "my profile", "my preferences", "my favorite");
        var implicitPersonalMemory = ContainsAny(lower,
            "based on what you know about me", "given what you know about me",
            "better for me", "my routine", "my schedule", "my history");
        var inheritedMemory = isEllipticalFollowUp && input.PreviousTurn?.UsedDynamicMemory == true;

        var highStakes = ContainsAny(lower,
            "medical advice", "diagnosis", "symptom", "medication", "dosage",
            "legal advice", "lawsuit", "criminal charge", "contract liability",
            "investment advice", "retirement money", "tax advice", "financial emergency");
        var explicitResearch = features.LooksLikeDeepDive || features.LooksLikeNewsLookup ||
            features.LooksLikeWebSearch || ContainsAny(lower,
                "research this", "deep research", "investigate", "find sources",
                "cite sources", "look this up", "search the web");
        var freshness = features.LooksLikeNewsLookup || features.LooksLikeWebSearch ||
            ContainsAny(lower, "latest", "current", "right now", "today", "recent",
                "price now", "weather", "forecast", "breaking news");
        var filesOrUrls = input.HasAttachments || hasUrl || features.LooksLikeFileRequest ||
            features.LooksLikeBrowseRequest;
        var explicitTool = features.LooksLikeScreenRequest || features.LooksLikeSystemCommand ||
            features.LooksLikeFileRequest || features.LooksLikeBrowseRequest ||
            features.LooksLikeLocalBusiness;
        var factLookup = features.LooksLikeFactLookup;
        var tools = explicitTool || explicitResearch || freshness || highStakes || factLookup ||
            (isEllipticalFollowUp && input.PreviousTurn?.UsedTools == true);
        var deepReasoning = features.IsLogicPuzzle || features.IsReasoningFollowUp ||
            ContainsAny(lower, "think deeply", "analyze carefully", "step by step",
                "reason through", "trade-offs", "tradeoffs", "substantial plan");
        var structured = ContainsAny(lower, "return json", "as json", "in a table",
            "structured output", "exact schema", "bullet list", "checklist");
        var creative = ContainsAny(lower, "write a story", "write a poem", "brainstorm",
            "creative", "roleplay", "role-play", "fiction", "song idea");
        var utility = UtilityRouter.TryHandle(text) is not null;
        var conversational = IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower) ||
            ContainsAny(lower, "tell me a joke", "thanks", "thank you");

        AddReason(reasons, explicitMemory, "dynamic_memory", "explicit_memory_signal");
        AddReason(reasons, implicitPersonalMemory, "dynamic_memory", "implicit_personal_history_signal");
        AddReason(reasons, inheritedMemory, "dynamic_memory", "referential_followup_inheritance");
        AddReason(reasons, freshness, "freshness", "current_information_signal");
        AddReason(reasons, tools, "tools", explicitTool ? "explicit_tool_signal" : "escalated_capability_signal");
        AddReason(reasons, filesOrUrls, "files_or_urls", input.HasAttachments ? "attachment_present" : "file_or_url_signal");
        AddReason(reasons, deepReasoning, "deep_reasoning", "reasoning_signal");
        AddReason(reasons, highStakes, "high_stakes", "high_stakes_domain_signal");
        AddReason(reasons, structured, "structured_response", "explicit_output_contract");
        reasons.Add(new TurnCapabilityReason("background_persistence", "preserve_existing_behavior"));

        var dynamicMemory = explicitMemory || implicitPersonalMemory || inheritedMemory;
        var hasHardEscalation = dynamicMemory || freshness || tools || filesOrUrls || deepReasoning || highStakes;

        TurnPrimaryKind kind;
        double confidence;
        if (utility)
        {
            kind = TurnPrimaryKind.Utility;
            confidence = 0.99;
            reasons.Add(new TurnCapabilityReason("primary_kind", "deterministic_utility_match"));
        }
        else if (explicitMemory || implicitPersonalMemory || inheritedMemory)
        {
            kind = TurnPrimaryKind.Memory;
            confidence = explicitMemory ? 0.98 : 0.90;
            reasons.Add(new TurnCapabilityReason("primary_kind", "memory_capability_required"));
        }
        else if (filesOrUrls || explicitTool)
        {
            kind = TurnPrimaryKind.ToolTask;
            confidence = 0.97;
            reasons.Add(new TurnCapabilityReason("primary_kind", "tool_or_input_capability_required"));
        }
        else if (explicitResearch || freshness || highStakes || factLookup)
        {
            kind = TurnPrimaryKind.Research;
            confidence = explicitResearch || freshness ? 0.96 : 0.88;
            reasons.Add(new TurnCapabilityReason("primary_kind", "research_or_verification_required"));
        }
        else if (deepReasoning)
        {
            kind = TurnPrimaryKind.Reasoning;
            confidence = 0.94;
            reasons.Add(new TurnCapabilityReason("primary_kind", "deep_reasoning_required"));
        }
        else if (creative)
        {
            kind = TurnPrimaryKind.Creative;
            confidence = 0.94;
            reasons.Add(new TurnCapabilityReason("primary_kind", "creative_request"));
        }
        else if (conversational)
        {
            kind = TurnPrimaryKind.Conversation;
            confidence = 0.97;
            reasons.Add(new TurnCapabilityReason("primary_kind", "high_confidence_conversation"));
        }
        else
        {
            kind = TurnPrimaryKind.Ambiguous;
            confidence = 0.40;
            reasons.Add(new TurnCapabilityReason("fallback", "existing_helper_classifier_required"));
        }

        var requiresFullPath = kind == TurnPrimaryKind.Ambiguous ||
            isEllipticalFollowUp ||
            hasHardEscalation;

        return new TurnPlan
        {
            PrimaryKind = kind,
            DynamicMemoryRequired = dynamicMemory,
            FreshnessRequired = freshness || highStakes,
            ToolsRequired = tools,
            FilesOrUrlsRequired = filesOrUrls,
            DeepReasoningRequired = deepReasoning,
            HighStakesHandlingRequired = highStakes,
            StructuredResponseRequired = structured,
            BackgroundPersistenceRequired = true,
            RequiresExistingFullPath = requiresFullPath,
            Confidence = confidence,
            TimeBudgetMs = kind switch
            {
                TurnPrimaryKind.Conversation or TurnPrimaryKind.Utility => 2_000,
                TurnPrimaryKind.Reasoning or TurnPrimaryKind.Creative => 15_000,
                _ => 30_000
            },
            Reasons = reasons
        };
    }

    private static bool LooksLikeEllipticalFollowUp(string lower, int wordCount)
    {
        if (wordCount == 0 || wordCount > 10)
            return false;

        return lower is "again" or "continue" or "go on" or "why" or "why?" ||
               lower.StartsWith("what about ", StringComparison.Ordinal) ||
               lower.StartsWith("how about ", StringComparison.Ordinal) ||
               lower.Contains("that one", StringComparison.Ordinal) ||
               lower.Contains("same thing", StringComparison.Ordinal) ||
               lower.Contains("do it again", StringComparison.Ordinal);
    }

    private static bool ContainsUrl(string lower) =>
        lower.Contains("http://", StringComparison.Ordinal) ||
        lower.Contains("https://", StringComparison.Ordinal) ||
        lower.Contains("www.", StringComparison.Ordinal);

    private static bool ContainsAny(string value, params string[] needles) =>
        needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));

    private static void AddReason(
        ICollection<TurnCapabilityReason> reasons,
        bool condition,
        string capability,
        string code)
    {
        if (condition)
            reasons.Add(new TurnCapabilityReason(capability, code));
    }
}
