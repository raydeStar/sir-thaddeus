using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Execution;

public sealed class ReplayLlmClient : ILlmClient
{
    private readonly HarnessFixture _fixture;
    private readonly TraceRecorder _traceRecorder;
    private int _index;

    public ReplayLlmClient(HarnessFixture fixture, TraceRecorder traceRecorder)
    {
        _fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
        _traceRecorder = traceRecorder ?? throw new ArgumentNullException(nameof(traceRecorder));
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return NextResponseAsync(messages, tools, maxTokensOverride: null);
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return NextResponseAsync(messages, tools, maxTokensOverride);
    }

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(_fixture.Metadata.Model);

    private Task<LlmResponse> NextResponseAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int? maxTokensOverride)
    {
        var callId = _traceRecorder.NextCallId("llm");
        var startedAt = DateTimeOffset.UtcNow;
        _traceRecorder.RecordLlmCall(callId, messages, tools);

        if (_index >= _fixture.LlmTurns.Count)
        {
            var exhausted = new LlmResponse
            {
                IsComplete = true,
                Content = "Replay fixture exhausted before conversation completed.",
                FinishReason = "stop"
            };
            _traceRecorder.RecordLlmResult(callId, exhausted, startedAt, DateTimeOffset.UtcNow);
            return Task.FromResult(exhausted);
        }

        var turn = _fixture.LlmTurns[_index++];
        if (maxTokensOverride is not null &&
            turn.MaxTokensOverride is not null &&
            turn.MaxTokensOverride != maxTokensOverride)
        {
            var mismatch = new LlmResponse
            {
                IsComplete = true,
                Content = $"Replay mismatch for maxTokensOverride. Expected {turn.MaxTokensOverride}, got {maxTokensOverride}.",
                FinishReason = "stop"
            };
            _traceRecorder.RecordLlmResult(callId, mismatch, startedAt, DateTimeOffset.UtcNow);
            return Task.FromResult(mismatch);
        }

        var response = new LlmResponse
        {
            IsComplete = turn.IsComplete,
            Content = turn.Content,
            FinishReason = turn.FinishReason,
            ToolCalls = turn.ToolCalls
        };

        _traceRecorder.RecordLlmResult(callId, response, startedAt, DateTimeOffset.UtcNow);
        return Task.FromResult(response);
    }
}
