using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace SirThaddeus.Harness.Models;

public sealed record HarnessSuite
{
    public required string Name { get; init; }
    public List<HarnessTestCase> Tests { get; init; } = [];
}

public sealed record HarnessTestCase
{
    [JsonPropertyName("id")]
    [YamlMember(Alias = "id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("user_message")]
    [YamlMember(Alias = "user_message")]
    public string UserMessage { get; init; } = "";

    [JsonPropertyName("allowed_tools")]
    [YamlMember(Alias = "allowed_tools")]
    public List<string> AllowedTools { get; init; } = [];

    [JsonPropertyName("mode")]
    [YamlMember(Alias = "mode")]
    public string Mode { get; init; } = "live";

    [JsonPropertyName("assertions")]
    [YamlMember(Alias = "assertions")]
    public HarnessAssertions Assertions { get; init; } = new();

    [JsonPropertyName("expectations")]
    [YamlMember(Alias = "expectations")]
    public HarnessExpectations Expectations { get; init; } = new();

    [JsonPropertyName("min_score")]
    [YamlMember(Alias = "min_score")]
    public double MinScore { get; init; } = 0;

    [JsonPropertyName("patch_targets")]
    [YamlMember(Alias = "patch_targets")]
    public HarnessPatchTargets PatchTargets { get; init; } = new();

    [JsonPropertyName("stub")]
    [YamlMember(Alias = "stub")]
    public HarnessStubConfig Stub { get; init; } = new();
}

public sealed record HarnessAssertions
{
    [JsonPropertyName("required_tools")]
    [YamlMember(Alias = "required_tools")]
    public List<string> RequiredTools { get; init; } = [];

    [JsonPropertyName("forbidden_tools")]
    [YamlMember(Alias = "forbidden_tools")]
    public List<string> ForbiddenTools { get; init; } = [];

    [JsonPropertyName("allowed_tools_only")]
    [YamlMember(Alias = "allowed_tools_only")]
    public bool AllowedToolsOnly { get; init; } = true;

    [JsonPropertyName("require_structured_errors")]
    [YamlMember(Alias = "require_structured_errors")]
    public bool RequireStructuredErrors { get; init; } = true;

    [JsonPropertyName("require_no_hallucinated_citations")]
    [YamlMember(Alias = "require_no_hallucinated_citations")]
    public bool RequireNoHallucinatedCitations { get; init; } = true;
}

public sealed record HarnessExpectations
{
    [JsonPropertyName("required_keywords")]
    [YamlMember(Alias = "required_keywords")]
    public List<string> RequiredKeywords { get; init; } = [];

    [JsonPropertyName("forbidden_keywords")]
    [YamlMember(Alias = "forbidden_keywords")]
    public List<string> ForbiddenKeywords { get; init; } = [];

    [JsonPropertyName("max_response_chars")]
    [YamlMember(Alias = "max_response_chars")]
    public int? MaxResponseChars { get; init; }
}

public sealed record HarnessPatchTargets
{
    [JsonPropertyName("tier1_targets")]
    [YamlMember(Alias = "tier1_targets")]
    public List<string> Tier1Targets { get; init; } = [];

    [JsonPropertyName("tier2_targets")]
    [YamlMember(Alias = "tier2_targets")]
    public List<string> Tier2Targets { get; init; } = [];

    [JsonPropertyName("tier3_targets")]
    [YamlMember(Alias = "tier3_targets")]
    public List<string> Tier3Targets { get; init; } = [];
}

public sealed record HarnessStubConfig
{
    [JsonPropertyName("default_failure")]
    [YamlMember(Alias = "default_failure")]
    public string DefaultFailure { get; init; } = "timeout";

    [JsonPropertyName("per_tool_failures")]
    [YamlMember(Alias = "per_tool_failures")]
    public Dictionary<string, string> PerToolFailures { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
