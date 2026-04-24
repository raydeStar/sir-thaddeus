using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that detects prompts whose answer <b>structurally</b>
/// depends on current or verifiable data (existence checks, recency
/// questions, current-price lookups, "who is the current X"), and forces
/// the first LLM round to call <c>web_search</c> via <c>tool_choice</c>.
///
/// <para>This is the first half of the "balance" the runtime wants: reach
/// for a search when the model's training data plainly can't be trusted,
/// stay out of the way the rest of the time. The regex gate is biased
/// toward <b>not</b> firing — it requires BOTH a factual question shape
/// AND a freshness marker (or a versioned entity). Casual chat, "does this
/// work", opinion prompts, and generative tasks pass through untouched.</para>
///
/// <para>Place this step <b>after</b> <c>FootmanRouterStep</c> (so the tool
/// list is already narrowed) but <b>before</b> <c>ToolLoopStep</c> (which
/// consumes <see cref="TurnContext.ForcedTool"/> on round 0).</para>
///
/// <para>No-op when <c>web_search</c> isn't present in
/// <see cref="TurnContext.ToolDefs"/> (e.g. tools disabled, permission
/// blocked). Under no circumstances does this step call the LLM — it's
/// pure pattern-match and context mutation.</para>
/// </summary>
public sealed class FreshnessRouterStep : ITurnStep
{
    private const string WebSearchToolName = "web_search";
    private const string WeatherGeocodeToolName = "weather_geocode";

