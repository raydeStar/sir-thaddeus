using System.Text.Json;
using System.Text.Json.Serialization;
using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Tests;

public sealed record ContinuityScript
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("seedState")]
    public DialogueState? SeedState { get; init; }

    [JsonPropertyName("llm")]
    public ContinuityLlmConfig Llm { get; init; } = new();

    [JsonPropertyName("mcp")]
    public ContinuityMcpConfig Mcp { get; init; } = new();

    [JsonPropertyName("turns")]
    public IReadOnlyList<ContinuityTurn> Turns { get; init; } = [];
}

public sealed record ContinuityTurn
{
    [JsonPropertyName("userMessage")]
    public string UserMessage { get; init; } = "";

    [JsonPropertyName("expect")]
    public ContinuityTurnExpectation Expect { get; init; } = new();
}

public sealed record ContinuityTurnExpectation
{
    [JsonPropertyName("success")]
    public bool? Success { get; init; }

    [JsonPropertyName("textContains")]
    public IReadOnlyList<string> TextContains { get; init; } = [];

    [JsonPropertyName("textNotContains")]
    public IReadOnlyList<string> TextNotContains { get; init; } = [];

    [JsonPropertyName("suppressSourceCardsUi")]
    public bool? SuppressSourceCardsUi { get; init; }

    [JsonPropertyName("suppressToolActivityUi")]
    public bool? SuppressToolActivityUi { get; init; }

    [JsonPropertyName("llmRoundTrips")]
    public int? LlmRoundTrips { get; init; }

    [JsonPropertyName("toolCalls")]
    public ContinuityToolExpectations ToolCalls { get; init; } = new();

    [JsonPropertyName("toolArgsContains")]
    public IReadOnlyList<string> ToolArgsContains { get; init; } = [];

    [JsonPropertyName("toolArgsNotContains")]
    public IReadOnlyList<string> ToolArgsNotContains { get; init; } = [];

    [JsonPropertyName("context")]
    public ContinuityExpectedContext? Context { get; init; }
}

public sealed record ContinuityToolExpectations
{
    [JsonPropertyName("include")]
    public IReadOnlyList<string> Include { get; init; } = [];

    [JsonPropertyName("includeAny")]
    public IReadOnlyList<string> IncludeAny { get; init; } = [];

    [JsonPropertyName("exclude")]
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

public sealed record ContinuityExpectedContext
{
    [JsonPropertyName("topic")]
    public string? Topic { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("timeScope")]
    public string? TimeScope { get; init; }

    [JsonPropertyName("contextLocked")]
    public bool? ContextLocked { get; init; }

    [JsonPropertyName("geocodeMismatch")]
    public bool? GeocodeMismatch { get; init; }

    [JsonPropertyName("locationInferred")]
    public bool? LocationInferred { get; init; }
}

public sealed record ContinuityLlmConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "fixed";

    [JsonPropertyName("fixedResponse")]
    public string? FixedResponse { get; init; }

    [JsonPropertyName("classification")]
    public string Classification { get; init; } = "search";

    [JsonPropertyName("entityJson")]
    public string EntityJson { get; init; } = """{"name":"","type":"none","hint":""}""";

    [JsonPropertyName("queryJson")]
    public string QueryJson { get; init; } = """{"query":"top headlines","recency":"any"}""";

    [JsonPropertyName("followupQueryJson")]
    public string? FollowupQueryJson { get; init; }

    [JsonPropertyName("summaryText")]
    public string SummaryText { get; init; } = "Summary.";
}

public sealed record ContinuityMcpConfig
{
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = "fixed";

    [JsonPropertyName("fixedValue")]
    public string FixedValue { get; init; } = "";

    [JsonPropertyName("perTool")]
    public Dictionary<string, ContinuityToolResponseConfig> PerTool { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed record ContinuityToolResponseConfig
{
    [JsonPropertyName("defaultResponse")]
    public string? DefaultResponse { get; init; }

    [JsonPropertyName("contains")]
    public Dictionary<string, string> Contains { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("sequence")]
    public IReadOnlyList<string> Sequence { get; init; } = [];
}

public static class ContinuityScriptLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static IReadOnlyList<ContinuityScript> LoadAll()
    {
        var root = ResolveScriptsDirectory();
        if (!Directory.Exists(root))
            return [];

        var files = Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var scripts = new List<ContinuityScript>(files.Count);
        foreach (var file in files)
        {
            var json = File.ReadAllText(file);
            var script = JsonSerializer.Deserialize<ContinuityScript>(json, JsonOptions);
            if (script is null)
                throw new InvalidOperationException($"Failed to parse continuity script: {file}");
            if (string.IsNullOrWhiteSpace(script.Id))
                throw new InvalidOperationException($"Continuity script missing id: {file}");
            if (script.Turns.Count == 0)
                throw new InvalidOperationException($"Continuity script has no turns: {file}");

            scripts.Add(script);
        }

        return scripts;
    }

    private static string ResolveScriptsDirectory()
    {
        var candidate1 = Path.Combine(AppContext.BaseDirectory, "continuity", "scripts");
        if (Directory.Exists(candidate1))
            return candidate1;

        var candidate2 = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "continuity", "scripts"));
        if (Directory.Exists(candidate2))
            return candidate2;

        return candidate1;
    }
}
