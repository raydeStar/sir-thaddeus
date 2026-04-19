using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Post-execution validator that checks whether the model's output actually
/// answered the user's request. Runs a heuristic fast-path first, then falls
/// back to a lightweight LLM validation call if needed.
/// </summary>
public sealed class CompletionValidator
{
    private readonly ILlmClient _llm;
    private const int ValidationMaxTokens = 150;

    public CompletionValidator(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Validates whether the assistant's response adequately answers the user's request.
    /// </summary>
    /// <param name="userRequest">The original user message.</param>
    /// <param name="assistantResponse">The model's response text.</param>
    /// <param name="hasToolResults">Whether tool/search results were used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="CompletionValidationResult"/> indicating pass/fail.</returns>
    public async Task<CompletionValidationResult> ValidateAsync(
        string userRequest,
        string assistantResponse,
        bool hasToolResults,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Fast-path: heuristic checks that don't need an LLM call.
        var heuristicResult = TryValidateHeuristic(userRequest, assistantResponse);
        if (heuristicResult is not null)
        {
            sw.Stop();
            return heuristicResult with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }

        // LLM validation call.
        try
        {
            var result = await ValidateWithLlmAsync(
                userRequest, assistantResponse, hasToolResults, cancellationToken);
            sw.Stop();
            return result with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }
        catch
        {
            sw.Stop();
            // On LLM failure, assume the response is valid (fail-open).
            return new CompletionValidationResult
            {
                Passed = true,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }
    }

    /// <summary>
    /// Heuristic fast-path that catches obvious failures without an LLM call.
    /// Returns null if the heuristics are inconclusive.
    /// </summary>
    internal static CompletionValidationResult? TryValidateHeuristic(
        string userRequest,
        string assistantResponse)
    {
        if (string.IsNullOrWhiteSpace(assistantResponse))
        {
            return new CompletionValidationResult
            {
                Passed = false,
                RepairNeeded = true,
                MissingElement = "Response is empty.",
                SuggestedRepair = "Generate a substantive response to the user's request."
            };
        }

        // Detect question-echo: response is just the user's question repeated.
        var responseLower = assistantResponse.Trim().ToLowerInvariant();
        var requestLower = userRequest.Trim().ToLowerInvariant();
        if (responseLower == requestLower ||
            responseLower.StartsWith(requestLower, StringComparison.Ordinal) &&
            responseLower.Length < requestLower.Length * 1.3)
        {
            return new CompletionValidationResult
            {
                Passed = false,
                RepairNeeded = true,
                MissingElement = "Response merely echoes the user's question.",
                SuggestedRepair = "Provide an actual answer instead of restating the question."
            };
        }

        // Detect refusal patterns.
        if (IsRefusalResponse(responseLower))
        {
            return new CompletionValidationResult
            {
                Passed = false,
                RepairNeeded = true,
                MissingElement = "Response is a refusal or inability statement without attempting an answer.",
                SuggestedRepair = "Attempt to answer using available context and tools."
            };
        }

        // Short responses to complex questions are suspicious but not conclusive.
        // Let the LLM decide.
        return null;
    }

    private async Task<CompletionValidationResult> ValidateWithLlmAsync(
        string userRequest,
        string assistantResponse,
        bool hasToolResults,
        CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ValidationPrompts.SystemPrompt),
            ChatMessage.User(ValidationPrompts.BuildUserPrompt(
                userRequest, assistantResponse, hasToolResults))
        };

        var response = await _llm.ChatAsync(
            messages, tools: null, ValidationMaxTokens, cancellationToken);

        return ParseValidationResponse(response.Content);
    }

    /// <summary>
    /// Parses the LLM's validation response JSON.
    /// </summary>
    internal static CompletionValidationResult ParseValidationResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return PassedResult();

        try
        {
            var json = content.Trim();

            // Strip markdown code fences.
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var passed = GetBoolProperty(root, "Passed") ?? GetBoolProperty(root, "passed") ?? true;
            var repairNeeded = GetBoolProperty(root, "RepairNeeded") ?? GetBoolProperty(root, "repairNeeded") ?? false;
            var missingElement = GetStringProperty(root, "MissingElement") ?? GetStringProperty(root, "missingElement");
            var suggestedRepair = GetStringProperty(root, "SuggestedRepair") ?? GetStringProperty(root, "suggestedRepair");

            return new CompletionValidationResult
            {
                Passed = passed,
                RepairNeeded = repairNeeded,
                MissingElement = missingElement,
                SuggestedRepair = suggestedRepair
            };
        }
        catch (JsonException)
        {
            // Can't parse — fail-open.
            return PassedResult();
        }
    }

    private static CompletionValidationResult PassedResult() => new() { Passed = true };

    private static bool? GetBoolProperty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True)
            return true;
        if (root.TryGetProperty(name, out prop) && prop.ValueKind == JsonValueKind.False)
            return false;
        return null;
    }

    private static string? GetStringProperty(JsonElement root, string name)
    {
        if (root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static bool IsRefusalResponse(string lower)
    {
        // Match common refusal/inability patterns.
        return lower.StartsWith("i can't", StringComparison.Ordinal) ||
               lower.StartsWith("i cannot", StringComparison.Ordinal) ||
               lower.StartsWith("i'm unable to", StringComparison.Ordinal) ||
               lower.StartsWith("i am unable to", StringComparison.Ordinal) ||
               lower.StartsWith("sorry, i can't", StringComparison.Ordinal) ||
               lower.StartsWith("sorry, i cannot", StringComparison.Ordinal) ||
               lower.StartsWith("i don't have", StringComparison.Ordinal) ||
               lower.StartsWith("i do not have", StringComparison.Ordinal);
    }
}
