using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests;

public sealed class PipelineClassifierTests
{
    [Fact]
    public void MapIntentToType_ChatOnly_MapsToChat()
    {
        Assert.Equal(PipelineIntentType.Chat, RequestClassifier.MapIntentToType(Intents.ChatOnly));
    }

    [Fact]
    public void MapIntentToType_LookupSearch_MapsToWebSearch()
    {
        Assert.Equal(PipelineIntentType.WebSearch, RequestClassifier.MapIntentToType(Intents.LookupSearch));
    }

    [Fact]
    public void MapIntentToType_LookupNews_MapsToWebSearch()
    {
        Assert.Equal(PipelineIntentType.WebSearch, RequestClassifier.MapIntentToType(Intents.LookupNews));
    }

    [Fact]
    public void MapIntentToType_FileTask_MapsToFileRead()
    {
        Assert.Equal(PipelineIntentType.FileRead, RequestClassifier.MapIntentToType(Intents.FileTask));
    }

    [Fact]
    public void MapIntentToType_SystemTask_MapsToCodeExecution()
    {
        Assert.Equal(PipelineIntentType.CodeExecution, RequestClassifier.MapIntentToType(Intents.SystemTask));
    }

    [Fact]
    public void MapIntentToType_UnknownIntent_MapsToUnknown()
    {
        Assert.Equal(PipelineIntentType.Unknown, RequestClassifier.MapIntentToType("unknown_garbage"));
    }

    [Fact]
    public async Task ClassifyAsync_EmptyIntents_ReturnsEmpty()
    {
        var router = new StubRouter(Intents.ChatOnly, 1.0);
        var classifier = new RequestClassifier(router);

        var result = await classifier.ClassifyAsync(new PreprocessorResult
        {
            Intents = [],
            IsMultiIntent = false
        });

        Assert.Empty(result.ClassifiedIntents);
        Assert.True(result.AllDeterministic);
    }

    [Fact]
    public async Task ClassifyAsync_HighConfidence_IsDeterministic()
    {
        var router = new StubRouter(Intents.LookupNews, 0.95);
        var classifier = new RequestClassifier(router);

        var result = await classifier.ClassifyAsync(new PreprocessorResult
        {
            Intents =
            [
                new PipelineIntent
                {
                    OriginalFragment = "what's the news",
                    NormalizedRequest = "what's the news",
                    Order = 0
                }
            ],
            IsMultiIntent = false
        });

        Assert.Single(result.ClassifiedIntents);
        Assert.True(result.AllDeterministic);
        Assert.Equal(Intents.LookupNews, result.ClassifiedIntents[0].ResolvedIntent);
        Assert.Equal(PipelineIntentType.WebSearch, result.ClassifiedIntents[0].MappedType);
    }

    [Fact]
    public async Task ClassifyAsync_LowConfidence_NotDeterministic()
    {
        var router = new StubRouter(Intents.LookupSearch, 0.6);
        var classifier = new RequestClassifier(router);

        var result = await classifier.ClassifyAsync(new PreprocessorResult
        {
            Intents =
            [
                new PipelineIntent
                {
                    OriginalFragment = "hmm something",
                    NormalizedRequest = "hmm something",
                    Order = 0
                }
            ],
            IsMultiIntent = false
        });

        Assert.False(result.AllDeterministic);
    }

    [Fact]
    public async Task ClassifyAsync_PolicyGateAttached()
    {
        var router = new StubRouter(Intents.LookupSearch, 0.95, needsSearch: true);
        var classifier = new RequestClassifier(router);

        var result = await classifier.ClassifyAsync(new PreprocessorResult
        {
            Intents =
            [
                new PipelineIntent
                {
                    OriginalFragment = "search for AI news",
                    NormalizedRequest = "AI news",
                    Order = 0
                }
            ],
            IsMultiIntent = false
        });

        Assert.NotNull(result.ClassifiedIntents[0].Policy);
    }

    [Fact]
    public async Task ClassifyAsync_ContextFlags_ArePassedToRouter()
    {
        var router = new StubRouter(Intents.LookupSearch, 0.95);
        var classifier = new RequestClassifier(router);

        await classifier.ClassifyAsync(
            new PreprocessorResult
            {
                Intents =
                [
                    new PipelineIntent
                    {
                        OriginalFragment = "tell me more",
                        NormalizedRequest = "tell me more",
                        Order = 0
                    }
                ]
            },
            new ClassifierContext
            {
                HasRecentFirstPrinciplesRationale = true,
                HasRecentSearchResults = true
            });

        Assert.NotNull(router.LastRequest);
        Assert.True(router.LastRequest!.HasRecentFirstPrinciplesRationale);
        Assert.True(router.LastRequest.HasRecentSearchResults);
    }

    private sealed class StubRouter : IRouter
    {
        private readonly string _intent;
        private readonly double _confidence;
        private readonly bool _needsSearch;

        public RouterRequest? LastRequest { get; private set; }

        public StubRouter(string intent, double confidence, bool needsSearch = false)
        {
            _intent = intent;
            _confidence = confidence;
            _needsSearch = needsSearch;
        }

        public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new RouterOutput
            {
                Intent = _intent,
                Confidence = _confidence,
                NeedsSearch = _needsSearch
            });
        }
    }
}
