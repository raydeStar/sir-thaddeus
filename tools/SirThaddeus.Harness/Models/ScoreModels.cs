using System.Text.Json.Serialization;

namespace SirThaddeus.Harness.Models;

public sealed record ScoreCard
{
    [JsonPropertyName("hard_pass")]
    public bool HardPass { get; init; }

    [JsonPropertyName("hard_failures")]
    public IReadOnlyList<string> HardFailures { get; init; } = [];

    [JsonPropertyName("soft_score")]
    public double SoftScore { get; init; }

    [JsonPropertyName("judge_score")]
    public double? JudgeScore { get; init; }

    [JsonPropertyName("final_score")]
    public double FinalScore { get; init; }

    [JsonPropertyName("judge_reasons")]
    public IReadOnlyList<string> JudgeReasons { get; init; } = [];

    [JsonPropertyName("judge_suggestions")]
    public IReadOnlyList<string> JudgeSuggestions { get; init; } = [];
}

public sealed record CursorJudgePacket
{
    [JsonPropertyName("test_id")]
    public string TestId { get; init; } = "";

    [JsonPropertyName("test_name")]
    public string TestName { get; init; } = "";

    [JsonPropertyName("user_message")]
    public string UserMessage { get; init; } = "";

    [JsonPropertyName("allowed_tools")]
    public IReadOnlyList<string> AllowedTools { get; init; } = [];

    [JsonPropertyName("final_response")]
    public string FinalResponse { get; init; } = "";

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallSnapshot> ToolCalls { get; init; } = [];

    [JsonPropertyName("hard_failures")]
    public IReadOnlyList<string> HardFailures { get; init; } = [];

    [JsonPropertyName("soft_score")]
    public double SoftScore { get; init; }

    [JsonPropertyName("min_score")]
    public double MinScore { get; init; }
}

public sealed record CursorJudgeResult
{
    [JsonPropertyName("score")]
    public double Score { get; init; }

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
