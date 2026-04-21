using Microsoft.Extensions.Logging;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Settings;
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
    private readonly Func<LlmSettings, IAssistant> _llmFactory;
    private readonly ILogger<AssistantRouter> _logger;

    /// <summary>Production constructor — builds an <see cref="LmStudioAssistant"/>
    /// over a cached <see cref="LmStudioClient"/> on demand.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        IThreadStore store,
        ChatTurnPublisher publisher,
        ILoggerFactory loggerFactory)
        : this(settings, stub,
              CreateDefaultFactory(store, publisher, loggerFactory),
              loggerFactory.CreateLogger<AssistantRouter>())
    {
    }

    /// <summary>Test seam: inject a custom factory mapping settings to an assistant.</summary>
    public AssistantRouter(
        ISettingsStore settings,
        StubAssistant stub,
        Func<LlmSettings, IAssistant> llmFactory,
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
            lm = _llmFactory(llm);
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

    private static Func<LlmSettings, IAssistant> CreateDefaultFactory(
        IThreadStore store, ChatTurnPublisher publisher, ILoggerFactory loggerFactory)
    {
        var gate = new object();
        LmStudioClient? cached = null;
        string? fingerprint = null;

        return llm =>
        {
            var fp = $"{llm.BaseUrl}|{llm.ModelId}|{llm.ApiKey}|{llm.MaxTokens}|{llm.ContextWindowTokens}|{llm.Temperature}";
            lock (gate)
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
                return new LmStudioAssistant(
                    cached, store, publisher,
                    loggerFactory.CreateLogger<LmStudioAssistant>());
            }
        };
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
