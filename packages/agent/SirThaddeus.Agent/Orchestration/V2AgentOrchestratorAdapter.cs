using SirThaddeus.LlmClient;
using SirThaddeus.Agent.Tools;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.AuditLog;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A wrapper around TurnPipeline that implements the legacy IAgentOrchestrator interface
/// so it can be swapped into the main app via a feature flag without breaking the world.
/// </summary>
public sealed class V2AgentOrchestratorAdapter : IAgentOrchestrator
{
    private readonly TurnPipeline _pipeline;
    private readonly IAgentOrchestrator _legacyFallback;
    private readonly ToolDefinitionBuilder _toolBuilder;
    private readonly AgentOrchestrator? _legacyAgent;
    private readonly IAuditLogger _audit;
    private readonly string _systemPrompt;

    public V2AgentOrchestratorAdapter(
        IAgentOrchestrator legacyFallback,
        ToolDefinitionBuilder toolBuilder,
        IAuditLogger audit,
        string systemPrompt,
        Func<IDeterministicIntentExecutor, TurnPipeline> pipelineFactory)
    {
        _legacyFallback = legacyFallback;
        _toolBuilder = toolBuilder;
        _audit = audit;
        _legacyAgent = legacyFallback as AgentOrchestrator;
        _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? "You are Sir Thaddeus." : systemPrompt;

        // The executor needs _legacyFallback, and the pipeline needs the executor,
        // so we use a factory to break the circular dependency.
        var executor = new LegacyDeterministicExecutor(legacyFallback, audit);
        _pipeline = pipelineFactory(executor);
    }

    public bool ContextLocked 
    { 
        get => _legacyFallback.ContextLocked; 
        set => _legacyFallback.ContextLocked = value; 
    }

    public async Task<AgentResponse> ProcessAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _toolBuilder.BuildAsync(
                memoryEnabled: _legacyAgent?.MemoryEnabled ?? true,
                panicModeEnabled: _legacyAgent?.PanicModeEnabled ?? false,
                safeModeEnabled: _legacyAgent?.SafeModeEnabled ?? false,
                logEvent: LogEvent,
                cancellationToken: cancellationToken);

            // Phase A/B adapter: lightweight history scaffold.
            // Full history ownership remains in legacy orchestrator until full cutover.
            var context = new TurnContext(
                userMessage,
                [
                    LlmClient.ChatMessage.System(_systemPrompt),
                    LlmClient.ChatMessage.User(userMessage)
                ],
                tools,
                LogEvent
            );

            return await _pipeline.ExecuteAsync(context, cancellationToken);
        }
        catch (NotSupportedException ex) when (ex.Message.StartsWith("v2_", StringComparison.OrdinalIgnoreCase))
        {
            LogEvent("V2_PIPELINE_FALLBACK", ex.Message);
            return await _legacyFallback.ProcessAsync(userMessage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogEvent("V2_PIPELINE_ERROR", ex.Message);
            return await _legacyFallback.ProcessAsync(userMessage, cancellationToken);
        }
    }

    private void LogEvent(string action, string message)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "v2_pipeline",
            Action = action,
            Result = message
        });
    }

    public void ResetConversation() => _legacyFallback.ResetConversation();

    public void SeedDialogueState(DialogueState state) => _legacyFallback.SeedDialogueState(state);

    public DialogueContextSnapshot GetContextSnapshot() => _legacyFallback.GetContextSnapshot();

    public Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default) 
        => _legacyFallback.GetAvailableToolCountAsync(cancellationToken);
}
