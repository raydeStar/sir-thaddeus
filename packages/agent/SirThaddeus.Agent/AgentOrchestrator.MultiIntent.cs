using System.Text.RegularExpressions;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Tools;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private static bool IsMultiIntentBypassActive() => MultiIntentBypassDepth.Value > 0;

    private static IDisposable EnterMultiIntentBypassScope() => new MultiIntentBypassScope();

    private sealed class MultiIntentBypassScope : IDisposable
    {
        private bool _disposed;

        public MultiIntentBypassScope()
        {
            MultiIntentBypassDepth.Value++;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            MultiIntentBypassDepth.Value = Math.Max(0, MultiIntentBypassDepth.Value - 1);
        }
    }

    private async Task<AgentResponse?> TryProcessMultiIntentTurnAsync(
        string userMessage,
        List<ToolCallRecord> aggregateToolCalls,
        CancellationToken cancellationToken)
    {
        var lowerMessage = (userMessage ?? string.Empty).Trim().ToLowerInvariant();
        if (SearchModeRouter.IsFollowUpMessage(lowerMessage) ||
            lowerMessage.Contains("anything else", StringComparison.Ordinal) ||
            lowerMessage.Contains("what else", StringComparison.Ordinal))
            return null;

        var segmentation = _conversationSegmenter.Segment(userMessage ?? string.Empty);
        if (!segmentation.HasActionable)
            return null; // Explicit no-actionable fast path

        var actionableSegments = segmentation.Segments
            .Where(s => s.IsActionable)
            .OrderBy(s => s.Order)
            .ToList();

        var nonActionableContext = segmentation.Segments
            .Where(s => !s.IsActionable)
            .Select(s => s.Text.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // Keep single-intent throughput untouched unless the turn is truly
        // multi-actionable or clearly wrapped in social/chit-chat context.
        var hasSocialWrapper = nonActionableContext.Any(IsSocialWrapperSegment);
        var mixedConversationalTurn = actionableSegments.Count > 1 || hasSocialWrapper;
        if (!mixedConversationalTurn)
            return null;

        if (!segmentation.HighConfidence)
        {
            IReadOnlyList<ConversationSegment> fallback = [];
            try
            {
                fallback = await _miniActionableExtractor.TryExtractAsync(
                    userMessage ?? string.Empty,
                    maxActionables: 2,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                LogEvent("SEGMENTATION_FALLBACK_SKIPPED",
                    $"reason=mini_extractor_error, error={ex.Message}");
            }

            if (fallback.Count > 0)
            {
                actionableSegments = fallback
                    .OrderBy(s => s.StartIndex)
                    .ToList();
                LogEvent("SEGMENTATION_FALLBACK_USED",
                    $"reason={segmentation.ConfidenceReason}, actionables={actionableSegments.Count}");
            }
        }

        if (actionableSegments.Count == 0)
            return null;

        LogEvent("SEGMENT_PLAN",
            $"segments={segmentation.Segments.Count}, actionables={actionableSegments.Count}, " +
            $"high_confidence={segmentation.HighConfidence}, reason={segmentation.ConfidenceReason}");

        var executionPlan = await _segmentExecutionCoordinator.ExecuteAsync(
            new SegmentExecutionRequest
            {
                ActionableSegments = actionableSegments,
                MaxToolUsingActionables = 2,
                ExecuteActionableAsync = ExecuteActionableSegmentAsync
            },
            cancellationToken);

        var executedCalls = executionPlan.Executed
            .SelectMany(e => e.ToolCallsMade)
            .ToList();
        aggregateToolCalls.AddRange(executedCalls);

        var composedText = _unifiedResponseComposer.Compose(new UnifiedResponseComposeRequest
        {
            OriginalMessage = userMessage ?? string.Empty,
            NonActionableContext = nonActionableContext,
            Executed = executionPlan.Executed,
            Deferred = executionPlan.Deferred
        });

        AppendAssistantMessage(composedText);
        LogEvent("SEGMENT_EXECUTION_SUMMARY",
            $"detected={segmentation.Segments.Count}, actionable={actionableSegments.Count}, " +
            $"executed={executionPlan.Executed.Count}, deferred={executionPlan.Deferred.Count}");
        LogEvent("AGENT_RESPONSE", composedText);

        var llmRoundTrips = executionPlan.Executed.Sum(e => e.LlmRoundTrips);
        var success = executionPlan.Executed.Count == 0 || executionPlan.Executed.All(e => e.Success);
        var allowToolResultPersonalityPresentation = executedCalls.Any(c =>
            IsPersonalityPresentationEligibleTool(c.ToolName));

        // Propagate the first briefing payload so the UI can display it.
        var briefing = executionPlan.Executed
            .Select(e => e.DeepDiveBriefing)
            .FirstOrDefault(b => b is not null);

        return new AgentResponse
        {
            Text = composedText,
            Success = success,
            ToolCallsMade = aggregateToolCalls,
            LlmRoundTrips = llmRoundTrips,
            DeepDiveBriefing = briefing,
            AllowToolResultPersonalityPresentation = allowToolResultPersonalityPresentation
        };
    }

    private async Task<ConversationSegmentation.SegmentExecutionResult> ExecuteActionableSegmentAsync(
        ConversationSegmentation.ConversationSegment segment,
        CancellationToken cancellationToken)
    {
        var historyCountBefore = _history.Count;
        try
        {
            using var _ = EnterMultiIntentBypassScope();
            using var segmentScope = ConversationSegmentation.SegmentExecutionContext.Enter(segment.SegmentId);
            var segmentResponse = await ProcessAsync(segment.Text, cancellationToken);

            var segmentToolCalls = segmentResponse.ToolCallsMade
                .Where(CountAsToolUsingCall)
                .ToList();

            var inferredIntent = InferIntentFromToolCalls(segment.Text, segmentToolCalls);
            LogEvent("SEGMENT_EXECUTED",
                $"segment_id={segment.SegmentId}, segment_order={segment.Order}, " +
                $"segment_intent={inferredIntent}, tool_call_count={segmentToolCalls.Count}");

            return new ConversationSegmentation.SegmentExecutionResult
            {
                SegmentId = segment.SegmentId,
                SegmentText = segment.Text,
                Intent = inferredIntent,
                Success = segmentResponse.Success,
                ResponseText = segmentResponse.Text,
                UsedTools = segmentToolCalls.Count > 0,
                ToolCallCount = segmentToolCalls.Count,
                ToolCallsMade = segmentResponse.ToolCallsMade,
                LlmRoundTrips = segmentResponse.LlmRoundTrips,
                Error = segmentResponse.Error,
                DeepDiveBriefing = segmentResponse.DeepDiveBriefing
            };
        }
        catch (Exception ex)
        {
            LogEvent("SEGMENT_EXECUTION_ERROR",
                $"segment_id={segment.SegmentId}, error={ex.Message}");
            return new ConversationSegmentation.SegmentExecutionResult
            {
                SegmentId = segment.SegmentId,
                SegmentText = segment.Text,
                Intent = "error",
                Success = false,
                ResponseText = $"I hit an issue while handling \"{Truncate(segment.Text, 48)}\".",
                UsedTools = false,
                ToolCallCount = 0,
                ToolCallsMade = [],
                LlmRoundTrips = 0,
                Error = ex.Message
            };
        }
        finally
        {
            // Recursive segment calls should not pollute top-level history.
            while (_history.Count > historyCountBefore)
                _history.RemoveAt(_history.Count - 1);
        }
    }

    private static bool CountAsToolUsingCall(ToolCallRecord record)
    {
        var name = (record.ToolName ?? "").Trim().ToLowerInvariant();
        if (name.Length == 0)
            return false;

        // Memory retrieval prefetch is not counted toward multi-intent
        // tool-action caps.
        return name is not "memoryretrieve" and not "memory_retrieve";
    }

    private static bool IsPersonalityPresentationEligibleTool(string? toolName)
    {
        var normalized = (toolName ?? "").Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        return normalized.Contains("web_search", StringComparison.Ordinal) ||
               normalized.Contains("websearch", StringComparison.Ordinal) ||
               normalized.Contains("browser_navigate", StringComparison.Ordinal) ||
               normalized.Contains("browsernavigate", StringComparison.Ordinal);
    }

    private static string InferIntentFromToolCalls(
        string segmentText,
        IReadOnlyList<ToolCallRecord> toolCalls)
    {
        if (toolCalls.Any(c => c.ToolName.Contains("weather", StringComparison.OrdinalIgnoreCase)))
            return "weather";
        if (toolCalls.Any(c => c.ToolName.Contains("time", StringComparison.OrdinalIgnoreCase)))
            return "time";
        if (toolCalls.Any(c => c.ToolName.Contains("search", StringComparison.OrdinalIgnoreCase)))
            return "lookup";
        if (toolCalls.Any())
            return "tool";

        var lower = segmentText.ToLowerInvariant();
        if (lower.Contains("weather", StringComparison.Ordinal))
            return "weather";
        if (lower.Contains("news", StringComparison.Ordinal))
            return "lookup";
        return "chat";
    }

    private static bool IsSocialWrapperSegment(string text)
    {
        var lower = (text ?? "").Trim().ToLowerInvariant();
        if (lower.Length == 0)
            return false;

        return Regex.IsMatch(lower, @"\b(?:hey|hi|hello|yo)\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\bhow are you\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\bthanks?\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\bthank you\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\banyway\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\b(?:bye|goodbye|gotta go|see ya)\b", RegexOptions.CultureInvariant) ||
               Regex.IsMatch(lower, @"\b(?:rough day|in trouble)\b", RegexOptions.CultureInvariant);
    }
}
