using System.Text.Json.Serialization;

namespace SirThaddeus.Harness.Models;

public sealed record ScoreCard
{
    [JsonPropertyName("testId")]
    public string TestId { get; init; } = "";

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("overallScore")]
    public double OverallScore { get; init; }

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "general";

    [JsonPropertyName("hardGateFailures")]
    public IReadOnlyList<string> HardGateFailures { get; init; } = [];

    [JsonPropertyName("scores")]
    public IReadOnlyDictionary<string, int> Scores { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("strengths")]
    public IReadOnlyList<string> Strengths { get; init; } = [];

    [JsonPropertyName("problems")]
    public IReadOnlyList<string> Problems { get; init; } = [];

    [JsonPropertyName("requiredFixes")]
    public IReadOnlyList<string> RequiredFixes { get; init; } = [];

    [JsonPropertyName("latencyMs")]
    public long LatencyMs { get; init; }

    [JsonPropertyName("tokensIn")]
    public int? TokensIn { get; init; }

    [JsonPropertyName("tokensOut")]
    public int? TokensOut { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "fail";

    [JsonPropertyName("threshold")]
    public double Threshold { get; init; } = 0.85;

    [JsonPropertyName("deterministicChecks")]
    public IReadOnlyList<RubricCheckResult> DeterministicChecks { get; init; } = [];

    // Backward-compatible fields retained for older harness scripts.
    [JsonPropertyName("hard_pass")]
    public bool HardPass => HardGateFailures.Count == 0;

    [JsonPropertyName("hard_failures")]
    public IReadOnlyList<string> HardFailures => HardGateFailures;

    [JsonPropertyName("soft_score")]
    public double SoftScore => OverallScore;

    [JsonPropertyName("judge_score")]
    public double? JudgeScore { get; init; }

    [JsonPropertyName("final_score")]
    public double FinalScore => OverallScore;

    [JsonPropertyName("judge_reasons")]
    public IReadOnlyList<string> JudgeReasons { get; init; } = [];

    [JsonPropertyName("judge_suggestions")]
    public IReadOnlyList<string> JudgeSuggestions { get; init; } = [];

    [JsonPropertyName("keyword_penalty")]
    public double KeywordPenalty { get; init; }

    [JsonPropertyName("deflection_penalty")]
    public double DeflectionPenalty { get; init; }

    [JsonPropertyName("tool_incorporation_penalty")]
    public double ToolIncorporationPenalty { get; init; }

    [JsonPropertyName("assertion_density_penalty")]
    public double AssertionDensityPenalty { get; init; }

    [JsonPropertyName("personality_adjustment")]
    public double PersonalityAdjustment { get; init; }

    [JsonPropertyName("deflection_phrase_count")]
    public int DeflectionPhraseCount { get; init; }

    [JsonPropertyName("hedge_ratio")]
    public double HedgeRatio { get; init; }

    [JsonPropertyName("tool_tokens_incorporated")]
    public int ToolTokensIncorporated { get; init; }

    [JsonPropertyName("tool_tokens_available")]
    public int ToolTokensAvailable { get; init; }

    [JsonPropertyName("required_keywords_found")]
    public int RequiredKeywordsFound { get; init; }

    [JsonPropertyName("required_keywords_total")]
    public int RequiredKeywordsTotal { get; init; }
}

public sealed record RubricCheckResult
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";

    [JsonPropertyName("message")]
    public string Message { get; init; } = "";
}

public sealed record CursorJudgePacket
{
    [JsonPropertyName("test_id")]
    public string TestId { get; init; } = "";

    [JsonPropertyName("test_name")]
    public string TestName { get; init; } = "";

    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "general";

    [JsonPropertyName("user_message")]
    public string UserMessage { get; init; } = "";

    [JsonPropertyName("allowed_tools")]
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    [JsonPropertyName("final_response")]
    public string FinalResponse { get; init; } = "";

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallSnapshot> ToolCalls { get; init; } = [];

    [JsonPropertyName("hard_gate_failures")]
    public IReadOnlyList<string> HardGateFailures { get; init; } = [];

    [JsonPropertyName("deterministic_checks")]
    public IReadOnlyList<RubricCheckResult> DeterministicChecks { get; init; } = [];

    [JsonPropertyName("scores")]
    public IReadOnlyDictionary<string, int> Scores { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("overall_score")]
    public double OverallScore { get; init; }

    [JsonPropertyName("min_score")]
    public double MinScore { get; init; }

    // Legacy aliases for older external judge handoffs.
    [JsonPropertyName("hard_failures")]
    public IReadOnlyList<string> HardFailures => HardGateFailures;

    [JsonPropertyName("soft_score")]
    public double SoftScore => OverallScore;
}

public sealed record CursorJudgeResult
{
    [JsonPropertyName("score")]
    public double Score { get; init; }

    [JsonPropertyName("scores")]
    public IReadOnlyDictionary<string, int> Scores { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    [JsonPropertyName("hardGateFailures")]
    public IReadOnlyList<string> HardGateFailures { get; init; } = [];

    [JsonPropertyName("strengths")]
    public IReadOnlyList<string> Strengths { get; init; } = [];

    [JsonPropertyName("problems")]
    public IReadOnlyList<string> Problems { get; init; } = [];

    [JsonPropertyName("requiredFixes")]
    public IReadOnlyList<string> RequiredFixes { get; init; } = [];

    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    [JsonPropertyName("suggestions")]
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    [JsonPropertyName("patches")]
    public IReadOnlyList<JudgePatchSuggestion> Patches { get; init; } = [];
}

public sealed record JudgePatchSuggestion
{
    [JsonPropertyName("file")]
    public string File { get; init; } = "";

    [JsonPropertyName("find")]
    public string Find { get; init; } = "";

    [JsonPropertyName("replace")]
    public string Replace { get; init; } = "";
}

public sealed record ToolCallSnapshot
{
    [JsonPropertyName("tool_name")]
    public string ToolName { get; init; } = "";

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "";

    [JsonPropertyName("result")]
    public string Result { get; init; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; init; }
}
