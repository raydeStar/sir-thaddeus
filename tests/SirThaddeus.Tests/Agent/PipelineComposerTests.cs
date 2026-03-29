using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests;

public sealed class PipelineComposerTests
{
    [Fact]
    public void SingleSuccess_ProducesResponseText()
    {
        var composer = new PipelineComposer();
        var result = composer.Compose(
            "What's the news?",
            SingleIntentPreprocessed("What's the news"),
            SingleExecuted("Here are the latest headlines about AI...", true));

        Assert.Contains("headlines", result.FinalResponse, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void AllFailed_ProducesGracefulMessage()
    {
        var composer = new PipelineComposer();
        var result = composer.Compose(
            "search for something",
            SingleIntentPreprocessed("search for something"),
            SingleExecuted("", false, "timeout"));

        Assert.Contains("issue", result.FinalResponse, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void GreetingPlusAction_LeadsWithGreeting()
    {
        var preprocessed = new PreprocessorResult
        {
            Intents =
            [
                new PipelineIntent { OriginalFragment = "Hey!", NormalizedRequest = "Hey", Order = 0 },
                new PipelineIntent { OriginalFragment = "What's the news?", NormalizedRequest = "news", Order = 1, Type = PipelineIntentType.WebSearch }
            ],
            IsMultiIntent = true
        };

        var chatQuery = new BuiltQuery
        {
            Source = BuildClassifiedChat(preprocessed.Intents[0]),
            InlineAnswer = "Hey",
            RequiresExecution = false
        };
        var searchQuery = new BuiltQuery
        {
            Source = BuildClassifiedSearch(preprocessed.Intents[1]),
            SearchQuery = "news",
            RequiresExecution = true
        };

        var executed = new ExecutorResult
        {
            Segments =
            [
                new ExecutionSegmentResult { Source = chatQuery, ResponseText = "", Success = true },
                new ExecutionSegmentResult { Source = searchQuery, ResponseText = "Here are today's headlines.", Success = true }
            ]
        };

        var composer = new PipelineComposer();
        var result = composer.Compose("Hey! What's the news?", preprocessed, executed);

        Assert.StartsWith("Hey!", result.FinalResponse);
        Assert.Contains("headlines", result.FinalResponse, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitizer_IsApplied()
    {
        var composer = new PipelineComposer(raw => raw.Replace("bad", "good"));
        var result = composer.Compose(
            "test",
            SingleIntentPreprocessed("test"),
            SingleExecuted("This is bad content", true));

        Assert.Contains("good", result.FinalResponse);
        Assert.True(result.WasSanitized);
    }

    [Fact]
    public void EmptyExecution_ReturnsFallback()
    {
        var composer = new PipelineComposer();
        var result = composer.Compose(
            "test",
            new PreprocessorResult { Intents = [] },
            new ExecutorResult { Segments = [] });

        Assert.NotEmpty(result.FinalResponse);
        Assert.NotEmpty(result.Warnings);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static PreprocessorResult SingleIntentPreprocessed(string text) => new()
    {
        Intents =
        [
            new PipelineIntent
            {
                OriginalFragment = text,
                NormalizedRequest = text,
                Order = 0,
                Type = PipelineIntentType.WebSearch
            }
        ],
        IsMultiIntent = false
    };

    private static ExecutorResult SingleExecuted(string text, bool success, string error = "")
    {
        var query = new BuiltQuery
        {
            Source = new ClassifiedIntent
            {
                Source = new PipelineIntent
                {
                    OriginalFragment = "test",
                    NormalizedRequest = "test",
                    Order = 0
                },
                ResolvedIntent = Intents.LookupSearch,
                MappedType = PipelineIntentType.WebSearch,
                Confidence = 0.95
            },
            SearchQuery = "test",
            RequiresExecution = true
        };

        return new ExecutorResult
        {
            Segments =
            [
                new ExecutionSegmentResult
                {
                    Source = query,
                    ResponseText = text,
                    Success = success,
                    Error = error
                }
            ]
        };
    }

    private static ClassifiedIntent BuildClassifiedChat(PipelineIntent source) => new()
    {
        Source = source,
        ResolvedIntent = Intents.ChatOnly,
        MappedType = PipelineIntentType.Chat,
        Confidence = 0.95
    };

    private static ClassifiedIntent BuildClassifiedSearch(PipelineIntent source) => new()
    {
        Source = source,
        ResolvedIntent = Intents.LookupNews,
        MappedType = PipelineIntentType.WebSearch,
        Confidence = 0.95,
        RouterOutput = new RouterOutput { Intent = Intents.LookupNews, NeedsSearch = true, Confidence = 0.95 }
    };
}
