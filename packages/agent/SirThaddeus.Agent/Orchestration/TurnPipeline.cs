using System.Text.Json;
using SirThaddeus.LlmClient;
using SirThaddeus.Agent.Tools;
using SirThaddeus.Agent.ToolLoop;

namespace SirThaddeus.Agent.Orchestration;

public sealed record TurnContext(
    string UserMessage,
    IReadOnlyList<ChatMessage> History,
    IReadOnlyList<ToolDefinition> AvailableTools,
    Action<string, string> LogEvent);

public sealed class TurnPipeline
{
    private readonly RouterV2 _router;
    private readonly IClarificationGate _clarificationGate;
    private readonly IToolRetriever _toolRetriever;
    private readonly IToolLoopExecutor _toolLoopExecutor;
    private readonly IDeterministicIntentExecutor? _deterministicExecutor;

    public TurnPipeline(
        RouterV2 router,
        IClarificationGate clarificationGate,
        IToolRetriever toolRetriever,
        IToolLoopExecutor toolLoopExecutor,
        IDeterministicIntentExecutor? deterministicExecutor = null)
    {
        _router = router;
        _clarificationGate = clarificationGate;
        _toolRetriever = toolRetriever;
        _toolLoopExecutor = toolLoopExecutor;
        _deterministicExecutor = deterministicExecutor;
    }

    public async Task<AgentResponse> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        // 1. Route
        context.LogEvent("PIPELINE_ROUTE_START", context.UserMessage);
        var decision = await _router.RouteAsync(context.UserMessage, cancellationToken);
        context.LogEvent(
            "route_decision_v2",
            JsonSerializer.Serialize(new
            {
                intent = decision.Intent,
                confidence = decision.Confidence,
                reason_codes = decision.RouteReasonCodes
            }));

        // 2. Clarification Gate
        var clarification = _clarificationGate.TryClarify(decision);
        if (clarification != null)
        {
            context.LogEvent(
                "clarify_gate_triggered",
                JsonSerializer.Serialize(new
                {
                    intent = decision.Intent,
                    confidence = decision.Confidence,
                    question = clarification.Question
                }));
            return new AgentResponse
            {
                Text = clarification.Question,
                Success = true,
                LlmRoundTrips = 1,
                ToolCallsMade = []
            };
        }

        // 3. Policy Stage
        var canonicalIntent = CanonicalizeIntentForPolicy(decision.Intent);
        var policyInput = BuildPolicyInput(canonicalIntent, decision);
        var policy = PolicyGate.Evaluate(policyInput);
        var allowedTools = PolicyGate.FilterTools(context.AvailableTools, policy);

        context.LogEvent(
            "PIPELINE_POLICY_FILTERED",
            $"Intent={canonicalIntent}, AllowedTools={allowedTools.Count}, UseToolLoop={policy.UseToolLoop}");

        // No-tool-loop paths: chat-only or deterministic (search/news/deep-dive/screen/memory-read/etc.)
        if (!policy.UseToolLoop)
        {
            if (string.Equals(canonicalIntent, Intents.ChatOnly, StringComparison.OrdinalIgnoreCase))
            {
                var chatRequest = new ToolLoopExecutionRequest
                {
                    History = context.History.ToList(),
                    Tools = [],
                    ToolCallsMade = new List<ToolCallRecord>(),
                    InitialRoundTrips = 0,
                    Decision = decision,
                    MaxRoundTrips = 1,
                    LogEvent = context.LogEvent,
                    SanitizeAssistantText = text => text
                };

                return await _toolLoopExecutor.ExecuteAsync(chatRequest, cancellationToken);
            }

            // Delegate deterministic intents (search, screen, memory-read, etc.)
            if (_deterministicExecutor != null)
            {
                context.LogEvent("PIPELINE_DETERMINISTIC_DISPATCH", $"Intent={canonicalIntent}");
                var deterministicResult = await _deterministicExecutor.TryExecuteAsync(
                    context.UserMessage, decision, canonicalIntent, cancellationToken);
                if (deterministicResult != null)
                    return deterministicResult;
            }

            // No executor or executor did not recognise intent — fail closed.
            context.LogEvent("PIPELINE_UNSUPPORTED_INTENT", $"Intent={canonicalIntent}");
            throw new NotSupportedException($"v2_unsupported_intent:{canonicalIntent}");
        }

