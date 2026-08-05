using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Validation;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using Thaddeus.Runtime.Modules;
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
    private readonly ModuleRuntimeService? _modules;

    /// <summary>Production constructor — builds an <see cref="LmStudioAssistant"/>
    /// over a cached <see cref="LmStudioClient"/> on demand.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        IMcpToolClient mcp,
        ToolPermissionGate gate,
        IThreadStore store,
        ChatTurnPublisher publisher,
        IAuditLogger audit,
        ILoggerFactory loggerFactory,
        ModuleRuntimeService modules,
        LlmRuntimeRegistry llmRuntime,
        HarnessToolEvidenceStore harnessToolEvidence,
        SirThaddeus.Memory.IMemoryStore? memoryStore = null,
        TurnRunCoordinator? runCoordinator = null)
        : this(settings, stub,
              CreateDefaultFactory(
                  mcp, gate, store, publisher, audit, loggerFactory, llmRuntime,
                  harnessToolEvidence, memoryStore, runCoordinator),
              loggerFactory.CreateLogger<AssistantRouter>(),
              modules)
    {
    }

    /// <summary>Test seam: inject a custom factory mapping settings to an assistant.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        Func<SettingsDocument, IAssistant> llmFactory,
        ILogger<AssistantRouter> logger,
        ModuleRuntimeService? modules = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _stub = stub ?? throw new ArgumentNullException(nameof(stub));
        _llmFactory = llmFactory ?? throw new ArgumentNullException(nameof(llmFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _modules = modules;

        _settings.Changed += OnSettingsChanged;
    }

    public async Task<RuntimeChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
        => await RespondAsync(threadId, userText, new AssistantTurnOptions(), ct).ConfigureAwait(false);

    public async Task<RuntimeChatMessage> RespondAsync(
        string threadId,
        string userText,
        AssistantTurnOptions options,
        CancellationToken ct)
    {
        if (_modules is not null && _modules.IsHealthBriefRequest(userText))
        {
            var reply = await _modules.BuildHealthBriefChatResponseAsync(ct).ConfigureAwait(false);
            return await _stub.RespondWithAsync(threadId, reply, ct).ConfigureAwait(false);
        }

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
            return await lm.RespondAsync(threadId, userText, options, ct).ConfigureAwait(false);
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
        IMcpToolClient mcp, ToolPermissionGate gate, IThreadStore store, ChatTurnPublisher publisher,
        IAuditLogger audit, ILoggerFactory loggerFactory,
        LlmRuntimeRegistry llmRuntime,
        HarnessToolEvidenceStore harnessToolEvidence,
        SirThaddeus.Memory.IMemoryStore? memoryStore,
        TurnRunCoordinator? runCoordinator)
    {
        var cacheLock = new object();
        IConfigurableLlmClient? cached = null;
        IConfigurableLlmClient? gatekeeperCached = null;
        IFootmanRouter? footmanCached = null;
        IPersonalityRuntime? personalityCached = null;
        IMemoryContextProvider? memoryProviderCached = null;
        ISearchFallbackExecutor? searchFallbackCached = null;
        ReasoningGuardrailsPipeline? guardrailsCached = null;
        CompletionValidator? validatorCached = null;
        RepairLoop? repairCached = null;
        // Dialogue state survives settings rebuilds — it's per-thread
        // conversation context, not per-client wiring.
        IDialogueStateAccessor dialogueAccessor = new ThreadScopedDialogueStateAccessor();
        string? fingerprint = null;
        string? gatekeeperFingerprint = null;
        var footmanLlmTimeout = TimeSpan.FromMilliseconds(1500);

        return doc =>
        {
            var llm = doc.Llm;
            var forcedToolChoiceMode = ModelCapabilityPolicy.ResolveForcedToolChoiceMode(
                doc,
                llmRuntime.GetSnapshot());
            var fp = $"{llm.Provider}|{llm.BaseUrl}|{llm.ModelId}|{llm.ApiKey}|{llm.MaxTokens}|{llm.ContextWindowTokens}|{llm.Temperature}|{llm.CodexCliPath}|{llm.CodexReasoningEffort}|{forcedToolChoiceMode}";
            var gatekeeperPolicy = ResolveGatekeeperPolicy(llm);
            var gfp = gatekeeperPolicy.Fingerprint;
            lock (cacheLock)
            {
                if (cached is null || fingerprint != fp)
                {
                    var options = ToClientOptions(llm, forcedToolChoiceMode);
                    if (cached is null)
                    {
                        cached = LlmClientFactory.Create(options, loggerFactory: loggerFactory);
                    }
                    else
                    {
                        cached.UpdateOptions(options);
                    }
                    llmRuntime.SetPrimary(cached);
                    fingerprint = fp;

                    // Guardrails, validator, and repair loop are all
                    // pinned to the primary LLM client. When the primary
                    // rebuilds, drop the cached pipeline so the next turn
                    // rewires against the refreshed client.
                    guardrailsCached = null;
                    validatorCached = null;
                    repairCached = null;
                    memoryProviderCached = null;
                }

                // Gatekeeper client + footman router, rebuilt only when
                // gatekeeper-related settings change. Null footman means the
                // gatekeeper isn't configured (primary-model-only mode).
                if (gatekeeperFingerprint != gfp)
                {
                    switch (gatekeeperPolicy.Mode)
                    {
                        case GatekeeperPolicyMode.Off:
                            gatekeeperCached?.Dispose();
                            gatekeeperCached = null;
                            footmanCached = null;
                            break;
                        case GatekeeperPolicyMode.HeuristicOnly:
                            gatekeeperCached?.Dispose();
                            gatekeeperCached = null;
                            footmanCached = new HeuristicFootmanRouter();
                            break;
                        case GatekeeperPolicyMode.SharedPrimary:
                            gatekeeperCached?.Dispose();
                            gatekeeperCached = null;
                            footmanCached = new FastLlmFootmanRouter(cached, timeout: footmanLlmTimeout);
                            break;
                        case GatekeeperPolicyMode.SeparateLlm:
                            var options = gatekeeperPolicy.ToClientOptions();
                            if (gatekeeperCached is null)
                            {
                                gatekeeperCached = LlmClientFactory.Create(options, loggerFactory: loggerFactory);
                            }
                            else
                            {
                                gatekeeperCached.UpdateOptions(options);
                            }
                            footmanCached = new FastLlmFootmanRouter(gatekeeperCached, timeout: footmanLlmTimeout);
                            break;
                        default:
                            throw new InvalidOperationException($"Unknown gatekeeper policy: {gatekeeperPolicy.Mode}");
                    }
                    gatekeeperFingerprint = gfp;
                    memoryProviderCached = null;
                }

                var loc = doc.Location;

                // Personality runtime: constructed lazily on first turn
                // build-out so the profile is only loaded when a chat
                // actually runs. Settings doc doesn't have a profile-id
                // field yet, so we pass empty and the store falls back
                // to the built-in default.
                var personalityRuntime = personalityCached ??= new PersonalityRuntime(
                    activeProfileId: string.Empty,
                    profilesDirectory: SirThaddeus.Config.SettingsManager.GetPersonalityProfilesDirectory());

                // Memory context provider reads facts from the MCP
                // memory tools. In heuristic-only/off gatekeeper modes,
                // avoid a hidden helper LLM call that could trigger the
                // same shared-endpoint model swap the Footman policy is
                // trying to prevent.
                var classifierLlm = gatekeeperCached ?? cached;
                var memoryProvider = memoryProviderCached ??= new MemoryContextProvider(
                    mcp,
                    audit,
                    new SmartIntentClassifier(
                        classifierLlm,
                        audit,
                        allowLlmFallback: gatekeeperPolicy.AllowsHelperLlm));

                // Search fallback: retries refusal-shaped drafts via the
                // SearchOrchestrator. The orchestrator owns no session
                // state — safe to cache across turns. Uses a minimal
                // base prompt (the orchestrator supplies its own
                // instruction blocks at call time).
                var searchFallback = searchFallbackCached ??= new SearchFallbackExecutor(
                    new SearchOrchestrator(cached, mcp, audit,
                        "You are Sir Thaddeus, a helpful local-first assistant."));

                // Reasoning-guardrails pipeline: first-principles scaffold
                // for reasoning-shaped prompts. Constructed lazily against
                // the primary client — rebuilt by the fingerprint branch
                // above when the client refreshes.
                var guardrails = guardrailsCached ??= new ReasoningGuardrailsPipeline(cached, audit);

                // Completion validator + repair loop: catches inadequate
                // drafts (refusals after tool work, unaddressed user asks)
                // and runs one focused repair pass. Shares the primary
                // client for both passes.
                var validator = validatorCached ??= new CompletionValidator(cached);
                var repair = repairCached ??= new RepairLoop(cached, validator);

                return new LmStudioAssistant(
                    cached, mcp, gate, store, publisher, audit,
                    loggerFactory.CreateLogger<LmStudioAssistant>())
                {
                    Footman = footmanCached,
                    LocationHint = string.IsNullOrWhiteSpace(loc?.ManualLocation) ? null : loc!.ManualLocation,
                    PreferredUnits = string.IsNullOrWhiteSpace(loc?.PreferredUnits) ? null : loc!.PreferredUnits,
                    OfflineMode = doc.Privacy.OfflineMode,
                    PersonalityRuntime = personalityRuntime,
                    MemoryContextProvider = memoryProvider,
                    MemoryStore = memoryStore,
                    MemoryEnabled = doc.Memory?.Enabled ?? true,
                    WikiWriteEnabled = ModelCapabilityPolicy.IsWikiWriteEnabled(doc, llmRuntime.GetSnapshot()),
                    SearchFallbackExecutor = searchFallback,
                    GuardrailsPipeline = guardrails,
                    CompletionValidator = validator,
                    CompletionRepairLoop = repair,
                    HarnessToolEvidence = harnessToolEvidence,
                    DialogueStateAccessor = dialogueAccessor,
                    ExecutionControl = runCoordinator,
                    MaxOutputTokens = Math.Max(1, llm.MaxTokens),
                };
            }
        };
    }

    internal static GatekeeperPolicy ResolveGatekeeperPolicy(LlmSettings llm)
    {
        if (LlmProvider.IsCodexCli(llm.Provider))
            return GatekeeperPolicy.HeuristicOnly(llm, llm.BaseUrl ?? string.Empty);

        if (!llm.GatekeeperEnabled || string.IsNullOrWhiteSpace(llm.GatekeeperModelId))
            return GatekeeperPolicy.Off(llm);

        var gkBaseUrl = string.IsNullOrWhiteSpace(llm.GatekeeperBaseUrl)
            ? llm.BaseUrl
            : llm.GatekeeperBaseUrl;
        if (string.IsNullOrWhiteSpace(gkBaseUrl))
            return GatekeeperPolicy.Off(llm);

        var samePrimaryModel = string.Equals(llm.ModelId, llm.GatekeeperModelId, StringComparison.OrdinalIgnoreCase);
        var sameEndpoint = UriHostsMatch(llm.BaseUrl ?? "", gkBaseUrl);

        if (samePrimaryModel && sameEndpoint)
        {
            return GatekeeperPolicy.SharedPrimary(llm, gkBaseUrl);
        }

        if (sameEndpoint &&
            llm.ReusePrimaryForGatekeeperOnSharedEndpoint &&
            string.IsNullOrWhiteSpace(llm.GatekeeperModelId))
        {
            return GatekeeperPolicy.HeuristicOnly(llm, gkBaseUrl);
        }

        return GatekeeperPolicy.SeparateLlm(llm, gkBaseUrl);
    }

    internal static LlmClientOptions ToClientOptions(
        LlmSettings llm,
        ForcedToolChoiceMode forcedToolChoiceMode = ForcedToolChoiceMode.Required)
    {
        return new LlmClientOptions
        {
            Provider = llm.Provider,
            BaseUrl = llm.BaseUrl!,
            Model = llm.ModelId,
            ForcedToolChoiceMode = forcedToolChoiceMode,
            CodexCliPath = llm.CodexCliPath,
            CodexReasoningEffort = llm.CodexReasoningEffort,
            MaxTokens = llm.MaxTokens,
            ContextWindowTokens = llm.ContextWindowTokens,
            Temperature = llm.Temperature,
            ChatCompletionPath = string.IsNullOrWhiteSpace(llm.ChatCompletionPath)
                ? "/v1/chat/completions"
                : llm.ChatCompletionPath,
            PreloadModelKey = llm.PreloadModelKey,
            EnableStartupWarmup = llm.EnableStartupWarmup,
            EnableKeepWarm = llm.EnableKeepWarm,
            ContextLength = llm.ContextLength > 0 ? llm.ContextLength : 4096,
            FlashAttention = llm.FlashAttention,
            OffloadKvCacheToGpu = llm.OffloadKvCacheToGpu,
            MaxConcurrentLlmRequests = llm.MaxConcurrentLlmRequests > 0 ? llm.MaxConcurrentLlmRequests : 1,
            WarmupTimeoutSeconds = llm.WarmupTimeoutSeconds > 0 ? llm.WarmupTimeoutSeconds : 120,
            KeepWarmIntervalMinutes = llm.KeepWarmIntervalMinutes > 0 ? llm.KeepWarmIntervalMinutes : 30,
            MaxInputTokensSoftCap = llm.MaxInputTokensSoftCap > 0 ? llm.MaxInputTokensSoftCap : 4000,
            MaxOutputTokensDefault = llm.MaxOutputTokensDefault > 0
                ? llm.MaxOutputTokensDefault
                : Math.Min(Math.Max(llm.MaxTokens, 128), 4096)
        };
    }

    private static bool UriHostsMatch(string left, string right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a)) return false;
        if (!Uri.TryCreate(right, UriKind.Absolute, out var b)) return false;
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
    }

    internal enum GatekeeperPolicyMode
    {
        Off,
        HeuristicOnly,
        SharedPrimary,
        SeparateLlm
    }

    internal sealed record GatekeeperPolicy(
        GatekeeperPolicyMode Mode,
        string? BaseUrl,
        string? ModelId,
        string Fingerprint)
    {
        public bool AllowsHelperLlm => Mode is GatekeeperPolicyMode.SharedPrimary or GatekeeperPolicyMode.SeparateLlm;

        public static GatekeeperPolicy Off(LlmSettings llm) => Create(GatekeeperPolicyMode.Off, llm, null, null);

        public static GatekeeperPolicy HeuristicOnly(LlmSettings llm, string baseUrl) =>
            Create(GatekeeperPolicyMode.HeuristicOnly, llm, baseUrl, llm.GatekeeperModelId);

        public static GatekeeperPolicy SharedPrimary(LlmSettings llm, string baseUrl) =>
            Create(GatekeeperPolicyMode.SharedPrimary, llm, baseUrl, llm.ModelId);

        public static GatekeeperPolicy SeparateLlm(LlmSettings llm, string baseUrl) =>
            Create(GatekeeperPolicyMode.SeparateLlm, llm, baseUrl, llm.GatekeeperModelId);

        public LlmClientOptions ToClientOptions()
        {
            if (Mode is not GatekeeperPolicyMode.SeparateLlm || string.IsNullOrWhiteSpace(BaseUrl) || string.IsNullOrWhiteSpace(ModelId))
                throw new InvalidOperationException("Separate gatekeeper LLM policy requires a base URL and model id.");

            return new LlmClientOptions
            {
                BaseUrl = BaseUrl,
                Model = ModelId,
                MaxTokens = 120,
                ContextWindowTokens = 2048,
                Temperature = 0.0,
            };
        }

        private static GatekeeperPolicy Create(
            GatekeeperPolicyMode mode,
            LlmSettings llm,
            string? baseUrl,
            string? modelId)
        {
            var fingerprint = string.Join('|',
                mode,
                llm.Provider ?? string.Empty,
                llm.BaseUrl ?? string.Empty,
                llm.ModelId ?? string.Empty,
                baseUrl ?? string.Empty,
                modelId ?? string.Empty,
                llm.GatekeeperEnabled,
                llm.GatekeeperBaseUrl ?? string.Empty,
                llm.GatekeeperModelId ?? string.Empty,
                llm.ReusePrimaryForGatekeeperOnSharedEndpoint);

            return new GatekeeperPolicy(mode, baseUrl, modelId, fingerprint);
        }
    }

    private void OnSettingsChanged(SettingsDocument doc)
    {
        // Default factory's fingerprint check will rebuild on next call.
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
    }
}
