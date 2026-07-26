using System.Collections.Concurrent;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class AutoMemoryExtractStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("AutoMemoryExtract", new AutoMemoryExtractStep(extractor: null).Name);

    [Fact]
    public async Task No_op_when_extractor_is_null()
    {
        var step = new AutoMemoryExtractStep(extractor: null);
        var ctx = NewContext("hi", assistantDraft: "hello back");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Ephemeral_turn_never_enqueues_memory_writes()
    {
        var extractor = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(extractor);
        var ctx = NewContext(
            "remember this secret",
            assistantDraft: "I will not retain it") with
        {
            MemoryAccess = TurnMemoryAccess.Ephemeral,
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(extractor.ExtractionCalls);
        Assert.Empty(extractor.ChunkCalls);
    }

    [Fact]
    public async Task Fires_user_extraction_and_user_chunk_when_user_text_present()
    {
        var ex = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(ex);
        var ctx = NewContext("remember I moved to Olympia", assistantDraft: null);

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("remember I moved to Olympia", Assert.Single(ex.ExtractionCalls).UserMessage);
        Assert.Contains(ex.ChunkCalls, c => c.Role == "user" && c.Text == "remember I moved to Olympia");
    }

    [Fact]
    public async Task Fires_assistant_chunk_only_when_draft_present()
    {
        var ex = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(ex);
        var ctx = NewContext("hi", assistantDraft: "Hello there, Mark.");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Contains(ex.ChunkCalls, c => c.Role == "assistant" && c.Text == "Hello there, Mark.");
    }

    [Fact]
    public async Task Skips_blank_user_text_and_blank_draft()
    {
        // Absent inputs mean nothing worth storing. The step must not
        // call the extractor with empty payloads.
        var ex = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(ex);
        var ctx = NewContext("   ", assistantDraft: "  ");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Empty(ex.ExtractionCalls);
        Assert.Empty(ex.ChunkCalls);
    }

    [Fact]
    public async Task Profile_id_getter_is_consulted_per_turn()
    {
        var ex = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(ex, activeProfileIdGetter: _ => "butler");
        var ctx = NewContext("save that I like tea", assistantDraft: "Got it.");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var call = Assert.Single(ex.ExtractionCalls);
        Assert.Equal("butler", call.ActiveProfileId);
    }

    [Fact]
    public async Task Returns_same_context_reference()
    {
        // Writing memory is a side effect, not a context mutation.
        // Downstream steps should see the context unchanged.
        var ex = new RecordingExtractor();
        var step = new AutoMemoryExtractStep(ex);
        var ctx = NewContext("hi", assistantDraft: "hello");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new AutoMemoryExtractStep(new RecordingExtractor());
        var ctx = NewContext("hi", assistantDraft: "hello");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext NewContext(string userText, string? assistantDraft) => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = userText,
        AssistantDraft = assistantDraft,
    };

    private sealed record ExtractionCall(string UserMessage, string? ActiveProfileId, string TurnId);
    private sealed record ChunkCall(string Text, string? ConversationId, string TurnId, string Role);

    private sealed class RecordingExtractor : IAutoMemoryExtractor
    {
        public ConcurrentBag<ExtractionCall> ExtractionCalls { get; } = new();
        public ConcurrentBag<ChunkCall> ChunkCalls { get; } = new();

        public void FireAndForgetExtraction(string userMessage, string? activeProfileId, string turnId)
            => ExtractionCalls.Add(new ExtractionCall(userMessage, activeProfileId, turnId));

        public void FireAndForgetConversationChunk(string text, string? conversationId, string turnId, string role)
            => ChunkCalls.Add(new ChunkCall(text, conversationId, turnId, role));
    }
}