    // Weather-shaped asks: "weather in Seattle", "forecast for Tokyo",
    // "is it raining", "how cold", "use weather tools for X". When the
    // tool list includes weather_geocode, prefer forcing it — otherwise
    // small models consistently reach for web_search even though the
    // native tool gives structured, real-time data.
    //
    // Intentionally narrow: we require a weather/forecast/met-term plus
    // *some* question or imperative shape. "I checked the weather" as
    // prose doesn't match.
    private static readonly Regex WeatherIntentPattern = new(
        @"\b(weather|forecast|temperature|humidity|wind|precipitation|" +
        @"rain(?:ing|fall)?|snow(?:ing|fall)?|sunny|cloudy|overcast|" +
        @"heat\s*(?:wave|index)|cold\s*front|storm(?:y)?|" +
        @"met(?:eorological)?\s+conditions?|" +
        // Gerund / sensory phrasing: "how cold is it", "is it hot",
        // "how warm", "how humid". These are conversational weather
        // asks without the nominalized term "weather".
        @"how\s+(?:cold|hot|warm|humid|chilly|breezy|windy|muggy)|" +
        @"is\s+it\s+(?:cold|hot|warm|humid|chilly|breezy|windy|muggy|raining|snowing))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Imperative-tool phrasing: "use web_search for X", "try file_read",
    // "run tool_ping and...". When the user literally names a tool and
    // asks for it, small models routinely PREEMPT the call — fabricating
    // a plausible error message ("I hit a timeout") instead of actually
    // invoking the tool. Forcing tool_choice removes that shortcut.
    //
    // Matches `(use|try|run|call|invoke) <tool_identifier>` where the
    // identifier looks like a typical MCP tool name (snake_case or dot-
    // separated). Lets the prompt use backticks, quotes, or bare tokens.
    private static readonly Regex ImperativeToolPattern = new(
        @"\b(?:use|try|run|call|invoke|perform|execute)\s+" +
        @"(?:the\s+)?" +
        @"(?:""|`|')?" +
        @"(?<tool>[a-z][a-z0-9]*(?:[_\.][a-z0-9]+){0,4})" +
        @"(?:""|`|')?" +
        @"(?:\s+(?:tool|function|command|action))?" +
        @"\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Gate 1: the prompt must look like a factual question ────────────
    // "is there", "does X exist", "when did", "what year", "how much".
    // Deliberately narrower than FactualQuestionDetector — we only want
    // clear existence / recency / pricing shapes here, NOT every wh-word.
    private static readonly Regex ExistencePattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|so[,!\s]+|please[,!\s]+|could\s+you[,!\s]+)*" +
        @"(?:does\s+(?!this|that|it\s+work)\S+(?:\s+\S+){0,6}\s+exist|" +
        @"is\s+(?!this|that|it\s+working|it\s+okay|it\s+okay)\S+(?:\s+\S+){0,6}\s+(?:real|released|available|still\s+around)|" +
        @"was\s+\S+(?:\s+\S+){0,6}\s+(?:released|launched|announced)|" +
        @"has\s+\S+(?:\s+\S+){0,6}\s+(?:come\s+out|been\s+released|launched|shipped|been\s+announced)|" +
        @"is\s+there\s+an?\s+\S+|" +
        @"did\s+\S+(?:\s+\S+){0,6}\s+(?:ever\s+(?:release|ship|exist|come\s+out)))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RecencyPattern = new(
        @"^\s*(?:hey[,!\s]+|hi[,!\s]+|so[,!\s]+|please[,!\s]+|could\s+you[,!\s]+)*" +
        @"(?:what(?:'s|\s+is)\s+(?:the\s+)?(?:latest|current|newest|most\s+recent|up[-\s]?to[-\s]?date)|" +
        @"who(?:'s|\s+is)\s+the\s+(?:current|present|latest|new|sitting)|" +
        @"what\s+year\s+(?:did|was|is)|" +
        @"when\s+did\s+\S+(?:\s+\S+){0,6}\s+(?:come\s+out|release|launch|ship|die|win)|" +
        @"how\s+(?:much\s+does|much\s+is|much\s+costs?|many)\s+\S+|" +
        @"what(?:'s|\s+is)\s+the\s+price\s+of)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Gate 2: suppress obvious false positives ─────────────────────────
    // Self-referential introspection, opinion prompts, tutorial requests.
    private static readonly Regex SuppressPattern = new(
        @"\b(" +
        @"does\s+(?:this|that|it)\s+(?:work|make\s+sense|look|sound|seem)|" +
        @"is\s+(?:this|that|it)\s+(?:right|correct|okay|ok|fine|working|broken)|" +
        @"what\s+do\s+you\s+think|" +
        @"what(?:'s|\s+is)\s+(?:up|your\s+(?:favorite|opinion|take|name))|" +
        @"how\s+does\s+\S+\s+work\b|" +  // "how does TCP work" = tutorial, stable
        @"how\s+(?:much\s+wood|many\s+sides)" + // trivial jokes
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string Name => "FreshnessRouter";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Already routed by an earlier step? Respect the earlier decision.
        if (!string.IsNullOrWhiteSpace(context.ForcedTool))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var userText = context.UserText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        // ── Imperative tool invocation ───────────────────────────────────
        // "Use web_search for X" / "Try file_read" / "Run tool_ping".
        // When the user literally names a tool, honor it — small models
        // preempt and fabricate results if we don't force the call.
        var imperative = ImperativeToolPattern.Match(userText);
        if (imperative.Success)
        {
            var requestedTool = imperative.Groups["tool"].Value;
            if (HasTool(context, requestedTool))
            {
                return Task.FromResult<StepResult>(new StepResult.Continue(context with
                {
                    ForcedTool = requestedTool,
                }));
            }
        }

        // ── Weather intent ───────────────────────────────────────────────
        // Prefer weather_geocode when the prompt is weather-shaped AND
        // the tool is available. Without this, the 2B model reaches for
        // web_search even when native weather tools are exposed, failing
        // structural tests like smoke_weather_forecast. weather_geocode
        // is always step 1 — it resolves the place to coordinates; the
        // tool loop naturally chains to weather_forecast from there.
        if (HasTool(context, WeatherGeocodeToolName) &&
            WeatherIntentPattern.IsMatch(userText))
        {
            return Task.FromResult<StepResult>(new StepResult.Continue(context with
            {
                ForcedTool = WeatherGeocodeToolName,
            }));
        }

        // ── Freshness / existence / recency heuristics ───────────────────
        // No web_search in the narrowed tool list → nothing to force.
        // Falls through to a regular LLM answer (best-effort from memory).
        if (!HasTool(context, WebSearchToolName))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        if (SuppressPattern.IsMatch(userText))
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        var matched = ExistencePattern.IsMatch(userText) || RecencyPattern.IsMatch(userText);
        if (!matched)
            return Task.FromResult<StepResult>(new StepResult.Continue(context));

        return Task.FromResult<StepResult>(new StepResult.Continue(context with
        {
            ForcedTool = WebSearchToolName,
        }));
    }

    private static bool HasTool(TurnContext context, string toolName)
    {
        for (var i = 0; i < context.ToolDefs.Count; i++)
        {
            var def = context.ToolDefs[i];
            if (string.Equals(def.Function?.Name, toolName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
