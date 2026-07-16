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
    public string Mode { get; init; } = "headless";

    [JsonPropertyName("category")]
    [YamlMember(Alias = "category")]
    public string? Category { get; init; }

    [JsonPropertyName("rubric_profile")]
    [YamlMember(Alias = "rubric_profile")]
    public string? RubricProfile { get; init; }

    /// <summary>
    /// Optional per-test personality override. When set, the runner uses this
    /// profile instead of the global setting, enabling cross-profile comparison
    /// suites without separate harness invocations.
    /// </summary>
    [JsonPropertyName("personality_id")]
    [YamlMember(Alias = "personality_id")]
    public string? PersonalityId { get; init; }

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

    [JsonPropertyName("state_setup")]
    [YamlMember(Alias = "state_setup")]
    public HarnessStateSetup StateSetup { get; init; } = new();

    [JsonPropertyName("wiki_context")]
    [YamlMember(Alias = "wiki_context")]
    public HarnessWikiContextSetup? WikiContext { get; init; }

    [JsonPropertyName("observations")]
    [YamlMember(Alias = "observations")]
    public List<HarnessObservationRequest> Observations { get; init; } = [];
}

/// <summary>
/// Evaluator-owned Wiki attachment resolved against the disposable harness
/// library after state setup. Names keep generated runtime identifiers out of
/// frozen fixtures while exercising the same attachment API as the desktop UI.
/// </summary>
public sealed record HarnessWikiContextSetup
{
    [JsonPropertyName("mode")]
    [YamlMember(Alias = "mode")]
    public string Mode { get; init; } = "none";

    [JsonPropertyName("root_name")]
    [YamlMember(Alias = "root_name")]
    public string? RootName { get; init; }

    [JsonPropertyName("page_title")]
    [YamlMember(Alias = "page_title")]
    public string? PageTitle { get; init; }
}

public sealed record HarnessStateSetup
{
    [JsonPropertyName("wiki_roots")]
    [YamlMember(Alias = "wiki_roots")]
    public List<HarnessWikiRootSetup> WikiRoots { get; init; } = [];

    [JsonPropertyName("files")]
    [YamlMember(Alias = "files")]
    public List<HarnessFileSetup> Files { get; init; } = [];
}

public sealed record HarnessFileSetup
{
    [JsonPropertyName("path")]
    [YamlMember(Alias = "path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("content")]
    [YamlMember(Alias = "content")]
    public string Content { get; init; } = "";
}

public sealed record HarnessWikiRootSetup
{
    [JsonPropertyName("name")]
    [YamlMember(Alias = "name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("pages")]
    [YamlMember(Alias = "pages")]
    public List<HarnessWikiPageSetup> Pages { get; init; } = [];
}

public sealed record HarnessWikiPageSetup
{
    [JsonPropertyName("title")]
    [YamlMember(Alias = "title")]
    public string Title { get; init; } = "";

    [JsonPropertyName("markdown")]
    [YamlMember(Alias = "markdown")]
    public string Markdown { get; init; } = "";
}

public sealed record HarnessObservationRequest
{
    [JsonPropertyName("type")]
    [YamlMember(Alias = "type")]
    public string Type { get; init; } = "";

    [JsonPropertyName("root_names")]
    [YamlMember(Alias = "root_names")]
    public List<string> RootNames { get; init; } = [];

    [JsonPropertyName("paths")]
    [YamlMember(Alias = "paths")]
    public List<string> Paths { get; init; } = [];
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

    /// <summary>
    /// When true (default), hard-fails if the final response looks like an
    /// infrastructure or configuration error (missing API key, provider not
    /// configured, env-var setup instructions). Automatically disabled for
    /// stub-mode tests.
    /// </summary>
    [JsonPropertyName("forbid_infrastructure_errors")]
    [YamlMember(Alias = "forbid_infrastructure_errors")]
    public bool ForbidInfrastructureErrors { get; init; } = true;
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

    [JsonPropertyName("require_json")]
    [YamlMember(Alias = "require_json")]
    public bool RequireJson { get; init; }

    [JsonPropertyName("required_json_fields")]
    [YamlMember(Alias = "required_json_fields")]
    public List<string> RequiredJsonFields { get; init; } = [];

    [JsonPropertyName("forbidden_phrases")]
    [YamlMember(Alias = "forbidden_phrases")]
    public List<string> ForbiddenPhrases { get; init; } = [];

    // ── Personality-specific scoring dimensions ──────────────────────

    /// <summary>
    /// When true, asserts the configured signature note appears in output.
    /// When false, asserts it does not. Null = no check.
    /// </summary>
    [JsonPropertyName("expect_signature")]
    [YamlMember(Alias = "expect_signature")]
    public bool? ExpectSignature { get; init; }

    /// <summary>
    /// Caps average sentence length (in words). Responses exceeding this
    /// threshold are penalized for verbosity. Null = no check.
    /// </summary>
    [JsonPropertyName("max_avg_sentence_words")]
    [YamlMember(Alias = "max_avg_sentence_words")]
    public int? MaxAvgSentenceWords { get; init; }

    /// <summary>
    /// Minimum average sentence length (in words). Responses below this
    /// threshold are penalized for being too terse. Null = no check.
    /// </summary>
    [JsonPropertyName("min_avg_sentence_words")]
    [YamlMember(Alias = "min_avg_sentence_words")]
    public int? MinAvgSentenceWords { get; init; }

    /// <summary>
    /// When true, penalizes responses containing casual slang
    /// (lol, lmao, btw, ngl, tbh, omg, imho).
    /// </summary>
    [JsonPropertyName("forbid_slang")]
    [YamlMember(Alias = "forbid_slang")]
    public bool ForbidSlang { get; init; }

    /// <summary>
    /// When true, asserts the response contains structured formatting
    /// (numbered lists, bullet points, headers, or step markers).
    /// </summary>
    [JsonPropertyName("expect_structured_format")]
    [YamlMember(Alias = "expect_structured_format")]
    public bool ExpectStructuredFormat { get; init; }

    /// <summary>
    /// When true, asserts the response acknowledges user emotion
    /// (contains empathy markers like "understand", "frustrating", "hear you").
    /// </summary>
    [JsonPropertyName("expect_empathy")]
    [YamlMember(Alias = "expect_empathy")]
    public bool ExpectEmpathy { get; init; }

    /// <summary>
    /// When true, asserts the response pushes back on the premise
    /// rather than blindly complying. Checks for corrective language.
    /// </summary>
    [JsonPropertyName("expect_pushback")]
    [YamlMember(Alias = "expect_pushback")]
    public bool ExpectPushback { get; init; }

    /// <summary>
    /// When true, asserts the response is a refusal (safety boundary).
    /// Checks for refusal markers and absence of compliance.
    /// </summary>
    [JsonPropertyName("expect_refusal")]
    [YamlMember(Alias = "expect_refusal")]
    public bool ExpectRefusal { get; init; }
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
