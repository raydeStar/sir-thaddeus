using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Context;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.PersonalityEngine.Formatting;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    public int MaxTokensBudget
    {
        get => _maxTokensCasual;
        set
        {
            _maxTokensCasual = value > 0 ? value : 512;
            _maxTokensCasualRetry = Math.Max(_maxTokensCasual, 2048);
        }
    }

    public void SeedHistory(IEnumerable<(string Role, string Content)> priorMessages)
    {
        foreach (var (role, content) in priorMessages)
        {
            if (string.IsNullOrWhiteSpace(content)) continue;

            switch (role)
            {
                case "user":
                    _history.Add(ChatMessage.User(content));
                    break;
                case "assistant":
                    _history.Add(ChatMessage.Assistant(content));
                    break;
            }
        }

        TrimHistory();
    }

    public bool ContextLocked
    {
        get => _dialogueStore.Get().ContextLocked;
        set
        {
            var current = _dialogueStore.Get();
            if (current.ContextLocked == value)
                return;

            _dialogueStore.Update(current with { ContextLocked = value });
        }
    }

    public AgentOrchestrator(
        ILlmClient llm,
        IMcpToolClient mcp,
        IAuditLogger audit,
        string systemPrompt,
        TimeProvider? timeProvider = null,
        IDialogueStateStore? dialogueStateStore = null,
        SlotExtract? slotExtract = null,
        MergeSlots? mergeSlots = null,
        ValidateSlots? validateSlots = null,
        IToolPlanner? toolPlanner = null,
        string geocodeMismatchMode = "fallback_previous",
        IRouter? router = null,
        IMemoryContextProvider? memoryContextProvider = null,
        IToolLoopExecutor? toolLoopExecutor = null,
        IDeterministicUtilityEngine? deterministicUtilityEngine = null,
        IGuardrailsCoordinator? guardrailsCoordinator = null,
        ISelfMemorySummarizer? selfMemorySummarizer = null,
        ISearchFallbackExecutor? searchFallbackExecutor = null,
        IContextAnchoringService? contextAnchoringService = null,
        IUtilityIntentHandler? utilityIntentHandler = null,
        IConversationSegmenter? conversationSegmenter = null,
        MiniActionableExtractor? miniActionableExtractor = null,
        SegmentExecutionCoordinator? segmentExecutionCoordinator = null,
        UnifiedResponseComposer? unifiedResponseComposer = null,
        IPersonalityRuntime? personalityRuntime = null,
        string? activePersonalityId = null,
        string? personalityProfilesDirectory = null,
        IFootmanRouter? footmanRouter = null,
        IAutoMemoryExtractor? autoMemoryExtractor = null,
        ILlmClient? gatekeeperLlm = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _activePersonalityId = NormalizePersonalityId(activePersonalityId);
        _personalityProfilesDirectory = string.IsNullOrWhiteSpace(personalityProfilesDirectory)
            ? ResolveDefaultPersonalityProfilesDirectory()
            : personalityProfilesDirectory.Trim();
        _personalityRuntime = personalityRuntime ?? new PersonalityRuntime(
            _activePersonalityId,
            _personalityProfilesDirectory);

        _searchOrchestrator = new SearchOrchestrator(
            llm,
            mcp,
            audit,
            _personalityRuntime.BuildSystemPrompt(_systemPrompt))
        {
            PreferredUnits = _preferredUnits
        };
        _dialogueStore = dialogueStateStore ?? new DialogueStateStore(_timeProvider);
        _slotExtract = slotExtract ?? new SlotExtract(llm, audit);
        _mergeSlots = mergeSlots ?? new MergeSlots();
        _validateSlots = validateSlots ?? new ValidateSlots(new ValidationOptions
        {
            GeocodeMismatchMode = geocodeMismatchMode
        });
        _toolPlanner = toolPlanner ?? new ToolPlanner();

        var effectiveGatekeeper = gatekeeperLlm ?? llm;
        _reasoningGuardrailsPipeline = new ReasoningGuardrailsPipeline(effectiveGatekeeper, audit);
        _deterministicUtilityEngine = deterministicUtilityEngine ?? new DeterministicUtilityEngineAdapter();
        _router = router ?? new Routing.RouterV2(effectiveGatekeeper, _deterministicUtilityEngine);
        _memoryContextProvider = memoryContextProvider ?? new MemoryContextProvider(
            mcp,
            audit,
            new SmartIntentClassifier(effectiveGatekeeper, audit),
            _timeProvider);
        _toolLoopExecutor = toolLoopExecutor ?? new ToolLoopExecutor(llm, mcp);
        _guardrailsCoordinator = guardrailsCoordinator ?? new GuardrailsCoordinator(_reasoningGuardrailsPipeline);
        _toolDefinitionBuilder = new ToolDefinitionBuilder(mcp);
        _postProcessor = new DeterministicChatPostProcessor(() => _personalityRuntime.Snapshot.Profile);
        _selfMemorySummarizer = selfMemorySummarizer ?? new SelfMemorySummarizer(mcp);
        _searchFallbackExecutor = searchFallbackExecutor ?? new SearchFallbackExecutor(_searchOrchestrator);
        _contextAnchoringService = contextAnchoringService ?? new ContextAnchoringService(
            _dialogueStore,
            _searchOrchestrator,
            _timeProvider);
        _utilityIntentHandler = utilityIntentHandler ?? new UtilityIntentHandler();
        _conversationSegmenter = conversationSegmenter ?? new ConversationSegmenter();
        _miniActionableExtractor = miniActionableExtractor ?? new MiniActionableExtractor(_llm);
        _segmentExecutionCoordinator = segmentExecutionCoordinator ?? new SegmentExecutionCoordinator();
        _unifiedResponseComposer = unifiedResponseComposer ?? new UnifiedResponseComposer();
        _toolAliasResolver = new Tools.ToolAliasResolver(mcp);
        _footmanRouter = footmanRouter;
        _autoMemoryExtractor = autoMemoryExtractor;

        _history.Add(ChatMessage.System(BuildEffectiveSystemPrompt()));

        var personalitySnapshot = _personalityRuntime.Snapshot;
        EmitPersonalityAuditSnapshot(personalitySnapshot, _activePersonalityId);
    }
}