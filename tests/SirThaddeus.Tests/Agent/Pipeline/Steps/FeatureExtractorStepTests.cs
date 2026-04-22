using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class FeatureExtractorStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name()
    {
        // Step names surface in pipeline logs + event traces. Keeping them
        // stable is part of the module's public contract.
        Assert.Equal("FeatureExtractor", new FeatureExtractorStep().Name);
    }

    [Fact]
    public async Task Populates_Features_when_none_on_context()
    {
        var step = new FeatureExtractorStep();
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hey there!" };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.NotNull(cont.Next.Features);
        // Greetings are a well-known detector in RoutingFeatures; if this
        // check regresses it signals that the feature extractor drifted.
        Assert.True(cont.Next.Features!.IsGreeting);
    }

    [Fact]
    public async Task Does_not_re_extract_when_features_already_set()
    {
        // A facade or upstream step may have seeded features (replay, tests,
        // future fast-path injectors). The extractor must be idempotent so
        // those don't get clobbered.
        var seeded = RoutingFeatures.Extract("completely different question");
        var step = new FeatureExtractorStep();
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            Features = seeded,
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(seeded, cont.Next.Features);
    }

    [Fact]
    public async Task Detects_logic_puzzle_phrasing_on_real_prompts()
    {
        // Smoke check that the extractor still lights up the logic-puzzle
        // signal for the car-wash trap — the same case we broadened the
        // detector for. Regression here would mean the logic scaffold
        // would never fire downstream.
        var step = new FeatureExtractorStep();
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "The car is dirty and needs to be washed. It is 50m away. Should I walk or drive?",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.True(cont.Next.Features!.IsLogicPuzzle);
    }

    [Fact]
    public async Task Empty_user_text_yields_features_with_no_positive_signals()
    {
        // Edge case — an empty context shouldn't crash the extractor and
        // shouldn't claim false-positive signals.
        var step = new FeatureExtractorStep();
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = string.Empty };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var features = cont.Next.Features!;
        Assert.False(features.IsGreeting);
        Assert.False(features.IsLogicPuzzle);
        Assert.Equal(0, features.WordCount);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        // Steps are called inside a larger pipeline; a pre-cancelled token
        // must throw, not quietly do work. Keeps user-initiated cancels
        // responsive.
        var step = new FeatureExtractorStep();
        var ctx = new TurnContext { ThreadId = "t1", MessageId = "m1", UserText = "hi" };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }
}
