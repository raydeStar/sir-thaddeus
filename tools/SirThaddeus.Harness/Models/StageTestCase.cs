using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace SirThaddeus.Harness.Models;

public sealed record StageSuite
{
    public required string Name { get; init; }
    public List<StageTestCase> Tests { get; init; } = [];
}

/// <summary>
/// A stage-level test case that validates individual pipeline stage outputs
/// without requiring a full E2E run through the headless runtime.
/// </summary>
public sealed record StageTestCase
{
    [JsonPropertyName("id")]
    [YamlMember(Alias = "id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("input")]
    [YamlMember(Alias = "input")]
    public string Input { get; init; } = "";

    [JsonPropertyName("context")]
    [YamlMember(Alias = "context")]
    public StageExecutionContext Context { get; init; } = new();

    [JsonPropertyName("stage_checks")]
    [YamlMember(Alias = "stage_checks")]
    public StageChecks Checks { get; init; } = new();
}

public sealed record StageExecutionContext
{
    [JsonPropertyName("assistant_context")]
    [YamlMember(Alias = "assistant_context")]
    public string AssistantContext { get; init; } = "";

    [JsonPropertyName("followup_anchor")]
    [YamlMember(Alias = "followup_anchor")]
    public string FollowUpAnchor { get; init; } = "";

    [JsonPropertyName("user_city")]
    [YamlMember(Alias = "user_city")]
    public string UserCity { get; init; } = "";

    [JsonPropertyName("has_recent_rationale")]
    [YamlMember(Alias = "has_recent_rationale")]
    public bool HasRecentFirstPrinciplesRationale { get; init; }

    [JsonPropertyName("has_recent_search_results")]
    [YamlMember(Alias = "has_recent_search_results")]
    public bool HasRecentSearchResults { get; init; }
}

public sealed record StageChecks
{
    [JsonPropertyName("preprocess")]
    [YamlMember(Alias = "preprocess")]
    public PreprocessCheck? Preprocess { get; init; }

    [JsonPropertyName("classify")]
    [YamlMember(Alias = "classify")]
    public ClassifyCheck? Classify { get; init; }

    [JsonPropertyName("query")]
    [YamlMember(Alias = "query")]
    public QueryCheck? Query { get; init; }
}

public sealed record PreprocessCheck
{
    [JsonPropertyName("expected_intent_count")]
    [YamlMember(Alias = "expected_intent_count")]
    public int? ExpectedIntentCount { get; init; }

    [JsonPropertyName("is_multi_intent")]
    [YamlMember(Alias = "is_multi_intent")]
    public bool? IsMultiIntent { get; init; }

    [JsonPropertyName("intent_must_contain")]
    [YamlMember(Alias = "intent_must_contain")]
    public List<string> IntentMustContain { get; init; } = [];

    [JsonPropertyName("intent_must_not_contain")]
    [YamlMember(Alias = "intent_must_not_contain")]
    public List<string> IntentMustNotContain { get; init; } = [];
}

public sealed record ClassifyCheck
{
    [JsonPropertyName("expected_intents")]
    [YamlMember(Alias = "expected_intents")]
    public List<string> ExpectedIntents { get; init; } = [];

    [JsonPropertyName("forbidden_intents")]
    [YamlMember(Alias = "forbidden_intents")]
    public List<string> ForbiddenIntents { get; init; } = [];

    [JsonPropertyName("must_be_deterministic")]
    [YamlMember(Alias = "must_be_deterministic")]
    public bool? MustBeDeterministic { get; init; }
}

public sealed record QueryCheck
{
    [JsonPropertyName("search_query_must_contain")]
    [YamlMember(Alias = "search_query_must_contain")]
    public List<string> SearchQueryMustContain { get; init; } = [];

    [JsonPropertyName("search_query_must_not_contain")]
    [YamlMember(Alias = "search_query_must_not_contain")]
    public List<string> SearchQueryMustNotContain { get; init; } = [];

    [JsonPropertyName("max_query_length")]
    [YamlMember(Alias = "max_query_length")]
    public int? MaxQueryLength { get; init; }

    [JsonPropertyName("must_have_location_context")]
    [YamlMember(Alias = "must_have_location_context")]
    public bool? MustHaveLocationContext { get; init; }
}
