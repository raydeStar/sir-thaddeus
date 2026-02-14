using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Execution;

public sealed class RecordingLlmClient : ILlmClient
{
    private readonly ILlmClient _inner;
    private readonly TraceRecorder _traceRecorder;
    private readonly List<RecordedLlmTurn> _recordedTurns = [];
    private int _turnIndex;

    public RecordingLlmClient(ILlmClient inner, TraceRecorder traceRecorder)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _traceRecorder = traceRecorder ?? throw new ArgumentNullException(nameof(traceRecorder));
    }

    public IReadOnlyList<RecordedLlmTurn> RecordedTurns => _recordedTurns;

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return ChatInternalAsync(messages, tools, maxTokensOverride: null, cancellationToken);
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return ChatInternalAsync(messages, tools, maxTokensOverride, cancellationToken);
    }

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => _inner.GetModelNameAsync(cancellationToken);

    private async Task<LlmResponse> ChatInternalAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride,
        CancellationToken cancellationToken)
    {
        var callId = _traceRecorder.NextCallId("llm");
        var startedAt = DateTimeOffset.UtcNow;
        _traceRecorder.RecordLlmCall(callId, messages, tools);

        try
        {
            var response = maxTokensOverride is null
                ? await _inner.ChatAsync(messages, tools, cancellationToken)
                : await _inner.ChatAsync(messages, tools, maxTokensOverride.Value, cancellationToken);

            var endedAt = DateTimeOffset.UtcNow;
            _traceRecorder.RecordLlmResult(callId, response, startedAt, endedAt);

            _recordedTurns.Add(new RecordedLlmTurn
            {
                Index = Interlocked.Increment(ref _turnIndex) - 1,
                IsComplete = response.IsComplete,
                Content = response.Content,
                FinishReason = response.FinishReason,
                ToolCalls = response.ToolCalls,
                MaxTokensOverride = maxTokensOverride
            });

            return response;
        }
        catch (Exception ex)
        {
            var endedAt = DateTimeOffset.UtcNow;
            _traceRecorder.RecordLlmError(callId, ex, startedAt, endedAt);
            throw;
        }
    }
}
