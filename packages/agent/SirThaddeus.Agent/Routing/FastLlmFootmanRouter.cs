using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Fast LLM-based Footman router. Sends a minimal prompt to a small
/// gatekeeper model and parses strict JSON output into a
/// <see cref="RoutingDecision"/>. On any failure (timeout, parse error,
/// low confidence, explicit abstain), falls back conservatively.
/// </summary>
public sealed partial class FastLlmFootmanRouter : IFootmanRouter
{
    private readonly ILlmClient _llm;
    private readonly Action<string, string>? _logEvent;
    private readonly TimeSpan _timeout;

    /// <summary>Max tokens for the Footman response — keep it tight.</summary>
    private const int MaxResponseTokens = 120;

    public FastLlmFootmanRouter(
        ILlmClient llm,
        Action<string, string>? logEvent = null,
        TimeSpan? timeout = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logEvent = logEvent;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(3000);
    }

    public async Task<RoutingDecision> RouteAsync(
        string userMessage,
        RoutingFeatures features,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var sw = Stopwatch.StartNew();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_timeout);

            var messages = BuildPrompt(userMessage, features, requestId);

            var response = await _llm.ChatAsync(
                messages, tools: null, MaxResponseTokens, cts.Token);

            sw.Stop();
            var raw = response.Content ?? "";

            _logEvent?.Invoke("FOOTMAN_RAW_RESPONSE",
                $"requestId={requestId} elapsed={sw.ElapsedMilliseconds}ms tokens={response.Usage?.CompletionTokens ?? -1} raw={Truncate(raw, 300)}");

            var decision = ParseAndValidate(raw, requestId);

            _logEvent?.Invoke("FOOTMAN_DECISION",
                $"requestId={requestId} state={decision.NextState} " +
                $"policy={decision.ContextPolicy} confidence={decision.Confidence:F2} " +
                $"abstain={decision.Abstain} reason={decision.ReasonCode} " +
                $"authoritative={decision.IsAuthoritative}");

            return decision;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _logEvent?.Invoke("FOOTMAN_TIMEOUT",
                $"requestId={requestId} elapsed={sw.ElapsedMilliseconds}ms — falling back");