        if (allowedTools.Count == 0)
        {
            // Executor may still handle tool-requiring intents that have no allowed tools.
            if (_deterministicExecutor != null)
            {
                var noToolResult = await _deterministicExecutor.TryExecuteAsync(
                    context.UserMessage, decision, canonicalIntent, cancellationToken);
                if (noToolResult != null)
                    return noToolResult;
            }

            throw new NotSupportedException($"v2_no_tools:{canonicalIntent}");
        }

        // 4. Tool Retrieval Stage
        var relevantTools = await _toolRetriever.RetrieveAsync(decision, context.UserMessage, allowedTools, cancellationToken);
        context.LogEvent("PIPELINE_TOOLS_RETRIEVED", $"Selected {relevantTools.Count} tools for {canonicalIntent}");

        // 5/6/7. Planning, Validation, Execution (Handled inside ToolLoopExecutor with PlanValidator)
        var request = new ToolLoopExecutionRequest
        {
            History = context.History.ToList(), // copy
            Tools = relevantTools,
            ToolCallsMade = new List<ToolCallRecord>(),
            InitialRoundTrips = 0,
            Decision = decision,
            MaxRoundTrips = 10,
            LogEvent = context.LogEvent,
            SanitizeAssistantText = text => text // simplified for this stub
        };

        var response = await _toolLoopExecutor.ExecuteAsync(request, cancellationToken);

        // 8. Compose Stage (Handled by the caller or a canonical composer)
        return response;
    }

    private static RouterOutput BuildPolicyInput(string canonicalIntent, IntentDecisionV2 decision)
    {
        var needsWeb =
            string.Equals(canonicalIntent, Intents.LookupSearch, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalIntent, Intents.LookupFact, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalIntent, Intents.LookupNews, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(canonicalIntent, Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase);

        return new RouterOutput
        {
            Intent = canonicalIntent,
            Confidence = decision.Confidence,
            NeedsWeb = needsWeb,
            NeedsSearch = needsWeb,
            RiskLevel = canonicalIntent switch
            {
                var intent when string.Equals(intent, Intents.FileTask, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(intent, Intents.ScreenObserve, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(intent, Intents.SystemTask, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(intent, Intents.MemoryWrite, StringComparison.OrdinalIgnoreCase) => "high",
                var intent when string.Equals(intent, Intents.LookupFact, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(intent, Intents.LookupNews, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(intent, Intents.LookupDeepDive, StringComparison.OrdinalIgnoreCase) => "medium",
                _ => "low"
            }
        };
    }

    private static string CanonicalizeIntentForPolicy(string intent)
    {
        return intent switch
        {
            // snake_case variants (from NN classifier / LLM)
            "chat_only" => Intents.ChatOnly,
            "utility_deterministic" => Intents.UtilityDeterministic,
            "lookup_search" => Intents.LookupSearch,
            "lookup_fact" => Intents.LookupFact,
            "lookup_news" => Intents.LookupNews,
            "lookup_deep_dive" => Intents.LookupDeepDive,
            "browse_once" => Intents.BrowseOnce,
            "one_shot_discovery" => Intents.OneShotDiscovery,
            "screen_observe" => Intents.ScreenObserve,
            "file_task" => Intents.FileTask,
            "system_task" => Intents.SystemTask,
            "memory_read" => Intents.MemoryRead,
            "memory_write" => Intents.MemoryWrite,
            "general_tool" => Intents.GeneralTool,

            // PascalCase variants (from hard-rule tier)
            "ChatOnly" => Intents.ChatOnly,
            "UtilityDeterministic" => Intents.UtilityDeterministic,
            "LookupSearch" => Intents.LookupSearch,
            "LookupFact" => Intents.LookupFact,
            "LookupNews" => Intents.LookupNews,
            "LookupDeepDive" => Intents.LookupDeepDive,
            "BrowseOnce" => Intents.BrowseOnce,
            "OneShotDiscovery" => Intents.OneShotDiscovery,
            "ScreenObserve" => Intents.ScreenObserve,
            "FileTask" => Intents.FileTask,
            "SystemExecute" => Intents.SystemTask,
            "MemoryRead" => Intents.MemoryRead,
            "MemoryWrite" => Intents.MemoryWrite,
            "GeneralTool" => Intents.GeneralTool,

            _ => Intents.GeneralTool
        };
    }
}
