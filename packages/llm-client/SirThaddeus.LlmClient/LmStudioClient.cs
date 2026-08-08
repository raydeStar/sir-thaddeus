using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SirThaddeus.LlmClient;

/// <summary>
/// LLM client for LM Studio (or any OpenAI-compatible endpoint).
/// Sends chat completion requests with optional tool definitions and
/// parses tool_calls from the response.
/// </summary>
public sealed class LmStudioClient : IConfigurableLlmClient
{
    private static readonly TimeSpan ModelDiscoveryTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<string, LlmEndpointGate> EndpointGates = new(StringComparer.OrdinalIgnoreCase);

    private HttpClient _http;
    private readonly object _optionsGate = new();
    private readonly ILogger<LmStudioClient> _logger;
    private LlmClientOptions _options;
    private readonly JsonSerializerOptions _json;
    private long _promptTokensTotal;
    private long _completionTokensTotal;
    private long _totalTokensTotal;
    private long _requestCount;
    private readonly HashSet<string> _confirmedLoadedModels = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _autoLoadEnabled;
    private LlmEndpointGate _requestGate;
    private volatile bool _warmupCompleted;
    private volatile bool _lastReachable;
    private string? _lastReportedModel;
    private string? _lastError;
    private long _lastRequestDurationMs;
    private long _lastQueueWaitMs;
    private int _lastEstimatedInputTokens;
    private int _lastRequestedOutputTokens;
    private string _lastTaskKind = LlmTaskKind.Chat.ToString();
    private bool _lastRequestWasBackground;
    private DateTimeOffset? _lastWarmupAt;
    private DateTimeOffset? _lastRequestAt;
    private readonly AsyncLocal<LlmTaskKind> _requestTaskKind = new();
    private readonly Func<LlmClientOptions, CodexCliLlmClient> _codexClientFactory;
    private CodexCliLlmClient? _codexCli;

    public LmStudioClient(
        LlmClientOptions options,
        HttpClient? httpClient = null,
        ILogger<LmStudioClient>? logger = null)
        : this(options, httpClient, logger, static configured => new CodexCliLlmClient(configured))
    {
    }

