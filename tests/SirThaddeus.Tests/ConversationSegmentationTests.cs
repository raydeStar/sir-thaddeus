using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class ConversationSegmentationTests
{
    [Fact]
    public void Segmenter_GreetingOnly_HasNoActionable()
    {
        var segmenter = new ConversationSegmenter();
        var result = segmenter.Segment("Hey you there? Hope you're doing well.");

        Assert.False(result.HasActionable);
        Assert.True(result.HighConfidence);
        Assert.Equal("no_actionable", result.ConfidenceReason);
    }

    [Fact]
    public void Segmenter_MixedConversation_SeparatesActionableFromSocial()
    {
        var segmenter = new ConversationSegmenter();
        var result = segmenter.Segment(
            "Hey, how are you today? What's the weather like in Seattle? Anyway I got in trouble at school.");

        Assert.True(result.HasActionable);
        Assert.True(result.HighConfidence);
        Assert.Contains(result.Segments, s => s.IsActionable && s.Text.Contains("weather", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Segments, s => !s.IsActionable);
    }

    [Fact]
    public void Segmenter_TwoActionablesInOneSentence_SplitsClauses()
    {
        var segmenter = new ConversationSegmenter();
        var result = segmenter.Segment("What's the weather and what time does Target close?");

        var actionable = result.Segments.Where(s => s.IsActionable).ToList();
        Assert.True(actionable.Count >= 2);
    }

    [Fact]
    public async Task MiniExtractor_ReturnsExactSpanOffsets()
    {
        const string message = "Hey there. Can you pull up local news in Rexburg, ID? Thanks bye.";
        var snippet = "local news in Rexburg, ID";
        var start = message.IndexOf(snippet, StringComparison.Ordinal);
        var end = start + snippet.Length;

        var llm = new FakeLlmClient((msgs, _) => new LlmResponse
        {
            IsComplete = true,
            Content = $$"""
                        {"actionables":[{"startIndex":{{start}},"endIndex":{{end}}}]}
                        """,
            FinishReason = "stop"
        });

        var extractor = new MiniActionableExtractor(llm);
        var spans = await extractor.TryExtractAsync(message, maxActionables: 2);

        Assert.Single(spans);
        Assert.Equal(start, spans[0].StartIndex);
        Assert.Equal(end, spans[0].EndIndex);
        Assert.Equal(snippet, spans[0].Text);
        Assert.True(spans[0].IsActionable);
    }

    [Fact]
    public async Task ExecutionCoordinator_CapsOnlyToolUsingActionables()
    {
        var segments = new[]
        {
            new ConversationSegment
            {
                SegmentId = "seg-1",
                Text = "2+2",
                Order = 0,
                StartIndex = 0,
                EndIndex = 3,
                IsActionable = true,
                Confidence = 0.95
            },
            new ConversationSegment
            {
                SegmentId = "seg-2",
                Text = "what's the weather in seattle",
                Order = 1,
                StartIndex = 4,
                EndIndex = 33,
                IsActionable = true,
                Confidence = 0.95
            },
            new ConversationSegment
            {
                SegmentId = "seg-3",
                Text = "latest local news",
                Order = 2,
                StartIndex = 34,
                EndIndex = 51,
                IsActionable = true,
                Confidence = 0.95
            }
        };

        var coordinator = new SegmentExecutionCoordinator();
        var plan = await coordinator.ExecuteAsync(new SegmentExecutionRequest
        {
            ActionableSegments = segments,
            MaxToolUsingActionables = 1,
            ExecuteActionableAsync = (segment, _) =>
            {
                var usedTools = segment.SegmentId is "seg-2" or "seg-3";
                return Task.FromResult(new SegmentExecutionResult
                {
                    SegmentId = segment.SegmentId,
                    SegmentText = segment.Text,
                    Intent = "test",
                    Success = true,
                    ResponseText = segment.Text,
                    UsedTools = usedTools,
                    ToolCallCount = usedTools ? 1 : 0
                });
            }
        });

        Assert.Equal(2, plan.Executed.Count); // non-tool + first tool
        Assert.Single(plan.Deferred);         // second tool deferred
        Assert.Equal("seg-3", plan.Deferred[0].SegmentId);
    }
}

