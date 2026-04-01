using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Classifies every incoming user request into one of 7 <see cref="TaskLane"/>
/// values before any tool is loaded or model action occurs.
/// Uses deterministic heuristics first; falls back to a compact LLM prompt
/// when heuristics are inconclusive.
/// </summary>
public sealed class LaneRouter
{
    private readonly ILlmClient _llm;

    /// <summary>Confidence threshold below which results are treated as unreliable.</summary>
    private const double MinConfidenceThreshold = 0.6;

    /// <summary>Max tokens the LLM may use for the classification response.</summary>
    private const int ClassifyMaxTokens = 80;

    public LaneRouter(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Classifies <paramref name="userInput"/> into one of the 7 task lanes.
    /// </summary>
    /// <param name="userInput">The raw user message.</param>
    /// <param name="ctx">Conversation context for continuity hints.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="LaneRoutingResult"/> with lane, confidence, and rationale.</returns>
    public async Task<LaneRoutingResult> ClassifyAsync(
        string userInput,
        ConversationContext ctx,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        var heuristic = TryClassifyHeuristic(userInput);
        if (heuristic is not null)
        {
            sw.Stop();
            return heuristic with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
        }

        var llmResult = await ClassifyWithLlmAsync(userInput, ctx, cancellationToken);
        sw.Stop();
        return llmResult with { ElapsedMs = sw.Elapsed.TotalMilliseconds };
    }

    // ── Heuristic fast-path ──────────────────────────────────────────

    /// <summary>
    /// Attempts deterministic classification using keyword and pattern heuristics.
    /// Returns <c>null</c> when heuristics are inconclusive.
    /// </summary>
    internal static LaneRoutingResult? TryClassifyHeuristic(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return MakeResult(TaskLane.Conversation, 0.95, "Empty or whitespace input.");

        var lower = userInput.Trim().ToLowerInvariant();

        // ── Deterministic ────────────────────────────────────────────
        if (LooksLikeDeterministic(lower))
            return MakeResult(TaskLane.Deterministic, 0.95, "Input contains a computable expression.");

        // ── FileSystem ───────────────────────────────────────────────
        if (LooksLikeFileSystem(lower))
            return MakeResult(TaskLane.FileSystem, 0.93, "Input references file or folder operations.");

        // ── Compare ──────────────────────────────────────────────────
        if (LooksLikeCompare(lower))
            return MakeResult(TaskLane.Compare, 0.92, "Input asks for a comparison or evaluation.");

        // ── Guide ────────────────────────────────────────────────────
        if (LooksLikeGuide(lower))
            return MakeResult(TaskLane.Guide, 0.92, "Input requests step-by-step guidance.");

        // ── Lookup ───────────────────────────────────────────────────
        if (LooksLikeLookup(lower))
            return MakeResult(TaskLane.Lookup, 0.91, "Input asks for a real-world fact or current information.");

        // ── Explain ──────────────────────────────────────────────────
        if (LooksLikeExplain(lower))
            return MakeResult(TaskLane.Explain, 0.91, "Input asks for an explanation or summary.");

        // ── Conversation (greeting/small-talk) ───────────────────────
        if (LooksLikeGreeting(lower))
            return MakeResult(TaskLane.Conversation, 0.94, "Input is a greeting or small talk.");

        return null;
    }

    // ── Heuristic detectors ──────────────────────────────────────────

    internal static bool LooksLikeDeterministic(string lower)
    {
        // Percentage calculations: "what's 17% of 340"
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\d+(\.\d+)?\s*%\s*of\s*\d"))
            return true;

        // Arithmetic expressions: "15 + 23", "100 * 3.5", "250 / 5"
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\d+\s*[+\-*/]\s*\d+"))
            return true;

        // Unit/temperature conversions: "350f in c", "5 miles to km", "100c to f"
        // Mirrors the DeterministicUtilityEngine's conversion pattern.
        if (System.Text.RegularExpressions.Regex.IsMatch(lower,
            @"\d+\s*(?:°?\s*)?(?:fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?|feet|ft|meters?|m)\s+(?:to|in|into)\s*(?:°?\s*)?(?:fahrenheit|celsius|kelvin|f|c|k|lbs?|pounds?|kg|kilograms?|oz|ounces?|grams?|g|miles?|mi|km|kilometers?|inches?|in|cm|centimeters?|feet|ft|meters?|m)\b"))
            return true;

        // "convert X to Y" with a number
        if (lower.Contains("convert", StringComparison.Ordinal) &&
            System.Text.RegularExpressions.Regex.IsMatch(lower, @"\d+.*\bto\b"))
            return true;

        // "how many X in Y"
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"how many\b.*\bin\b"))
            return true;