    internal LmStudioClient(
        LlmClientOptions options,
        HttpClient? httpClient,
        ILogger<LmStudioClient>? logger,
        Func<LlmClientOptions, CodexCliLlmClient> codexClientFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<LmStudioClient>.Instance;
        _codexClientFactory = codexClientFactory
            ?? throw new ArgumentNullException(nameof(codexClientFactory));
        _http = httpClient ?? new HttpClient();
        if (!LlmProvider.IsCodexCli(options.Provider))
        {
            _http.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/'));
            ApplyAuthorizationHeader(_http, options.ApiKey);
        }
        else
            _codexCli = _codexClientFactory(options);

        // ── Sir Thaddeus notes: A butler must exhibit patience! ───
        // Local GPUs require time to sweep their VRAM floors. 
        // 120 seconds is too hasty; 300 seconds ensures enterprise stability.
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds));

        // Only auto-load models when using the internal HttpClient.
        // External clients (e.g. test mocks) don't support /v1/models/load.
        _autoLoadEnabled = httpClient is null;
        _requestGate = GetEndpointGate(options);

        _json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true
        };
    }

    /// <summary>
    /// Applies updated transport/model settings at runtime.
    /// Creates a fresh HttpClient because .NET forbids changing
    /// BaseAddress after the first request has been sent.
    /// </summary>
    public void UpdateOptions(LlmClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_optionsGate)
        {
            _options = options;
            // A provider-reported context window belongs to the exact loaded
            // configuration that produced it. The same client instance is
            // deliberately reused across settings/model changes, so carrying
            // this observation forward would let an old 8K model permanently
            // clamp a newly selected 32K model (or let the reverse overrun).
            Interlocked.Exchange(ref _observedContextWindowTokens, 0);

            if (LlmProvider.IsCodexCli(options.Provider))
            {
                var oldCodex = _codexCli;
                _codexCli = _codexClientFactory(options);
                _requestGate = GetEndpointGate(options);
                _confirmedLoadedModels.Clear();
                Task.Run(() =>
                {
                    try { oldCodex?.Dispose(); }
                    catch { /* best effort */ }
                });
                return;
            }

            var previousCodex = _codexCli;
            _codexCli = null;
            Task.Run(() =>
            {
                try { previousCodex?.Dispose(); }
                catch { /* best effort */ }
            });

            var targetBase = options.BaseUrl.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(targetBase))
            {
                // HttpClient.BaseAddress is immutable after first use.
                // Replace the entire client to avoid InvalidOperationException.
                var oldClient = _http;
                _http = new HttpClient
                {
                    BaseAddress = new Uri(targetBase),
                    Timeout = TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds))
                };
                ApplyAuthorizationHeader(_http, options.ApiKey);
                _requestGate = GetEndpointGate(options);
                _confirmedLoadedModels.Clear();

                // Dispose the old client on a background thread to avoid
                // blocking if a request is in flight.
                Task.Run(() =>
                {
                    try { oldClient.Dispose(); }
                    catch { /* best effort */ }
                });
            }
        }
    }

    private static void ApplyAuthorizationHeader(HttpClient client, string? apiKey)
    {
        client.DefaultRequestHeaders.Authorization = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    /// <inheritdoc />
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride: null, forcedToolName: null, cancellationToken);
    }

    /// <summary>
    /// Chat with an explicit max_tokens cap. Useful for intent-specific
    /// calls where the orchestrator knows the expected output length
    /// (e.g., casual chat = short, web summary = medium).
    /// </summary>
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride, forcedToolName: null, cancellationToken);
    }

    /// <summary>
    /// Chat with a forced <c>tool_choice</c> — the model's next action must
    /// be a call to <paramref name="forcedToolName"/>. Used by freshness /
    /// existence routing so small models can't hallucinate their way past a
    /// structurally-required lookup. Passing <c>null</c> is equivalent to
    /// the tool-less overload.
    /// </summary>
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        string? forcedToolName,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride: null, forcedToolName, cancellationToken);
    }

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        string? forcedToolName,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(messages, tools, maxTokensOverride, forcedToolName, cancellationToken);
    }

    /// <summary>
    /// Chat with an explicit max_tokens cap and a per-call sampling temperature
    /// that overrides the configured temperature for this request only.
    /// </summary>
    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        double temperatureOverride,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(
            messages,
            tools,
            maxTokensOverride,
            forcedToolName: null,
            new LlmRequestContext { TemperatureOverride = temperatureOverride },
            cancellationToken);
    }

    private async Task<LlmResponse> ChatCoreAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        string? forcedToolName,
        CancellationToken cancellationToken)
    {
        return await ChatCoreAsync(
            messages,
            tools,
            maxTokensOverride,
            forcedToolName,
            new LlmRequestContext(),
            cancellationToken);
    }

    public async Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        string? forcedToolName,
        LlmRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        return await ChatCoreAsync(
            messages,
            tools,
            maxTokensOverride,
            forcedToolName,
            requestContext,
            cancellationToken);
    }

    private async Task<LlmResponse> ChatCoreAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        string? forcedToolName,
        LlmRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        requestContext ??= new LlmRequestContext();

        CodexCliLlmClient? codexCli;
        LlmClientOptions options;
        lock (_optionsGate)
        {
            codexCli = _codexCli;
            options = _options;
        }

        if (codexCli is not null)
        {
            return await ChatCodexWithTelemetryAsync(
                codexCli,
                options,
                messages,
                tools,
                maxTokensOverride,
                forcedToolName,
                requestContext,
                cancellationToken).ConfigureAwait(false);
        }

        var providerRequestId = "llm_" + Guid.NewGuid().ToString("N")[..12];
        var routingCorrelationId = Activity.Current?.GetBaggageItem("routing_correlation_id") ?? string.Empty;
        var threadId = Activity.Current?.GetBaggageItem("thread_id") ?? string.Empty;
        var turnId = Activity.Current?.GetBaggageItem("turn_id") ?? string.Empty;
        _logger.LogInformation(
            "llm.queue_enter providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} task={TaskKind} background={Background}",
            providerRequestId,
            routingCorrelationId,
            threadId,
            turnId,
            requestContext.TaskKind,
            requestContext.Priority == LlmRequestPriority.Background);

        var queuedAt = Stopwatch.GetTimestamp();
        _requestGate.IncrementQueued();
        try
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requestGate.DecrementQueued();
        }

        var queueWaitMs = Stopwatch.GetElapsedTime(queuedAt).TotalMilliseconds;
        _requestGate.IncrementActive();
        var requestStarted = Stopwatch.GetTimestamp();
        var requestStartedAt = DateTimeOffset.UtcNow;
        _logger.LogInformation(
            "llm.request_started providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} queueWaitMs={QueueWaitMs} firstTokenObservable={FirstTokenObservable}",
            providerRequestId,
            routingCorrelationId,
            threadId,
            turnId,
            queueWaitMs,
            false);

        IReadOnlyList<ChatMessage> budgetedMessages = messages;
        try
        {
            var promptPreparationStarted = Stopwatch.GetTimestamp();
            budgetedMessages = ApplyPromptBudget(messages, tools, requestContext);
            var estimatedTokens = EstimateTokens(budgetedMessages);
            // Telemetry only — the request that actually goes out is budgeted
            // again in ChatCoreLegacyAsync, which owns the final number.
            var requestedOutputTokens = ResolveOutputTokenBudget(
                budgetedMessages, tools, maxTokensOverride, GetOptionsSnapshot());
            if (IsLatencyTracingEnabled())
            {
                var promptPreparationMs = Stopwatch.GetElapsedTime(promptPreparationStarted).TotalMilliseconds;
                _logger.LogInformation(
                    "llm.prompt_prepared providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} messages={Messages} tools={Tools} estimatedInputTokens={InputTokens} durationMs={DurationMs}",
                    providerRequestId,
                    routingCorrelationId,
                    threadId,
                    turnId,
                    budgetedMessages.Count,
                    tools?.Count ?? 0,
                    estimatedTokens,
                    promptPreparationMs);
            }
            TrackRequestStart(requestContext, estimatedTokens, requestedOutputTokens, queueWaitMs, requestStartedAt);

            using var requestCts = CreateRequestCancellationTokenSource(cancellationToken);
            _requestTaskKind.Value = requestContext.TaskKind;
            var response = await ChatCoreLegacyAsync(
                    budgetedMessages,
                    tools,
                    maxTokensOverride,
                    forcedToolName,
                    requestContext.TemperatureOverride,
                    requestCts.Token)
                .ConfigureAwait(false);

            _lastReachable = true;
            _lastError = null;
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastReachable = false;
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            _lastRequestDurationMs = (long)Math.Round(Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
            _requestGate.DecrementActive();
            _logger.LogInformation(
                "llm.request_completed providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} task={TaskKind} background={Background} model={Model} estimatedInputTokens={InputTokens} requestedOutputTokens={OutputTokens} queueWaitMs={QueueWaitMs} durationMs={DurationMs} firstTokenObservable={FirstTokenObservable}",
                providerRequestId,
                routingCorrelationId,
                threadId,
                turnId,
                _lastTaskKind,
                _lastRequestWasBackground,
                GetOptionsSnapshot().Model,
                _lastEstimatedInputTokens,
                _lastRequestedOutputTokens,
                _lastQueueWaitMs,
                _lastRequestDurationMs,
                false);
        }
    }

    private async Task<LlmResponse> ChatCodexWithTelemetryAsync(
        CodexCliLlmClient codexCli,
        LlmClientOptions options,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        string? forcedToolName,
        LlmRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var providerRequestId = "llm_" + Guid.NewGuid().ToString("N")[..12];
        var routingCorrelationId = Activity.Current?.GetBaggageItem("routing_correlation_id") ?? string.Empty;
        var threadId = Activity.Current?.GetBaggageItem("thread_id") ?? string.Empty;
        var turnId = Activity.Current?.GetBaggageItem("turn_id") ?? string.Empty;
        var estimatedTokens = EstimateTokens(messages);
        var requestedOutputTokens = options.EffectiveMaxTokens(maxTokensOverride);
        var requestStarted = Stopwatch.GetTimestamp();
        var requestStartedAt = DateTimeOffset.UtcNow;

        _logger.LogInformation(
            "llm.queue_enter providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} task={TaskKind} background={Background}",
            providerRequestId,
            routingCorrelationId,
            threadId,
            turnId,
            requestContext.TaskKind,
            requestContext.Priority == LlmRequestPriority.Background);
        _logger.LogInformation(
            "llm.request_started providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} queueWaitMs={QueueWaitMs} firstTokenObservable={FirstTokenObservable}",
            providerRequestId,
            routingCorrelationId,
            threadId,
            turnId,
            0,
            false);
        TrackRequestStart(requestContext, estimatedTokens, requestedOutputTokens, 0, requestStartedAt);

        try
        {
            using var requestCts = CreateRequestCancellationTokenSource(cancellationToken);
            var response = await codexCli.ChatAsync(
                    messages,
                    tools,
                    requestedOutputTokens,
                    forcedToolName,
                    requestCts.Token)
                .ConfigureAwait(false);

            Interlocked.Increment(ref _requestCount);
            TrackUsage(response.Usage);
            if (response.Usage is { PromptTokens: > 0 } usage)
                _lastEstimatedInputTokens = usage.PromptTokens;
            _lastReachable = true;
            _lastError = null;
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _lastReachable = false;
            _lastError = ex.Message;
            throw;
        }
        finally
        {
            _lastRequestDurationMs = (long)Math.Round(Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
            _logger.LogInformation(
                "llm.request_completed providerRequestId={ProviderRequestId} correlationId={CorrelationId} threadId={ThreadId} turnId={TurnId} task={TaskKind} background={Background} model={Model} estimatedInputTokens={InputTokens} requestedOutputTokens={OutputTokens} queueWaitMs={QueueWaitMs} durationMs={DurationMs} firstTokenObservable={FirstTokenObservable}",
                providerRequestId,
                routingCorrelationId,
                threadId,
                turnId,
                _lastTaskKind,
                _lastRequestWasBackground,
                options.Model,
                _lastEstimatedInputTokens,
                _lastRequestedOutputTokens,
                _lastQueueWaitMs,
                _lastRequestDurationMs,
                false);
        }
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<LlmResponse> ChatCoreLegacyAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        string? forcedToolName,
        double? temperatureOverride,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Ensure the configured model is loaded in LM Studio before
        // sending the request. This is a no-op after the first call.
        await EnsureModelLoadedAsync(cancellationToken);

        var requestMessages = tools is { Count: > 0 }
            ? messages
            : NormalizeMessagesForPlainChat(messages);
        requestMessages = ApplyPromptBudget(
            requestMessages,
            tools,
            new LlmRequestContext { TaskKind = _requestTaskKind.Value });
        var requestedOutputTokens = ResolveOutputTokenBudget(
            requestMessages, tools, maxTokensOverride, GetOptionsSnapshot());

        // ── Attempt 1: full request with stop + repetition_penalty ───
        var body = BuildRequestBody(requestMessages, tools, requestedOutputTokens, forcedToolName, temperatureOverride, includeExtras: true);

        System.Threading.Interlocked.Increment(ref _requestCount);
        var response = await _http.PostAsJsonAsync(
            NormalizePath(GetOptionsSnapshot().ChatCompletionPath), body, _json, cancellationToken);

        if (response.IsSuccessStatusCode)
            return await ParseResponse(response, tools, requestedOutputTokens, cancellationToken);

        var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

        // ── Self-healing: context overflow → learn the real window, re-budget ─
        // The server just told us its actual n_ctx, which beats anything in
        // settings (a model can be loaded at a different size than we asked
        // for). Record it, re-budget against it, and retry once. Without this a
        // single stale context number in settings fails every tool-using turn.
        if ((int)response.StatusCode == 400 &&
            TryParseContextOverflow(errorBody, out var observedContextWindow))
        {
            var isNews = LearnContextWindowTokens(observedContextWindow);
            _logger.LogWarning(
                "llm.context_window_observed contextWindowTokens={ContextWindowTokens} changed={Changed} " +
                "configuredContextWindowTokens={ConfiguredContextWindowTokens}",
                observedContextWindow,
                isNews,
                GetOptionsSnapshot().ContextWindowTokens);

            var rebudgetedMessages = ApplyPromptBudget(
                requestMessages,
                tools,
                new LlmRequestContext { TaskKind = _requestTaskKind.Value });
            var rebudgetedOutputTokens = ResolveOutputTokenBudget(
                rebudgetedMessages, tools, maxTokensOverride, GetOptionsSnapshot());

            // When the prompt alone can't fit, no completion budget rescues the
            // request and retrying is theatre. Report the shortfall in the terms
            // that let someone act on it — raw "n_keep >= n_ctx" tells a user
            // nothing about which part of their setup is oversized.
            var promptTokens = EstimateTokens(rebudgetedMessages);
            var toolTokens = EstimateToolTokens(tools);
            if (promptTokens + toolTokens + MinOutputTokens > observedContextWindow)
            {
                throw new HttpRequestException(
                    BuildContextExhaustedMessage(
                        observedContextWindow, promptTokens, toolTokens, tools?.Count ?? 0),
                    inner: null,
                    statusCode: response.StatusCode);
            }

            // Only retry if re-budgeting actually changed the request; retrying
            // an identical body would just burn another round-trip.
            if (rebudgetedOutputTokens < requestedOutputTokens ||
                rebudgetedMessages.Count < requestMessages.Count)
            {
                // These become the effective request values for any later
                // compatibility fallback as well. Otherwise a retry that then
                // hits the regex fallback would accidentally resend the
                // original over-budget body.
                requestMessages = rebudgetedMessages;
                requestedOutputTokens = rebudgetedOutputTokens;
                var rebudgeted = BuildRequestBody(
                    requestMessages, tools, requestedOutputTokens, forcedToolName, temperatureOverride,
                    includeExtras: true);

                System.Threading.Interlocked.Increment(ref _requestCount);
                response = await _http.PostAsJsonAsync(
                    NormalizePath(GetOptionsSnapshot().ChatCompletionPath), rebudgeted, _json, cancellationToken);

                if (response.IsSuccessStatusCode)
                    return await ParseResponse(response, tools, requestedOutputTokens, cancellationToken);

                errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            }
        }

        // ── Self-healing: regex failure → retry without extras ────────
        // Sir Thaddeus notes: When the magic fizzles, try a simpler spell.
        if ((int)response.StatusCode == 400 &&
            errorBody.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase))
        {
            var bare = BuildRequestBody(requestMessages, tools, requestedOutputTokens, forcedToolName, temperatureOverride, includeExtras: false);

            System.Threading.Interlocked.Increment(ref _requestCount);
            response = await _http.PostAsJsonAsync(
                NormalizePath(GetOptionsSnapshot().ChatCompletionPath), bare, _json, cancellationToken);

            if (response.IsSuccessStatusCode)
                return await ParseResponse(response, tools, requestedOutputTokens, cancellationToken);

            errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // If the bare request still fails, it is highly likely the local model 
            // is not properly instructed for tool schemas. We must inform the user elegantly.
            throw new HttpRequestException(
                $"The local model failed to parse the tool schema. " +
                $"Check that the loaded model is an instruct / tool-calling build. " +
                $"Original LLM error: {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}",
                inner: null,
                statusCode: response.StatusCode);
        }

        var options = GetOptionsSnapshot();

        // Handle LM Studio 500 HTML errors (often means model not loaded or crashed)
        if ((int)response.StatusCode == 500 && errorBody.Contains("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(
                $"The LLM server encountered an internal error. Please verify that the model '{options.Model}' is currently loaded and running in LM Studio.",
                inner: null,
                statusCode: response.StatusCode);
        }

        // Carry the status code on the exception. Callers need to tell "the
        // provider is down" apart from "the provider rejected this request" —
        // they are different problems with different fixes, and a bare message
        // string forces everything into the first bucket.
        throw new HttpRequestException(
            $"LLM returned {(int)response.StatusCode} ({response.ReasonPhrase}): {errorBody}",
            inner: null,
            statusCode: response.StatusCode);
    }

    /// <summary>
    /// Some chat templates (including popular LM Studio defaults) expect
    /// at most one leading system message, followed by strict user/assistant
    /// alternation. When tools are disabled, strip tool scaffolding and
    /// compact role runs so plain-chat requests stay template-safe.
    /// </summary>
    private static IReadOnlyList<ChatMessage> NormalizeMessagesForPlainChat(
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return messages;

        ChatMessage? system = null;
        var turns = new List<ChatMessage>(messages.Count);

        foreach (var message in messages)
        {
            var role = message.Role?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(role))
                continue;

            if (role == "system")
            {
                if (system is null && !string.IsNullOrWhiteSpace(message.Content))
                    system = ChatMessage.System(message.Content!);
                continue;
            }

            if (role == "tool")
                continue;

            if (role == "assistant" &&
                message.ToolCalls is { Count: > 0 } &&
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if ((role == "user" || role == "assistant") &&
                !string.IsNullOrWhiteSpace(message.Content))
            {
                turns.Add(role == "user"
                    ? ChatMessage.User(message.Content!)
                    : ChatMessage.Assistant(message.Content!));
            }
        }

        var alternating = new List<ChatMessage>(turns.Count);
        foreach (var turn in turns)
        {
            if (alternating.Count == 0)
            {
                // Templates usually expect the first conversational turn to be user.
                if (turn.Role == "assistant")
                    continue;

                alternating.Add(turn);
                continue;
            }

            var previous = alternating[^1];
            if (string.Equals(previous.Role, turn.Role, StringComparison.Ordinal))
            {
                var merged = string.Concat(
                    previous.Content?.TrimEnd(),
                    "\n",
                    turn.Content?.TrimStart());

                alternating[^1] = turn.Role == "user"
                    ? ChatMessage.User(merged)
                    : ChatMessage.Assistant(merged);
                continue;
            }

            alternating.Add(turn);
        }

        // Never send an empty message array to the backend.
        if (alternating.Count == 0)
            return system is null
                ? [ChatMessage.User("Hello")]
                : [system, ChatMessage.User("Hello")];

        if (system is null)
            return alternating;

        var normalized = new List<ChatMessage>(alternating.Count + 1) { system };
        normalized.AddRange(alternating);
        return normalized;
    }

    // ─────────────────────────────────────────────────────────────────
    // Request / Response Helpers
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the chat-completions request body.
    /// </summary>
    /// <remarks>
    /// <c>maxTokensFinal</c> is the already-resolved completion budget and is
    /// used verbatim. Callers run it through
    /// <see cref="ResolveOutputTokenBudget"/> first; re-deriving it here would
    /// re-apply the thinking-model boost in
    /// <see cref="LlmClientOptions.EffectiveMaxTokens"/> and undo the context
    /// clamp that keeps the request inside the window.
    /// When <paramref name="includeExtras"/> is false, non-standard parameters
    /// are omitted for maximum model compatibility.
    /// </remarks>
    private Dictionary<string, object> BuildRequestBody(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensFinal,
        string? forcedToolName,
        double? temperatureOverride,
        bool includeExtras)
    {
        var options = GetOptionsSnapshot();
        var routedModel = new ConfiguredLlmModelRouter(options.Model, options.ModelRoutes)
            .GetModelForTask(_requestTaskKind.Value);
        var body = new Dictionary<string, object>
        {
            ["model"] = routedModel,
            ["messages"] = messages,
            ["max_tokens"] = maxTokensFinal,
            ["temperature"] = temperatureOverride ?? options.Temperature,
            ["stream"] = false
        };

        if (includeExtras)
        {
            // Repetition penalty — not part of the OpenAI spec, but
            // supported by llama.cpp / LM Studio for most models.
            if (options.RepetitionPenalty is > 0 and not 1.0)
                body["repetition_penalty"] = options.RepetitionPenalty;

            // Stop sequences — plain-text only (no template tokens).
            if (options.StopSequences is { Length: > 0 })
                body["stop"] = options.StopSequences;
        }

        if (tools is { Count: > 0 })
        {
            // LM Studio / llama.cpp only support the string-form tool_choice
            // values (none / auto / required). The per-function object form
            // that OpenAI ships is rejected with HTTP 400 ("Invalid
            // tool_choice type: 'object'"). When a caller forces a specific
            // tool, advertise only that definition. Exact-configuration
            // certification may select "auto" for providers whose required
            // grammar emits non-native markup; unknown configurations retain
            // "required". Either way, no unrelated tool remains visible.
            var forcedTool = !string.IsNullOrWhiteSpace(forcedToolName)
                ? tools.FirstOrDefault(t =>
                    string.Equals(t.Function?.Name, forcedToolName, StringComparison.Ordinal))
                : null;

            body["tools"] = forcedTool is null ? tools : new[] { forcedTool };

            body["tool_choice"] = forcedTool is null
                ? "auto"
                : options.ForcedToolChoiceMode == ForcedToolChoiceMode.Auto
                    ? "auto"
                    : "required";
        }
        // When tools is null/empty, intentionally omit both fields.
        // Sending tools:[] or tool_choice:"none" can trigger LM Studio's
        // grammar engine to compile an empty/degenerate pattern, which
        // fails with "Failed to process regex" on some models.

        return body;
    }

    /// <summary>
    /// Reads and deserializes a successful chat completion response.
    /// </summary>
    private async Task<LlmResponse> ParseResponse(
        HttpResponseMessage response,
        IReadOnlyList<ToolDefinition>? advertisedTools,
        int requestedOutputTokens,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        var completion = JsonSerializer.Deserialize<CompletionResponse>(raw, _json);
        TrackUsage(completion?.Usage);

        if (completion?.Choices is not { Count: > 0 })
        {
            return new LlmResponse
            {
                IsComplete = true,
                Content = "[No response from model]",
                FinishReason = "error"
            };
        }

        var choice = completion.Choices[0];
        var message = choice.Message;

        var providerToolCallCount = message?.ToolCalls?.Count ?? 0;
        IReadOnlyList<ToolCallRequest>? toolCalls = message?.ToolCalls;
        if (toolCalls is not { Count: > 0 } && message is not null)
        {
            toolCalls = DocumentedToolCallParser.TryParse(
                [message.ReasoningContent, message.Reasoning, message.Content],
                advertisedTools);
        }
        var hasToolCalls = toolCalls is { Count: > 0 };
        var parserOutcome = providerToolCallCount > 0
            ? "native"
            : hasToolCalls
                ? "normalized"
                : "none";

        LogResponseBoundary(
            choice,
            message,
            providerToolCallCount,
            toolCalls?.Count ?? 0,
            parserOutcome,
            completion.Usage,
            requestedOutputTokens);

        // Strip thinking scaffold at the transport layer so callers never
        // see raw <think> blocks regardless of model or path.
        var content = StripThinkBlocks(message?.Content);
        if (hasToolCalls)
            content = DocumentedToolCallParser.RemoveBalancedBlock(content);

        // When the model emitted only thinking in Content alongside tool
        // calls, null out Content so it doesn't leak into history.
        if (hasToolCalls && string.IsNullOrWhiteSpace(content))
            content = null;

        return new LlmResponse
        {
            IsComplete = !hasToolCalls,
            Content = content,
            ReasoningContent = message?.ReasoningContent,
            ToolCalls = toolCalls,
            FinishReason = choice.FinishReason,
            Usage = completion.Usage
        };
    }

    private void LogResponseBoundary(
        CompletionChoice choice,
        ChoiceMessage? message,
        int providerToolCallCount,
        int effectiveToolCallCount,
        string parserOutcome,
        TokenUsage? usage,
        int requestedOutputTokens)
    {
        if (!IsLatencyTracingEnabled())
            return;

        var turnId = Activity.Current?.GetBaggageItem("turn_id") ?? string.Empty;
        var threadId = Activity.Current?.GetBaggageItem("thread_id") ?? string.Empty;
        var contentChars = message?.Content?.Length ?? 0;
        var reasoningChars = (message?.ReasoningContent?.Length ?? 0) +
                             (message?.Reasoning?.Length ?? 0);
        var completionTokens = usage?.CompletionTokens ?? 0;
        var outputLimitReached = string.Equals(
                                     choice.FinishReason,
                                     "length",
                                     StringComparison.OrdinalIgnoreCase) ||
                                 (requestedOutputTokens > 0 &&
                                  completionTokens >= requestedOutputTokens);

        _logger.LogInformation(
            "LLM_RESPONSE_BOUNDARY threadId={ThreadId} turnId={TurnId} " +
            "finish_reason={FinishReason} content_present={ContentPresent} content_chars={ContentChars} " +
            "reasoning_present={ReasoningPresent} reasoning_chars={ReasoningChars} " +
            "provider_tool_calls={ProviderToolCalls} effective_tool_calls={EffectiveToolCalls} " +
            "tool_call_parser_outcome={ParserOutcome} completion_tokens={CompletionTokens} " +
            "requested_output_tokens={RequestedOutputTokens} output_limit_reached={OutputLimitReached}",
            threadId,
            turnId,
            choice.FinishReason ?? "unknown",
            contentChars > 0,
            contentChars,
            reasoningChars > 0,
            reasoningChars,
            providerToolCallCount,
            effectiveToolCallCount,
            parserOutcome,
            completionTokens,
            requestedOutputTokens,
            outputLimitReached);
    }

    public LlmUsageSnapshot GetUsageSnapshot()
    {
        var options = GetOptionsSnapshot();
        var contextWindow = options.ContextWindowTokens > 0
            ? options.ContextWindowTokens
            : 8192;

        return new LlmUsageSnapshot
        {
            RequestCount = System.Threading.Interlocked.Read(ref _requestCount),
            PromptTokens = System.Threading.Interlocked.Read(ref _promptTokensTotal),
            CompletionTokens = System.Threading.Interlocked.Read(ref _completionTokensTotal),
            TotalTokens = System.Threading.Interlocked.Read(ref _totalTokensTotal),
            ContextWindowTokens = contextWindow
        };
    }

    /// <inheritdoc />
    public async Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
    {
        if (GetCodexCliSnapshot() is { } codexCli)
            return await codexCli.GetModelNameAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var response = await _http.GetAsync(NormalizePath(GetOptionsSnapshot().ModelsPath), cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(raw);

            // LM Studio's /v1/models returns { data: [{ id: "model-name", ... }] }
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                var modelName = data[0].TryGetProperty("id", out var id)
                    ? id.GetString() ?? "unknown"
                    : "connected";
                _lastReachable = true;
                _lastReportedModel = modelName;
                return modelName;
            }

            return "connected";
        }
        catch
        {
            // Endpoint not reachable — LM Studio is likely not running
            return null;
        }
    }

    public void Dispose()
    {
        GetCodexCliSnapshot()?.Dispose();
        _http.Dispose();
    }

    // ─────────────────────────────────────────────────────────────────
    // Auto-Model Loading
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures the configured model is loaded in LM Studio before
    /// the first request. Uses <c>GET /v1/models</c> to check and
    /// <c>POST /v1/models/load</c> to load if missing. Results are
    /// cached per model ID so the HTTP round-trip only happens once.
    /// <para>
    /// IMPORTANT: Only loads via POST when zero models are present.
    /// If other models are already loaded, we skip — sending a load
    /// request could displace an active model, causing regressions
    /// when multiple LLM clients share the same LM Studio endpoint.
    /// </para>
    /// </summary>
    private async Task EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        var options = GetOptionsSnapshot();
        var modelId = options.Model;
        if (string.IsNullOrWhiteSpace(modelId))
            return;

        // Fast path: already confirmed loaded this session,
        // or auto-loading is disabled (e.g. test/external HttpClient).
        if (!_autoLoadEnabled || _confirmedLoadedModels.Contains(modelId))
            return;

        try
        {
            // Check what's currently loaded.
            using var discoverCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            discoverCts.CancelAfter(ModelDiscoveryTimeout);
            var modelsResponse = await _http.GetAsync(NormalizePath(options.ModelsPath), discoverCts.Token);
            if (!modelsResponse.IsSuccessStatusCode)
            {
                _confirmedLoadedModels.Add(modelId);
                return;
            }

            var raw = await modelsResponse.Content.ReadAsStringAsync(discoverCts.Token);
            using var doc = JsonDocument.Parse(raw);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                _confirmedLoadedModels.Add(modelId);
                return;
            }

            var loadedCount = data.GetArrayLength();
            var foundOurModel = false;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl) ||
                    idEl.ValueKind != JsonValueKind.String)
                    continue;

                var loadedId = idEl.GetString();
                if (string.Equals(loadedId, modelId, StringComparison.OrdinalIgnoreCase))
                {
                    foundOurModel = true;
                    break;
                }
            }

            if (foundOurModel)
            {
                // Our model is already loaded — nothing to do.
                _confirmedLoadedModels.Add(modelId);
                return;
            }

            if (loadedCount > 0)
            {
                // Other models are loaded but ours isn't. Do NOT force-
                // load because that could displace them. The request will
                // go through to whichever model is active; LM Studio may
                // route it if multi-model is enabled.
                _confirmedLoadedModels.Add(modelId);
                return;
            }

            // No models loaded at all — safe to load ours.
            var modelKey = string.IsNullOrWhiteSpace(options.PreloadModelKey)
                ? modelId
                : options.PreloadModelKey.Trim();
            var loadPayload = new StringContent(
                JsonSerializer.Serialize(new { model = modelKey, identifier = modelId, context_length = options.ContextLength }),
                Encoding.UTF8,
                "application/json");

            using var loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loadCts.CancelAfter(ModelLoadTimeout);
            await _http.PostAsync(NormalizePath(options.ModelLoadPath), loadPayload, loadCts.Token);
            _confirmedLoadedModels.Add(modelId);
        }
        catch
        {
            // Non-fatal: if the models API is unavailable (e.g. older
            // LM Studio or non-LM Studio backend), skip gracefully.
            // Mark as "confirmed" so we don't retry every request.
            _confirmedLoadedModels.Add(modelId);
        }
    }

    /// <summary>
    /// Strips all <c>&lt;think&gt;...&lt;/think&gt;</c> blocks at the transport
    /// layer. Handles multiple blocks and truncated (unclosed) tags so
    /// callers never see raw chain-of-thought regardless of model.
    /// </summary>
    private static string? StripThinkBlocks(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        const string openTag = "<think>";
        const string closeTag = "</think>";

        // Fast-path: no think tags at all → return content unchanged
        // (avoids .Trim() side-effects for non-thinking models).
        if (content.IndexOf(openTag, StringComparison.OrdinalIgnoreCase) < 0 &&
            content.IndexOf(closeTag, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return content;
        }

        var cleaned = content.Trim();
        while (true)
        {
            var openIdx = cleaned.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0) break;

            var closeIdx = cleaned.IndexOf(closeTag, openIdx, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                cleaned = cleaned[..openIdx].Trim();
                break;
            }

            cleaned = (cleaned[..openIdx] + cleaned[(closeIdx + closeTag.Length)..]).Trim();
        }

        return cleaned.Length == 0 ? null : cleaned;
    }

    public async Task<LlmWarmupResult> WarmupAsync(CancellationToken cancellationToken = default)
    {
        if (GetCodexCliSnapshot() is { } codexCli)
            return await codexCli.WarmupAsync(cancellationToken).ConfigureAwait(false);

        var options = GetOptionsSnapshot();
        if (!options.EnableStartupWarmup)
        {
            return new LlmWarmupResult
            {
                Reachable = _lastReachable,
                Completed = _warmupCompleted,
                Model = options.Model,
                Snapshot = GetRuntimeHealthSnapshot()
            };
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.WarmupTimeoutSeconds)));

            var model = await GetModelNameAsync(cts.Token).ConfigureAwait(false);
            if (model is null)
            {
                _warmupCompleted = false;
                _logger.LogWarning("llm.warmup_unreachable baseUrl={BaseUrl}", options.BaseUrl);
                return new LlmWarmupResult
                {
                    Reachable = false,
                    Completed = false,
                    Model = options.Model,
                    Error = _lastError,
                    Snapshot = GetRuntimeHealthSnapshot()
                };
            }

            await ChatCoreAsync(
                    [ChatMessage.User("Respond with exactly: ready")],
                    tools: null,
                    maxTokensOverride: 8,
                    forcedToolName: null,
                    new LlmRequestContext
                    {
                        TaskKind = LlmTaskKind.Chat,
                        Priority = LlmRequestPriority.Background,
                        OperationName = "startup-warmup"
                    },
                    cts.Token)
                .ConfigureAwait(false);

            _warmupCompleted = true;
            _lastWarmupAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("llm.warmup_completed model={Model}", options.Model);
            return new LlmWarmupResult
            {
                Reachable = true,
                Completed = true,
                Model = model,
                Snapshot = GetRuntimeHealthSnapshot()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _warmupCompleted = false;
            _lastReachable = false;
            _lastError = ex.Message;
            _logger.LogWarning(ex, "llm.warmup_failed model={Model}", options.Model);
            return new LlmWarmupResult
            {
                Reachable = false,
                Completed = false,
                Model = options.Model,
                Error = ex.Message,
                Snapshot = GetRuntimeHealthSnapshot()
            };
        }
    }

    public LlmRuntimeHealthSnapshot GetRuntimeHealthSnapshot()
    {
        if (GetCodexCliSnapshot() is { } codexCli)
            return codexCli.GetRuntimeHealthSnapshot();

        var options = GetOptionsSnapshot();
        return new LlmRuntimeHealthSnapshot
        {
            LmStudioReachable = _lastReachable,
            ModelConfigured = options.Model,
            ModelLoadedOrReported = _lastReportedModel,
            WarmupCompleted = _warmupCompleted,
            ActiveRequests = _requestGate.ActiveRequests,
            QueuedRequests = _requestGate.QueuedRequests,
            LastRequestDurationMs = _lastRequestDurationMs,
            LastQueueWaitMs = _lastQueueWaitMs,
            LastEstimatedInputTokens = _lastEstimatedInputTokens,
            LastRequestedOutputTokens = _lastRequestedOutputTokens,
            LastTaskKind = _lastTaskKind,
            LastRequestWasBackground = _lastRequestWasBackground,
            LastError = _lastError,
            LastWarmupAt = _lastWarmupAt,
            LastRequestAt = _lastRequestAt
        };
    }

    /// <summary>
    /// Smallest completion we will ever ask for. Below this a reply is not
    /// worth generating, and squeezing further is the prompt budget's job.
    /// </summary>
    private const int MinOutputTokens = 256;

    /// <summary>
    /// Slack left between (input + output) and the context window to absorb the
    /// gap between our character-based token estimate and the model's real
    /// tokenizer, plus whatever the chat template adds.
    /// </summary>
    private const int ContextSafetyMarginTokens = 256;

    /// <summary>
    /// Context window observed from the provider itself, which outranks
    /// configuration. 0 until something authoritative is seen. Settings carry a
    /// user's *intent* (and drift out of date — a model can be loaded at a
    /// different size than <c>ContextLength</c> requested); the server's own
    /// numbers are ground truth.
    /// </summary>
    private int _observedContextWindowTokens;

    /// <summary>
    /// Best current estimate of the loaded model's context window, preferring
    /// an observed value over configuration.
    /// </summary>
    private int ResolveContextWindowTokens(LlmClientOptions options)
    {
        var observed = Volatile.Read(ref _observedContextWindowTokens);
        if (observed > 0) return observed;
        if (options.ContextWindowTokens > 0) return options.ContextWindowTokens;
        if (options.ContextLength > 0) return options.ContextLength;
        return 8192;
    }

    /// <summary>
    /// Records a context window learned from the provider so later requests are
    /// budgeted against the truth. Returns true when this is new information.
    /// </summary>
    private bool LearnContextWindowTokens(int contextWindowTokens)
    {
        if (contextWindowTokens <= 0) return false;
        var previous = Interlocked.Exchange(ref _observedContextWindowTokens, contextWindowTokens);
        return previous != contextWindowTokens;
    }

    /// <summary>
    /// Resolves <c>max_tokens</c> for a request so that
    /// <c>input + output</c> fits the context window.
    /// </summary>
    /// <remarks>
    /// Without this the client happily sends an arithmetically impossible
    /// request — e.g. a 6.4k-token tool-loop prompt plus <c>max_tokens: 4096</c>
    /// against an 8k context — and llama.cpp rejects the whole thing with
    /// "n_keep &gt;= n_ctx". The configured output cap is a ceiling, never a
    /// promise; physics wins.
    /// </remarks>
    private int ResolveOutputTokenBudget(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        LlmClientOptions options)
    {
        var requested = options.EffectiveMaxTokens(maxTokensOverride);
        var contextWindow = ResolveContextWindowTokens(options);
        var available = contextWindow
            - EstimateTokens(messages)
            - EstimateToolTokens(tools)
            - ContextSafetyMarginTokens;

        // When even the floor doesn't fit, the prompt itself is over budget.
        // Ask for the floor anyway: the provider's own error is then the honest
        // signal, rather than us silently requesting a negative budget.
        if (available < MinOutputTokens) return MinOutputTokens;
        return Math.Min(requested, available);
    }

    /// <summary>
    /// Explains a context overflow the client cannot budget its way out of,
    /// naming the oversized part so the reader knows which knob to turn.
    /// </summary>
    internal static string BuildContextExhaustedMessage(
        int contextWindowTokens,
        int promptTokens,
        int toolTokens,
        int toolCount)
    {
        var needed = promptTokens + toolTokens + MinOutputTokens;
        var message =
            $"The request needs about {needed:N0} tokens but the loaded model's context is " +
            $"{contextWindowTokens:N0}. Conversation and instructions account for roughly " +
            $"{promptTokens:N0} tokens";

        if (toolCount > 0)
        {
            message +=
                $", and the {toolCount} advertised tool definitions add about {toolTokens:N0} more";
        }

        message += ". ";

        // Point at whichever part is actually the problem.
        if (toolTokens >= contextWindowTokens / 2 && toolCount > 0)
        {
            message +=
                $"The tool definitions alone take up most of the context, so no reply can fit. " +
                $"Load the model with a larger context length, or reduce the number of enabled tools.";
        }
        else
        {
            message +=
                "Load the model with a larger context length, or start a new conversation to shorten the history.";
        }

        return message;
    }

    /// <summary>
    /// Extracts the context length from a llama.cpp / LM Studio overflow error.
    /// The body looks like:
    /// <c>The number of tokens to keep from the initial prompt is greater than
    /// the context length (n_keep: 11553&gt;= n_ctx: 8192)</c>.
    /// </summary>
    internal static bool TryParseContextOverflow(string? errorBody, out int contextWindowTokens)
    {
        contextWindowTokens = 0;
        if (string.IsNullOrWhiteSpace(errorBody)) return false;

        var match = System.Text.RegularExpressions.Regex.Match(
            errorBody,
            @"n_ctx:\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
        return match.Success
            && int.TryParse(match.Groups[1].Value, out contextWindowTokens)
            && contextWindowTokens > 0;
    }

    private IReadOnlyList<ChatMessage> ApplyPromptBudget(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        LlmRequestContext requestContext)
    {
        var options = GetOptionsSnapshot();
        // The configured soft cap is an intent; the context window is a hard
        // wall. Trim against whichever binds first, leaving room for a minimum
        // reply and for the tool schemas, which occupy the same window and are
        // not something trimming messages can reclaim.
        var contextCap = ResolveContextWindowTokens(options)
            - EstimateToolTokens(tools)
            - MinOutputTokens
            - ContextSafetyMarginTokens;
        var softCap = Math.Max(256, Math.Min(options.MaxInputTokensSoftCap, contextCap));
        var estimated = EstimateTokens(messages);
        if (estimated <= softCap)
            return messages;

        var maxChars = softCap * 4;
        var systemMessages = messages
            .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var nonSystem = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var kept = new List<ChatMessage>();
        var usedChars = systemMessages.Sum(EstimateMessageChars);
        for (var i = nonSystem.Length - 1; i >= 0; i--)
        {
            var candidate = nonSystem[i];
            var chars = EstimateMessageChars(candidate);
            if (usedChars + chars <= maxChars || kept.Count == 0)
            {
                kept.Add(candidate);
                usedChars += chars;
                continue;
            }

            break;
        }

        kept.Reverse();
        var result = new List<ChatMessage>(systemMessages.Length + kept.Count);
        result.AddRange(systemMessages);
        result.AddRange(kept);

        var reduced = EstimateTokens(result);
        _logger.LogInformation(
            "llm.prompt_reduced task={TaskKind} originalEstimatedTokens={OriginalTokens} reducedEstimatedTokens={ReducedTokens}",
            requestContext.TaskKind,
            estimated,
            reduced);
        return result.Count == 0 ? messages : result;
    }

    private void TrackRequestStart(
        LlmRequestContext requestContext,
        int estimatedTokens,
        int requestedOutputTokens,
        double queueWaitMs,
        DateTimeOffset startedAt)
    {
        _lastTaskKind = requestContext.TaskKind.ToString();
        _lastRequestWasBackground = requestContext.Priority == LlmRequestPriority.Background;
        _lastEstimatedInputTokens = estimatedTokens;
        _lastRequestedOutputTokens = requestedOutputTokens;
        _lastQueueWaitMs = (long)Math.Round(queueWaitMs);
        _lastRequestAt = startedAt;
    }

    private CancellationTokenSource CreateRequestCancellationTokenSource(CancellationToken cancellationToken)
    {
        var options = GetOptionsSnapshot();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.RequestTimeoutSeconds)));
        return cts;
    }

    private static int EstimateTokens(IReadOnlyList<ChatMessage> messages)
        => Math.Max(1, messages.Sum(EstimateMessageChars) / 4);

    /// <summary>
    /// Estimates the tokens the advertised tool definitions occupy.
    /// </summary>
    /// <remarks>
    /// Tool schemas travel in the same request body as the messages and count
    /// against the same context window, but they are a separate argument — so a
    /// messages-only estimate silently omits them. With a full MCP tool set
    /// that omission is thousands of tokens, which is how the client came to
    /// send requests it should have known could not fit.
    ///
    /// <para>Serialized JSON tokenizes far denser than prose (braces, quotes,
    /// and punctuation are mostly one token each), so this deliberately uses a
    /// tighter chars-per-token ratio than <see cref="EstimateTokens"/>.
    /// Overestimating costs a slightly shorter completion; underestimating
    /// costs the entire turn.</para>
    /// </remarks>
    private int EstimateToolTokens(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is not { Count: > 0 }) return 0;

        // Serializing on every request is wasteful when the tool set rarely
        // changes, so memoize against the last list instance we measured.
        var cached = _toolTokenEstimate;
        if (cached is not null && ReferenceEquals(cached.Value.Tools, tools))
            return cached.Value.Tokens;

        int tokens;
        try
        {
            tokens = JsonSerializer.Serialize(tools, _json).Length / 3;
        }
        catch (NotSupportedException)
        {
            // A schema that won't serialize here would fail the request anyway;
            // fall back to a coarse structural estimate rather than throwing
            // from a budgeting helper.
            tokens = tools.Sum(t =>
                ((t.Function?.Name?.Length ?? 0) + (t.Function?.Description?.Length ?? 0)) / 3 + 64);
        }

        _toolTokenEstimate = (tools, tokens);
        return tokens;
    }

    private (IReadOnlyList<ToolDefinition> Tools, int Tokens)? _toolTokenEstimate;

    private static int EstimateMessageChars(ChatMessage message)
    {
        var chars = (message.Role?.Length ?? 0) + (message.Content?.Length ?? 0);
        if (message.ToolCalls is { Count: > 0 })
        {
            foreach (var call in message.ToolCalls)
                chars += call.Function.Name.Length + call.Function.Arguments.Length;
        }

        return chars;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        var trimmed = path.Trim();
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static LlmEndpointGate GetEndpointGate(LlmClientOptions options)
    {
        var key = string.IsNullOrWhiteSpace(options.BaseUrl)
            ? "default"
            : options.BaseUrl.Trim().TrimEnd('/');
        return EndpointGates.GetOrAdd(key, _ => new LlmEndpointGate(Math.Max(1, options.MaxConcurrentLlmRequests)));
    }

    private sealed class LlmEndpointGate
    {
        private readonly SemaphoreSlim _semaphore;
        private int _activeRequests;
        private int _queuedRequests;

        public LlmEndpointGate(int maxConcurrency)
        {
            _semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        }

        public int ActiveRequests => Volatile.Read(ref _activeRequests);
        public int QueuedRequests => Volatile.Read(ref _queuedRequests);

        public Task WaitAsync(CancellationToken cancellationToken) => _semaphore.WaitAsync(cancellationToken);
        public void IncrementActive() => Interlocked.Increment(ref _activeRequests);
        public void DecrementActive()
        {
            var remaining = Interlocked.Decrement(ref _activeRequests);
            if (remaining < 0)
            {
                Interlocked.Increment(ref _activeRequests);
                return;
            }

            _semaphore.Release();
        }
        public void IncrementQueued() => Interlocked.Increment(ref _queuedRequests);
        public void DecrementQueued() => Interlocked.Decrement(ref _queuedRequests);
    }

    private LlmClientOptions GetOptionsSnapshot()
    {
        lock (_optionsGate)
            return _options;
    }

    private CodexCliLlmClient? GetCodexCliSnapshot()
    {
        lock (_optionsGate)
            return _codexCli;
    }

    private void TrackUsage(TokenUsage? usage)
    {
        if (usage is null)
            return;

        if (usage.PromptTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _promptTokensTotal,
                usage.PromptTokens);
        }

        if (usage.CompletionTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _completionTokensTotal,
                usage.CompletionTokens);
        }

        if (usage.TotalTokens > 0)
        {
            System.Threading.Interlocked.Add(
                ref _totalTokensTotal,
                usage.TotalTokens);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Internal DTOs matching the OpenAI response shape
    // ─────────────────────────────────────────────────────────────────

    private sealed record CompletionResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("choices")]
        public List<CompletionChoice>? Choices { get; init; }

        [JsonPropertyName("usage")]
        public TokenUsage? Usage { get; init; }
    }

    private sealed record CompletionChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("message")]
        public ChoiceMessage? Message { get; init; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    private sealed record ChoiceMessage
    {
        [JsonPropertyName("role")]
        public string? Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        /// <summary>
        /// Some providers (LM Studio ≥ 0.3.x with thinking models) surface
        /// the chain-of-thought in a dedicated field rather than (or in
        /// addition to) embedding it inside <c>&lt;think&gt;</c> tags in
        /// <see cref="Content"/>.
        /// </summary>
        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; init; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; init; }

        [JsonPropertyName("tool_calls")]
        public List<ToolCallRequest>? ToolCalls { get; init; }
    }
}
