using System.Collections.Concurrent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Tracing;

public sealed class TraceRecorder
{
    private readonly ConcurrentQueue<TraceStep> _steps = new();
    private int _stepIndex;
    private int _callCounter;

    public IReadOnlyList<TraceStep> Snapshot()
        => _steps.OrderBy(step => step.StepIndex).ToList();

    public string NextCallId(string prefix)
    {
        var seq = Interlocked.Increment(ref _callCounter);
        return $"{prefix}_{seq:D4}";
    }

    public void RecordLlmCall(string callId, IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
    {
        var startedAt = DateTimeOffset.UtcNow;
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "llm_call",
            CallId = callId,
            StartedAt = startedAt,
            Result = $"messages={messages.Count};tools={(tools?.Count ?? 0)}"
        });
    }

    public void RecordLlmResult(string callId, LlmResponse response, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "llm_result",
            CallId = callId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationMs = Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            Content = response.Content,
            FinishReason = response.FinishReason,
            IsComplete = response.IsComplete,
            Result = $"tool_calls={(response.ToolCalls?.Count ?? 0)}"
        });
    }

    public void RecordLlmError(string callId, Exception ex, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "llm_result",
            CallId = callId,
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationMs = Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            Error = new TraceError
            {
                Code = "llm_error",
                Message = ex.Message,
                Retriable = true
            }
        });
    }

    public void RecordToolCall(string callId, string toolName, string argumentsJson)
    {
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "tool_call",
            CallId = callId,
            ToolName = toolName,
            Arguments = argumentsJson,
            StartedAt = DateTimeOffset.UtcNow
        });
    }

    public void RecordToolResult(
        string callId,
        string toolName,
        string argumentsJson,
        string result,
        TraceError? error,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "tool_result",
            CallId = callId,
            ToolName = toolName,
            Arguments = argumentsJson,
            StartedAt = startedAt,
            EndedAt = endedAt,
            DurationMs = Math.Max(0, (long)(endedAt - startedAt).TotalMilliseconds),
            Result = result,
            Error = error
        });
    }

    public void RecordFinal(string content)
    {
        Enqueue(new TraceStep
        {
            StepIndex = NextStepIndex(),
            StepType = "final_response",
            StartedAt = DateTimeOffset.UtcNow,
            Content = content
        });
    }

    private void Enqueue(TraceStep step) => _steps.Enqueue(step);

    private int NextStepIndex() => Interlocked.Increment(ref _stepIndex);
}