        return false;
    }

    internal static bool LooksLikeFileSystem(string lower)
    {
        var fileKeywords = new[]
        {
            "move all my", "move my files", "copy my files", "organize my files",
            "rename the file", "delete the file", "create a folder",
            "move all", "copy all", "move files", "copy files",
            "move pdfs", "move documents", "move photos",
            "to a documents folder", "to a folder", "into a folder"
        };

        return fileKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    internal static bool LooksLikeCompare(string lower)
    {
        if (lower.Contains("compare", StringComparison.Ordinal))
            return true;
        if (lower.Contains("which is better", StringComparison.Ordinal))
            return true;
        if (lower.Contains("good deal", StringComparison.Ordinal))
            return true;
        if (lower.Contains("is this a good", StringComparison.Ordinal))
            return true;
        if (lower.Contains(" vs ", StringComparison.Ordinal) ||
            lower.Contains(" versus ", StringComparison.Ordinal))
            return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"\bor\b.*\bwhich\b|\bwhich\b.*\bor\b"))
            return true;

        return false;
    }

    internal static bool LooksLikeGuide(string lower)
    {
        var guideKeywords = new[]
        {
            "walk me through", "help me do", "what do i click",
            "step by step", "how do i", "guide me", "show me how",
            "help me fix", "help me with", "help me set up",
            "walk me", "how can i fix"
        };

        return guideKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    internal static bool LooksLikeLookup(string lower)
    {
        // "when does X open/close"
        if (System.Text.RegularExpressions.Regex.IsMatch(lower, @"when does\b.*\b(open|close|start|end)\b"))
            return true;
        // "is X in stock"
        if (lower.Contains("in stock", StringComparison.Ordinal))
            return true;
        // "what is the price of"
        if (lower.Contains("price of", StringComparison.Ordinal) ||
            lower.Contains("how much does", StringComparison.Ordinal) ||
            lower.Contains("how much is", StringComparison.Ordinal))
            return true;
        // "what time does"
        if (lower.Contains("what time does", StringComparison.Ordinal) ||
            lower.Contains("what time is", StringComparison.Ordinal))
            return true;
        // "does X close tonight"
        if (lower.Contains("close tonight", StringComparison.Ordinal) ||
            lower.Contains("open today", StringComparison.Ordinal) ||
            lower.Contains("open right now", StringComparison.Ordinal))
            return true;

        return false;
    }

    internal static bool LooksLikeExplain(string lower)
    {
        var explainKeywords = new[]
        {
            "what is this", "what is a ", "what is an ", "what are ",
            "summarize", "describe", "explain", "is this legit",
            "what does this mean", "tell me about",
            "summarize this", "what is the", "what's this"
        };

        return explainKeywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    internal static bool LooksLikeGreeting(string lower)
    {
        var greetings = new[]
        {
            "hey", "hello", "hi", "good morning", "good afternoon",
            "good evening", "how are you", "what's up", "howdy", "sup"
        };

        // Only match if the message is short (typical greetings)
        if (lower.Length > 40)
            return false;

        return greetings.Any(g => lower.Contains(g, StringComparison.Ordinal));
    }

    // ── LLM fallback path ────────────────────────────────────────────

    private async Task<LaneRoutingResult> ClassifyWithLlmAsync(
        string userInput,
        ConversationContext ctx,
        CancellationToken cancellationToken)
    {
        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(LaneRouterPrompts.ClassificationSystemPrompt),
                ChatMessage.User(LaneRouterPrompts.BuildUserPrompt(userInput, ctx))
            };

            var response = await _llm.ChatAsync(
                messages, tools: null, ClassifyMaxTokens, cancellationToken);

            return ParseLlmResponse(response.Content);
        }
        catch
        {
            // LLM failure → safe fallback
            return MakeResult(TaskLane.Conversation, 0.5,
                "LLM classification failed; defaulting to Conversation.");
        }
    }

    /// <summary>
    /// Parses the strict JSON response from the LLM.
    /// Returns a <see cref="TaskLane.Conversation"/> fallback if the JSON is
    /// invalid or confidence is below <see cref="MinConfidenceThreshold"/>.
    /// </summary>
    internal static LaneRoutingResult ParseLlmResponse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return MakeResult(TaskLane.Conversation, 0.5,
                "Empty LLM response; defaulting to Conversation.");

        try
        {
            // Strip any markdown code fences the model may have wrapped around the JSON.
            var json = content.Trim();
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var laneStr = root.TryGetProperty("lane", out var laneProp)
                ? laneProp.GetString() ?? ""
                : "";

            var confidence = root.TryGetProperty("confidence", out var confProp)
                ? confProp.GetDouble()
                : 0.0;

            var rationale = root.TryGetProperty("rationale", out var ratProp)
                ? ratProp.GetString() ?? ""
                : "";

            if (!TryParseLane(laneStr, out var lane))
                return MakeResult(TaskLane.Conversation, 0.5,
                    $"Unrecognised lane '{laneStr}'; defaulting to Conversation.");

            if (confidence < MinConfidenceThreshold)
                return MakeResult(TaskLane.Conversation, confidence,
                    $"Low confidence ({confidence:F2}); defaulting to Conversation. Original: {rationale}");

            return MakeResult(lane, confidence, rationale);
        }
        catch (JsonException)
        {
            return MakeResult(TaskLane.Conversation, 0.5,
                "Invalid JSON from LLM; defaulting to Conversation.");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static bool TryParseLane(string value, out TaskLane lane)
    {
        return Enum.TryParse(value, ignoreCase: true, out lane) &&
               Enum.IsDefined(lane);
    }

    internal static LaneRoutingResult MakeResult(TaskLane lane, double confidence, string rationale)
        => new()
        {
            Lane = lane,
            Confidence = confidence,
            Rationale = rationale
        };
}
