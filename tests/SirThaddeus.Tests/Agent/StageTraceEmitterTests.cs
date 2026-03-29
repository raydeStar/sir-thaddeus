using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests;

public sealed class StageTraceEmitterTests
{
    [Fact]
    public void RecordPreprocess_CapturesIntentCount()
    {
        var emitter = new StageTraceEmitter("test-123");
        emitter.RecordPreprocess(new PreprocessorResult
        {
            Intents =
            [
                new PipelineIntent { OriginalFragment = "hello", NormalizedRequest = "hello", Order = 0 },
                new PipelineIntent { OriginalFragment = "news", NormalizedRequest = "news", Order = 1 }
            ],
            IsMultiIntent = true
        }, 5);

        var trace = emitter.BuildTrace("hello news");
        Assert.Single(trace.Stages);
        Assert.Equal(PipelineStageName.Preprocess, trace.Stages[0].Stage);
        Assert.Equal("multi_intent", trace.Stages[0].Decision);
        Assert.Equal("test-123", trace.CorrelationId);
    }

    [Fact]
    public void MultiStage_TracksAll()
    {
        var emitter = new StageTraceEmitter();

        emitter.RecordPreprocess(new PreprocessorResult
        {
            Intents = [new PipelineIntent { OriginalFragment = "test", NormalizedRequest = "test", Order = 0 }],
            IsMultiIntent = false
        }, 1);

        emitter.RecordClassify(new ClassifierResult
        {
            ClassifiedIntents =
            [
                new ClassifiedIntent
                {
                    Source = new PipelineIntent { OriginalFragment = "test", NormalizedRequest = "test", Order = 0 },
                    ResolvedIntent = "lookup_search",
                    MappedType = PipelineIntentType.WebSearch,
                    Confidence = 0.9
                }
            ],
            AllDeterministic = true
        }, 10);

        emitter.RecordCompose(new ComposerResult
        {
            FinalResponse = "Here is the result.",
            WasSanitized = false,
            Warnings = []
        }, 2);

        var trace = emitter.BuildTrace("test");
        Assert.Equal(3, trace.Stages.Count);
        Assert.Equal(PipelineStageName.Preprocess, trace.Stages[0].Stage);
        Assert.Equal(PipelineStageName.Classify, trace.Stages[1].Stage);
        Assert.Equal(PipelineStageName.Compose, trace.Stages[2].Stage);
    }

    [Fact]
    public void ToJsonLines_ProducesOneLinePerStage()
    {
        var emitter = new StageTraceEmitter();
        emitter.RecordPreprocess(new PreprocessorResult
        {
            Intents = [new PipelineIntent { OriginalFragment = "x", NormalizedRequest = "x", Order = 0 }]
        }, 1);
        emitter.RecordCompose(new ComposerResult { FinalResponse = "y" }, 1);

        var lines = emitter.ToJsonLines().Split('\n');
        Assert.Equal(2, lines.Length);
        // Enums serialize as numbers in System.Text.Json by default
        Assert.Contains("\"stage\":0", lines[0]); // Preprocess = 0
        Assert.Contains("\"stage\":4", lines[1]); // Compose = 4
    }

    [Fact]
    public void BeginEnd_CapturesDuration()
    {
        var emitter = new StageTraceEmitter();
        emitter.BeginStage(PipelineStageName.Execute, "2 queries");
        System.Threading.Thread.Sleep(10);
        emitter.EndStage("2 ok, 0 failed", "500 chars");

        var trace = emitter.BuildTrace("test");
        Assert.Single(trace.Stages);
        Assert.True(trace.Stages[0].DurationMs >= 5);
        Assert.Equal(PipelineStageName.Execute, trace.Stages[0].Stage);
    }
}