            return RoutingDecision.CreateFallback(requestId, "footman_timeout");
        }
        catch (OperationCanceledException)
        {
            throw; // Propagate real cancellation
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logEvent?.Invoke("FOOTMAN_ERROR",
                $"requestId={requestId} elapsed={sw.ElapsedMilliseconds}ms error={ex.Message} — falling back");

            return RoutingDecision.CreateFallback(requestId, "footman_error");
        }
    }

    // ── Prompt Construction ──────────────────────────────────────────

    private static IReadOnlyList<ChatMessage> BuildPrompt(
        string userMessage, RoutingFeatures features, string requestId)
    {
        var systemPrompt = BuildSystemPrompt(requestId, features);
        return
        [
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userMessage)
        ];
    }

    private static string BuildSystemPrompt(string requestId, RoutingFeatures features)
    {
        return
$@"You are Footman, a fast routing classifier. Given a user message and pre-computed signals, output ONLY a single JSON object. No markdown, no explanation, no text outside the JSON.

Schema (all fields required):
{{
  ""schemaVersion"": 1,
  ""requestId"": ""{requestId}"",
  ""nextState"": ""<state>"",
  ""contextPolicy"": ""<policy>"",
  ""confidence"": <0.00-1.00>,
  ""abstain"": <true|false>,
  ""reasonCode"": ""<code>""
}}

Valid nextState values: Chat, SearchFact, SearchNews, SearchDeepDive, ScreenObserve, FileTask, SystemTask, MemoryWrite, MemoryRead, BrowseOnce, UtilityDeterministic, Fallback
Valid contextPolicy values: None, LastAssistantOnly, LastTurns, ChatSessionSnapshot, ScreenSnapshot

Rules:
- If the message is a greeting or casual chat → Chat
- If the message asks for current facts, prices, weather, people, events → SearchFact
- If the message asks for news or headlines → SearchNews
- If the message asks for a deep dive, briefing, or business hours+reviews → SearchDeepDive
- If the message asks to look at screen, screenshot, or observe → ScreenObserve
- If the message asks to read/write files → FileTask
- If the message asks to run a command or launch a program → SystemTask
- If the message asks to remember, store, note, update, or correct information → MemoryWrite
- If the message asks to recall stored information → MemoryRead
- If the message contains a URL or asks to browse/navigate → BrowseOnce
- If the message is a math problem, conversion, time query, or logic puzzle → Chat (no tools needed)
- If the user supplies all necessary facts and asks for an opinion, advice, or judgment (e.g. 'X is 50m away, should I walk or drive?') → Chat (no lookup needed; reason from what was given)
- If uncertain, set abstain=true and nextState=Fallback
- Set confidence honestly. Below 0.60 triggers automatic fallback.

Pre-computed signals for this message:
{features.ToPromptSummary()}";
    }

    // ── Parse + Validate ─────────────────────────────────────────────

    internal RoutingDecision ParseAndValidate(string raw, string requestId)
    {
        var sanitized = SanitizeJson(raw);

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            _logEvent?.Invoke("FOOTMAN_PARSE_FAIL",
                $"requestId={requestId} reason=empty_after_sanitize");
            return RoutingDecision.CreateFallback(requestId, "parse_empty");
        }

        FootmanJsonResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<FootmanJsonResponse>(
                sanitized, s_jsonOptions);
        }
        catch (JsonException ex)
        {
            _logEvent?.Invoke("FOOTMAN_PARSE_FAIL",
                $"requestId={requestId} reason=json_deserialize error={ex.Message} sanitized={Truncate(sanitized, 200)}");
            return RoutingDecision.CreateFallback(requestId, "parse_json_error");
        }

        if (parsed is null)
        {
            _logEvent?.Invoke("FOOTMAN_PARSE_FAIL",
                $"requestId={requestId} reason=null_result");
            return RoutingDecision.CreateFallback(requestId, "parse_null");
        }

        // Validate schema version
        if (parsed.SchemaVersion != 1)
        {
            _logEvent?.Invoke("FOOTMAN_VALIDATION_FAIL",
                $"requestId={requestId} reason=bad_schema_version got={parsed.SchemaVersion}");
            return RoutingDecision.CreateFallback(requestId, "bad_schema_version");
        }

        // Parse nextState
        var nextState = AgentStateMapper.TryParse(parsed.NextState);
        if (nextState is null)
        {
            _logEvent?.Invoke("FOOTMAN_VALIDATION_FAIL",
                $"requestId={requestId} reason=unknown_state got={parsed.NextState}");
            return RoutingDecision.CreateFallback(requestId, "unknown_state");
        }

        // Parse contextPolicy (optional — defaults apply)
        var contextPolicy = ContextPolicyDefaults.TryParse(parsed.ContextPolicy)
                            ?? ContextPolicyDefaults.For(nextState.Value);

        // Clamp confidence
        var confidence = Math.Clamp(parsed.Confidence, 0.0, 1.0);

        var decision = new RoutingDecision
        {
            SchemaVersion = 1,
            RequestId = requestId,
            NextState = nextState.Value,
            ContextPolicy = contextPolicy,
            Confidence = confidence,
            Abstain = parsed.Abstain,
            ReasonCode = parsed.ReasonCode ?? "footman_llm"
        };

        // Auto-fallback on low confidence
        if (confidence < RoutingDecision.ConfidenceThreshold && !parsed.Abstain)
        {
            _logEvent?.Invoke("FOOTMAN_LOW_CONFIDENCE",
                $"requestId={requestId} confidence={confidence:F2} — auto-fallback");
            return decision with
            {
                NextState = AgentState.Fallback,
                Abstain = true,
                ReasonCode = "low_confidence"
            };
        }

        // Honour explicit abstain
        if (parsed.Abstain)
        {
            return decision with
            {
                NextState = AgentState.Fallback,
                ReasonCode = parsed.ReasonCode ?? "footman_abstain"
            };
        }

        return decision;
    }

    // ── JSON Sanitization ────────────────────────────────────────────

    /// <summary>
    /// Extracts the first JSON object from potentially noisy LLM output.
    /// Strips markdown fences, leading prose, trailing text, etc.
    /// </summary>
    internal static string SanitizeJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();

        // Strip markdown code fences
        text = MarkdownFenceRegex().Replace(text, "").Trim();

        // Find first '{' and last '}'
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
            return string.Empty;

        return text[start..(end + 1)];
    }

    [GeneratedRegex(@"```(?:json)?\s*|```", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownFenceRegex();

    // ── Helpers ──────────────────────────────────────────────────────

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // ── Internal DTO for deserialization ─────────────────────────────

    /// <summary>
    /// Raw JSON shape from the Footman LLM. Strings instead of enums so
    /// we can validate and fallback gracefully on bad values.
    /// </summary>
    private sealed class FootmanJsonResponse
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }

        [JsonPropertyName("nextState")]
        public string? NextState { get; set; }

        [JsonPropertyName("contextPolicy")]
        public string? ContextPolicy { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("abstain")]
        public bool Abstain { get; set; }

        [JsonPropertyName("reasonCode")]
        public string? ReasonCode { get; set; }
    }
}
