using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using Thaddeus.Runtime.Chat.Pipeline;

namespace Thaddeus.Runtime.Chat;

public sealed partial class LmStudioAssistant
{
    /// <summary>
    /// Builds the per-turn chat pipeline from the shared production
    /// composition. This host supplies desktop-specific event, permission,
    /// memory, sanitization, and execution-control adapters only.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> (exposed to <c>Thaddeus.Runtime.Tests</c> via
    /// <c>InternalsVisibleTo</c>) so composition tests can assert security and
    /// ordering invariants without spinning up a full turn.
    /// </remarks>
    internal ChatPipeline BuildTurnPipeline(IMcpToolClient mcp, IChatEventSink sink)
    {
        var sanitize = new Func<TurnContext, string, string>((ctx, draft) =>
            AssistantResponseSanitizer.CleanChatReply(draft, ctx.UserText));
        Action<string, string>? latencyLog = IsLatencyTracingEnabled()
            ? (action, message) => _logger.LogInformation("{Action} {Message}", action, message)
            : null;

        var toolLoop = new ToolLoopStep(
            _llm, mcp, sink,
            // AuditedMcpToolClient is the single permission-enforcement
            // boundary. Gating again here would prompt twice for every tool.
            permissionGate: null,
            groupClassifier: RuntimeToolGroupClassifier.Instance,
            interceptors: Array.Empty<IToolCallInterceptor>(),
            argsRewriters:
            [
                new LocationAwarePlacesArgsRewriter(() => LocationHint),
                new FactSearchArgsRewriter(),
                new ExistenceSearchArgsRewriter()
            ],
            maxRoundTrips: MaxRoundTrips,
            maxOutputTokens: MaxOutputTokens,
            log: latencyLog,
            executionControl: ExecutionControl);

        return ProductionChatPipelineFactory.Build(new ProductionChatPipelineOptions
        {
            Mcp = mcp,
            EventSink = sink,
            PolicyStateEventSink = sink,
            PolicyStateLog = latencyLog,
            LogEvent = latencyLog,
            ExecutionControl = ExecutionControl,
            ResolveActiveProfileId = () => PersonalityRuntime?.Snapshot.Profile.Id,
            PersonalityRuntime = PersonalityRuntime,
            MemoryContextProvider = MemoryContextProvider,
            OnMemoryRecalled = async (notification, cancellationToken) =>
            {
                await _publisher.PublishMemoryRecalledAsync(
                    notification.ThreadId,
                    notification.MessageId,
                    notification.FactsCount,
                    notification.EventsCount,
                    notification.ChunksCount,
                    notification.NuggetsCount,
                    notification.Preview,
                    notification.DurationMs,
                    cancellationToken).ConfigureAwait(false);
            },
            IncludeCoreMemoryStep = true,
            CoreMemoryStore = MemoryStore,
            DialogueStateAccessor = DialogueStateAccessor,
            FootmanRouter = Footman,
            AlwaysAllowToolNames = Array.Empty<string>(),
            GuardrailsPipeline = GuardrailsPipeline,
            ToolLoop = toolLoop,
            Sanitize = sanitize,
            CompletionValidator = CompletionValidator,
            CompletionRepairLoop = CompletionRepairLoop,
            SearchFallbackExecutor = SearchFallbackExecutor,
            AutoMemoryExtractor = AutoMemoryExtractor,
        });
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
