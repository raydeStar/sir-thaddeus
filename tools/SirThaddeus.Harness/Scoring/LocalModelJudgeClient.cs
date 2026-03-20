using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Config;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Scoring;

/// <summary>
/// Calls the local LLM directly (via the same OpenAI-compatible endpoint
/// Sir Thaddeus uses) to judge responses. Runs in-process — no filesystem
/// handshake like CursorJudgeClient.
///
/// CRITICAL: The judge receives ONLY the user message, the tool trace, and
/// the final response. It does NOT receive the YAML test spec.
/// </summary>
public sealed class LocalModelJudgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private const string JudgeSystemPrompt = """
        You are a strict, impartial judge evaluating an AI assistant's response.

        You will receive:
        1. The user's original question
        2. A trace of tools the assistant called and their results
        3. The assistant's final response

        Evaluate the response on these five dimensions, each scored 0-10:

        1. **Correctness**: Did the response answer the question factually?
           Score 0 if it's a deflection or refusal. Score 10 if fully correct.
        2. **Completeness**: Did it address all parts of the query?
           Score 0 if it ignored the question. Score 10 if comprehensive.
        3. **Tool Appropriateness**: Were the right tools called for the right reasons?
           Score 10 if tools match the task. Score 5 if some were unnecessary.
           Score 0 if tools were called and results ignored, or wrong tools used.
        4. **Synthesis Quality**: Did it integrate tool results coherently?
           Score 0 if tool results were fetched but not used. Score 10 if well-integrated.
           Score N/A (use 7) if no tools were needed.
        5. **Confidence Calibration**: Is the expressed certainty appropriate to evidence?
           Score 0 if the response hedges on everything despite having evidence.
           Score 10 if confidence matches evidence. Score 5 if overconfident or underconfident.

        Respond with ONLY a JSON object in this exact format (no markdown fences):
        {
          "correctness": <0-10>,
          "completeness": <0-10>,
          "tool_appropriateness": <0-10>,
          "synthesis_quality": <0-10>,
          "confidence_calibration": <0-10>,
          "overall": <0-10>,
          "reasons": ["<reason1>", "<reason2>"],
          "suggestions": ["<suggestion1>"]
        }

        The "overall" score should be a weighted judgment, not a simple average.
        Correctness matters most. A deflection that refuses to answer gets overall ≤ 2.
        """;

    public async Task<CursorJudgeResult?> EvaluateAsync(
        string userMessage,
        IReadOnlyList<TraceStep> steps,
        string finalResponse,
        CancellationToken cancellationToken)
    {
        var settings = SettingsManager.Load();
        if (string.IsNullOrWhiteSpace(settings.Llm.BaseUrl))
            return null;

        var options = new LlmClientOptions
        {
            BaseUrl = settings.Llm.BaseUrl,
            Model = settings.Llm.Model,
            MaxTokens = 512,
            ContextWindowTokens = settings.Llm.ContextWindowTokens,
            Temperature = 0.1
        };

        using var httpClient = new HttpClient();
        var llm = new LmStudioClient(options, httpClient);

        var userPrompt = BuildUserPrompt(userMessage, steps, finalResponse);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(JudgeSystemPrompt),
            ChatMessage.User(userPrompt)
        };

        LlmResponse response;
        try
        {
            response = await llm.ChatAsync(messages, tools: null, maxTokensOverride: 512, cancellationToken);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [judge] LLM call failed: {ex.Message}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(response.Content))
            return null;

        return ParseJudgeResponse(response.Content);
    }

    private static string BuildUserPrompt(
        string userMessage,
        IReadOnlyList<TraceStep> steps,
        string finalResponse)
    {
        var sb = new StringBuilder();

        sb.AppendLine("## User Question");
        sb.AppendLine(userMessage);
        sb.AppendLine();

        var toolSteps = steps
            .Where(s => string.Equals(s.StepType, "tool_call", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.StepType, "tool_result", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (toolSteps.Count > 0)
        {
            sb.AppendLine("## Tool Trace");
            foreach (var step in toolSteps)
            {
                if (string.Equals(step.StepType, "tool_call", StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"CALL: {step.ToolName}");
                    if (!string.IsNullOrWhiteSpace(step.Arguments))
                    {
                        var argsPreview = step.Arguments.Length > 500
                            ? step.Arguments[..500] + "..."
                            : step.Arguments;
                        sb.AppendLine($"  args: {argsPreview}");
                    }
                }
                else
                {
                    var resultPreview = (step.Result ?? "").Length > 1000
                        ? step.Result![..1000] + "..."
                        : step.Result ?? "(empty)";
                    sb.AppendLine($"RESULT: {resultPreview}");
                    if (step.Error is not null)
                        sb.AppendLine($"  ERROR: {step.Error.Code} - {step.Error.Message}");
                }
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Final Response");
        sb.AppendLine(finalResponse);

        return sb.ToString();
    }

    private static CursorJudgeResult? ParseJudgeResponse(string content)
    {
        // Strip markdown code fences if the model wrapped the JSON
        var json = Regex.Replace(content.Trim(), @"^```(?:json)?\s*|\s*```$", "", RegexOptions.Multiline);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var overall = GetDouble(root, "overall");
            if (overall < 0) return null;

            var reasons = GetStringArray(root, "reasons");
            var suggestions = GetStringArray(root, "suggestions");

            // Include dimension scores in reasons for transparency
            var dimensionReasons = new List<string>(reasons);
            var correctness = GetDouble(root, "correctness");
            var completeness = GetDouble(root, "completeness");
            var toolAppropriateness = GetDouble(root, "tool_appropriateness");
            var synthesisQuality = GetDouble(root, "synthesis_quality");
            var confidenceCalibration = GetDouble(root, "confidence_calibration");

            if (correctness >= 0)
                dimensionReasons.Insert(0,
                    $"Dimensions: correctness={correctness:F1} completeness={completeness:F1} " +
                    $"tool_appropriateness={toolAppropriateness:F1} synthesis={synthesisQuality:F1} " +
                    $"confidence={confidenceCalibration:F1}");

            return new CursorJudgeResult
            {
                Score = Math.Clamp(overall, 0, 10),
                Reasons = dimensionReasons,
                Suggestions = suggestions
            };
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"  [judge] Failed to parse judge response: {ex.Message}");
            return null;
        }
    }

    private static double GetDouble(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var el) && el.TryGetDouble(out var value))
            return value;
        return -1;
    }

    private static List<string> GetStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
}
