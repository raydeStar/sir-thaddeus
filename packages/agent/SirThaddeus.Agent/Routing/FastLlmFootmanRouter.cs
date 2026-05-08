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

    /// <summary>Max tokens for the Footman response. The JSON envelope is
    /// ~80-100 chars / ~35 tokens; 64 gives headroom without letting a
    /// chatty model run on.</summary>
    private const int MaxResponseTokens = 64;

    public FastLlmFootmanRouter(
        ILlmClient llm,
        Action<string, string>? logEvent = null,
        TimeSpan? timeout = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _logEvent = logEvent;
        // 8 s tolerates an LM Studio model swap (unload primary, load a
        // small 2B gatekeeper, first-token) on single-GPU setups. Warm
        // subsequent calls still complete in a few hundred ms.
        _timeout = timeout ?? TimeSpan.FromMilliseconds(8000);
    }

    public async Task<RoutingDecision> RouteAsync(
        string userMessage,
        RoutingFeatures features,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var sw = Stopwatch.StartNew();

        // Deterministic fast-path: some prompts are cheap to classify from
        // heuristics alone. Skipping the LLM call here saves the gatekeeper
        // round-trip (often hundreds of ms to seconds on small-model setups)
        // and avoids cold-start swap latency entirely for the common cases.
        // Only fires on conservative, high-confidence signals — a miss here
        // just falls through to the LLM, same as before.
        var deterministic = TryDeterministicRoute(features, requestId);
        if (deterministic is not null)
        {
            _logEvent?.Invoke("FOOTMAN_FAST_PATH",
                $"requestId={requestId} state={deterministic.NextState} reason={deterministic.ReasonCode}");
            return deterministic;
        }

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

    // ── Fast-path classification ──────────────────────────────────────

    /// <summary>
    /// Returns a deterministic routing decision when the user message
    /// matches a high-confidence heuristic (greeting, logic puzzle, explicit
    /// screen/file/system request, etc.). Returning null means "fall through
    /// to the LLM classifier." Every short-circuit is phrased as 'Chat with
    /// no tools' or a specific single-family state, never a full Fallback —
    /// so a heuristic miss can only cost a few tools, not the whole turn.
    /// </summary>
    internal static RoutingDecision? TryDeterministicRoute(RoutingFeatures features, string requestId)
    {
        // Pure social / acknowledgement — no tools ever needed.
        if (features.IsGreeting)
            return RoutingDecision.CreateDeterministic(requestId, AgentState.Chat, "heuristic_greeting");

        // Logic puzzles, riddles, and reasoning traps — tools would only
        // distract. Primary model handles with the logic-puzzle scaffold.
        if (features.IsLogicPuzzle)
            return RoutingDecision.CreateDeterministic(requestId, AgentState.Chat, "heuristic_logic_puzzle");

        if (LooksLikePlainChat(features))
            return RoutingDecision.CreateDeterministic(requestId, AgentState.Chat, "heuristic_chat");

        // ── Single-family short-circuits ────────────────────────────────
        // When the feature extractor returns an UNAMBIGUOUS single bucket,
        // skip the LLM gatekeeper entirely. 2B models often abstain on
        // these which fail-opens the tool list — then the primary model
        // reaches for memory_store_facts on a news query, places_discover
        // on a file task, and so on. A short-circuit here narrows tools
        // to the right family every time without paying a classifier call.
        //
        // Conservatism is the point: if ANY non-matching family flag is
        // also set, we bail and let the LLM gatekeeper decide. Cost is
        // only extra coverage; we don't forge decisions when signals mix.

        if (features.LooksLikeScreenRequest
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeBrowseRequest
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeDeepDive)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.ScreenObserve, "heuristic_screen_request");
        }

        if (features.LooksLikeSystemCommand
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeDeepDive
            && !features.LooksLikeLocalBusiness
            && !features.LooksLikeBrowseRequest)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.SystemTask, "heuristic_system_command");
        }

        if (features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeDeepDive
            && !features.LooksLikeLocalBusiness
            && !features.LooksLikeBrowseRequest)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.FileTask, "heuristic_file_request");
        }

        if (features.LooksLikeBrowseRequest
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.BrowseOnce, "heuristic_browse_request");
        }

        if (features.LooksLikeNewsLookup
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeScreenRequest
            && !features.LooksLikeLocalBusiness)
        {
            // News queries only need WebSearch — narrowing here prevents
            // the model from proactively calling memory_store_facts or
            // places_discover on a "top headlines" prompt.
            return RoutingDecision.CreateDeterministic(requestId, AgentState.SearchNews, "heuristic_news_lookup");
        }

        if (features.LooksLikeLocalBusiness
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeScreenRequest)
        {
            // Local-business queries want deep-dive capabilities (places
            // tools + web_search + browse). SearchDeepDive exposes all of
            // them without opening memory-write or system tools.
            return RoutingDecision.CreateDeterministic(requestId, AgentState.SearchDeepDive, "heuristic_local_business");
        }

        if (features.LooksLikeDeepDive
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeScreenRequest)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.SearchDeepDive, "heuristic_deep_dive");
        }

        if (features.LooksLikeFactLookup
            && !features.LooksLikeNewsLookup
            && !features.LooksLikeDeepDive
            && !features.LooksLikeLocalBusiness
            && !features.LooksLikeFileRequest
            && !features.LooksLikeSystemCommand
            && !features.LooksLikeScreenRequest
            && !features.LooksLikeBrowseRequest)
        {
            return RoutingDecision.CreateDeterministic(requestId, AgentState.SearchFact, "heuristic_fact_lookup");
        }

        return null;
    }

    private static bool LooksLikePlainChat(RoutingFeatures features)
    {
        return !features.LooksLikeFactLookup
               && !features.LooksLikeNewsLookup
               && !features.LooksLikeDeepDive
               && !features.LooksLikeLocalBusiness
               && !features.LooksLikeScreenRequest
               && !features.LooksLikeFileRequest
               && !features.LooksLikeSystemCommand
               && !features.LooksLikeBrowseRequest
               && !features.LooksLikeMemoryWrite
               && !features.LooksLikeWebSearch
               && !features.IsSlashCommand;
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
$@"You are Footman, a fast router. Output ONLY a single JSON object.

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

nextState: Chat | SearchFact | SearchNews | SearchDeepDive | ScreenObserve | FileTask | SystemTask | MemoryWrite | MemoryRead | BrowseOnce | UtilityDeterministic | Fallback
contextPolicy: None | LastAssistantOnly | LastTurns | ChatSessionSnapshot | ScreenSnapshot
reasonCode (pick one, don't invent): heuristic_chat | fact_lookup | news_lookup | deep_dive | screen_request | file_request | system_command | memory_write | memory_read | browse_request | utility_query | opinion_advice | low_confidence | footman_abstain

Routing:
- greeting / casual / asking you to reason from given facts (""X is 50m away, walk or drive?"") → Chat
- current facts, prices, weather, people, events → SearchFact
- news / headlines → SearchNews
- deep dive, briefing, business hours+reviews → SearchDeepDive
- look at screen / screenshot → ScreenObserve
- read/write files → FileTask
- run command / launch program → SystemTask
- remember / save / correct info → MemoryWrite
- recall saved info → MemoryRead
- URL or browse/navigate → BrowseOnce
- math, unit conversion, time, logic puzzle → Chat
- uncertain → abstain=true, nextState=Fallback
Confidence < 0.60 auto-falls back.

Signals: {features.ToPromptSummary()}";
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

        // Parse typed block reason from raw reason code string.
        var rawReasonCode = parsed.ReasonCode ?? "footman_llm";
        var blockReason = FootmanBlockReasonPolicy.Parse(rawReasonCode);

        var decision = new RoutingDecision
        {
            SchemaVersion = 1,
            RequestId = requestId,
            NextState = nextState.Value,
            ContextPolicy = contextPolicy,
            Confidence = confidence,
            Abstain = parsed.Abstain,
            ReasonCode = rawReasonCode,
            BlockReason = blockReason
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
                ReasonCode = "low_confidence",
                BlockReason = FootmanBlockReason.Unknown
            };
        }

        // Honour explicit abstain
        if (parsed.Abstain)
        {
            return decision with
            {
                NextState = AgentState.Fallback,
                ReasonCode = parsed.ReasonCode ?? "footman_abstain",
                BlockReason = blockReason
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
