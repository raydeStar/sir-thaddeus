using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using RuntimeChatMessage = Thaddeus.SharedTypes.ChatMessage;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Phase 9 dispatch layer. Picks between the real LM-Studio-backed assistant
/// and the deterministic stub based on the current
/// <see cref="SettingsDocument"/>:
///
/// <list type="bullet">
///   <item>If <c>Llm.Provider</c> is "stub" or <c>Llm.BaseUrl</c>/<c>ModelId</c>
///         is blank, the stub is used.</item>
///   <item>Otherwise the LM-Studio-backed assistant is used. On
///         <see cref="HttpRequestException"/> the router falls back to the
///         stub for the same turn so the user always gets a reply.</item>
/// </list>
///
/// Subscribes to <see cref="ISettingsStore.Changed"/> so the cached client
/// rebuilds against the new settings on the next turn without restarting.
/// </summary>
public sealed class AssistantRouter : IAssistant, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly StubAssistant _stub;
    private readonly Func<SettingsDocument, IAssistant> _llmFactory;
    private readonly ILogger<AssistantRouter> _logger;

    /// <summary>Production constructor — builds an <see cref="LmStudioAssistant"/>
    /// over a cached <see cref="LmStudioClient"/> on demand.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        IMcpToolClient mcp,
        ToolPermissionGate gate,
        IThreadStore store,
        ChatTurnPublisher publisher,
        ILoggerFactory loggerFactory)
        : this(settings, stub,
              CreateDefaultFactory(mcp, gate, store, publisher, loggerFactory),
              loggerFactory.CreateLogger<AssistantRouter>())
    {
    }

    /// <summary>Test seam: inject a custom factory mapping settings to an assistant.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        Func<SettingsDocument, IAssistant> llmFactory,
        ILogger<AssistantRouter> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _stub = stub ?? throw new ArgumentNullException(nameof(stub));
        _llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _settings.Changed += OnSettingsChanged;
    }

    public async Task<RuntimeChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
    {
        var doc = await _settings.GetAsync(ct).ConfigureAwait(false);
        var llm = doc.Llm;

        if (UseStub(llm))
        {
            return await _stub.RespondAsync(threadId, userText, ct).ConfigureAwait(false);
        }

        IAssistant lm;
        try
        {
            lm = _llmFactory(doc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "assistant_router.lm_build_failed provider={Provider} base={Base}",
                llm.Provider, llm.BaseUrl);
            return await _stub.RespondAsync(threadId, userText, ct).ConfigureAwait(false);
        }

        try
        {
            return await lm.RespondAsync(threadId, userText, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "assistant_router.lm_unreachable thread={ThreadId} provider={Provider} base={Base}",
                threadId, llm.Provider, llm.BaseUrl);
            return await _stub.RespondAsync(threadId, userText, ct).ConfigureAwait(false);
        }
    }

    private static bool UseStub(LlmSettings llm) =>
        string.Equals(llm.Provider, "stub", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(llm.BaseUrl)
        || string.IsNullOrWhiteSpace(llm.ModelId);

    private static Func<SettingsDocument, IAssistant> CreateDefaultFactory(
        IMcpToolClient mcp, ToolPermissionGate gate, IThreadStore store, ChatTurnPublisher publisher, ILoggerFactory loggerFactory)
    {
        var cacheLock = new object();
        LmStudioClient? cached = null;
        LmStudioClient? gatekeeperCached = null;
        IFootmanRouter? footmanCached = null;
        string? fingerprint = null;
        string? gatekeeperFingerprint = null;

        return doc =>
        {
            var llm = doc.Llm;
            var fp = $"{llm.BaseUrl}|{llm.ModelId}|{llm.ApiKey}|{llm.MaxTokens}|{llm.ContextWindowTokens}|{llm.Temperature}";
            var gfp = $"{llm.GatekeeperBaseUrl ?? llm.BaseUrl}|{llm.GatekeeperModelId}|{llm.ReusePrimaryForGatekeeperOnSharedEndpoint}|{llm.GatekeeperEnabled}";
            lock (cacheLock)
            {
                if (cached is null || fingerprint != fp)
                {
                    var options = new LlmClientOptions
                    {
                        BaseUrl = llm.BaseUrl!,
                        Model = llm.ModelId,
                        MaxTokens = llm.MaxTokens,
                        ContextWindowTokens = llm.ContextWindowTokens,
                        Temperature = llm.Temperature,
                    };
                    if (cached is null)
                    {
                        cached = new LmStudioClient(options);
                    }
                    else
                    {
                        cached.UpdateOptions(options);
                    }
                    fingerprint = fp;
                }

                // Gatekeeper client + footman router, rebuilt only when
                // gatekeeper-related settings change. Null footman means the
                // gatekeeper isn't configured (primary-model-only mode).
                if (gatekeeperFingerprint != gfp)
                {
                    var gkOptions = BuildGatekeeperOptions(llm, cached);
                    if (gkOptions is null)
                    {
                        gatekeeperCached?.Dispose();
                        gatekeeperCached = null;
                        footmanCached = null;
                    }
                    else if (ReferenceEquals(gkOptions.SharedPrimaryClient, cached))
                    {
                        // Reuse primary client — no separate LM Studio session
                        // to burn. Common on single-GPU setups where both
                        // models point at the same endpoint.
                        gatekeeperCached?.Dispose();
                        gatekeeperCached = null;
                        footmanCached = new FastLlmFootmanRouter(cached);
                    }
                    else
                    {
                        if (gatekeeperCached is null)
                        {
                            gatekeeperCached = new LmStudioClient(gkOptions.ClientOptions);
                        }
                        else
                        {
                            gatekeeperCached.UpdateOptions(gkOptions.ClientOptions);
                        }
                        footmanCached = new FastLlmFootmanRouter(gatekeeperCached);
                    }
                    gatekeeperFingerprint = gfp;
                }

                var loc = doc.Location;
                return new LmStudioAssistant(
                    cached, mcp, gate, store, publisher,
                    loggerFactory.CreateLogger<LmStudioAssistant>())
                {
                    Footman = footmanCached,
                    LocationHint = string.IsNullOrWhiteSpace(loc?.ManualLocation) ? null : loc!.ManualLocation,
                    PreferredUnits = string.IsNullOrWhiteSpace(loc?.PreferredUnits) ? null : loc!.PreferredUnits,
                };
            }
        };
    }

    /// <summary>
    /// Decides how to build the gatekeeper client for the footman router:
    /// skip entirely (null), reuse the primary client (when models match —
    /// the primary IS the gatekeeper), or build a separate client pinned to
    /// the configured gatekeeper model ID.
    ///
    /// We always respect <see cref="LlmSettings.GatekeeperModelId"/>. Reusing
    /// the primary client is only safe when the primary and gatekeeper model
    /// IDs are identical — otherwise the footman's prompt would silently
    /// route to the primary model (often a large chat model), crushing
    /// latency and tripping the footman timeout.
    /// </summary>
    private static GatekeeperBuildResult? BuildGatekeeperOptions(LlmSettings llm, LmStudioClient primary)
    {
        // Explicit off-switch — skip all gatekeeper plumbing so the primary
        // model gets the full tool menu on every turn. Cheaper + simpler
        // than clearing the gatekeeper model id, because the id is
        // preserved for when the user toggles the footman back on.
        if (!llm.GatekeeperEnabled) return null;
        if (string.IsNullOrWhiteSpace(llm.GatekeeperModelId)) return null;

        var gkBaseUrl = string.IsNullOrWhiteSpace(llm.GatekeeperBaseUrl)
            ? llm.BaseUrl
            : llm.GatekeeperBaseUrl;
        if (string.IsNullOrWhiteSpace(gkBaseUrl)) return null;

        var samePrimaryModel = string.Equals(llm.ModelId, llm.GatekeeperModelId, StringComparison.OrdinalIgnoreCase);
        var sameEndpoint = UriHostsMatch(llm.BaseUrl ?? "", gkBaseUrl);

        // Reuse the primary client only when the gatekeeper model IS the
        // primary model (no model swap, no wasted HTTP client). The
        // ReusePrimaryForGatekeeperOnSharedEndpoint toggle is kept for
        // backwards compatibility but no longer redirects a distinct
        // gatekeeper model through the primary — that was silently making
        // the footman run on a 70B model and time out.
        if (samePrimaryModel && sameEndpoint)
        {
            return new GatekeeperBuildResult(SharedPrimaryClient: primary, ClientOptions: null!);
        }

        var options = new LlmClientOptions
        {
            BaseUrl = gkBaseUrl!,
            Model = llm.GatekeeperModelId!,
            MaxTokens = 120,       // footman replies with a tiny JSON envelope
            ContextWindowTokens = 2048,
            Temperature = 0.0,
        };
        return new GatekeeperBuildResult(SharedPrimaryClient: null, ClientOptions: options);
    }

    private static bool UriHostsMatch(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    }

    private sealed record GatekeeperBuildResult(LmStudioClient? SharedPrimaryClient, LlmClientOptions ClientOptions);

    private void OnSettingsChanged(SettingsDocument doc)
    {
        // Default factory's fingerprint check will rebuild on next call.
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
    }
}
