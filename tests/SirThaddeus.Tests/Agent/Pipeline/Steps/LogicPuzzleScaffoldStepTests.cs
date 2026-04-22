using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class LogicPuzzleScaffoldStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("LogicPuzzleScaffold", new LogicPuzzleScaffoldStep().Name);

    [Fact]
    public async Task No_op_when_features_missing()
    {
        // Without features, the step can't know whether the prompt is a
        // logic puzzle. Rather than re-running extraction, it leaves the
        // context untouched — the pipeline composer is expected to place
        // FeatureExtractorStep before this one.
        var step = new LogicPuzzleScaffoldStep();
        var seedSystem = ChatMessage.System("base system prompt");
        var ctx = WithMessages(userText: "any", features: null, seedSystem);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.LlmMessages, cont.Next.LlmMessages);
    }

    [Fact]
    public async Task No_op_when_features_show_no_logic_puzzle()
    {
        // Regular greeting — no scaffold should be appended.
        var step = new LogicPuzzleScaffoldStep();
        var features = RoutingFeatures.Extract("hello there");
        Assert.False(features.IsLogicPuzzle);
        var seedSystem = ChatMessage.System("base");
        var ctx = WithMessages(userText: "hello there", features: features, seedSystem);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx.LlmMessages, cont.Next.LlmMessages);
    }

    [Fact]
    public async Task Appends_scaffold_to_existing_system_message_when_logic_puzzle_detected()
    {
        var step = new LogicPuzzleScaffoldStep();
        var puzzle = "The car is dirty and needs to be washed. 50m away. Walk or drive?";
        var features = RoutingFeatures.Extract(puzzle);
        Assert.True(features.IsLogicPuzzle);

        var seedSystem = ChatMessage.System("You are Sir Thaddeus.");
        var seedUser = ChatMessage.User(puzzle);
        var ctx = WithMessages(userText: puzzle, features: features, seedSystem, seedUser);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.LlmMessages.Count);

        // The system prompt is the first entry and now carries the
        // scaffold suffix; the user message is untouched.
        Assert.Equal("system", cont.Next.LlmMessages[0].Role);
        Assert.StartsWith("You are Sir Thaddeus.", cont.Next.LlmMessages[0].Content);
        Assert.Contains("LOGIC PUZZLE MODE", cont.Next.LlmMessages[0].Content);
        Assert.Same(seedUser, cont.Next.LlmMessages[1]);
    }

    [Fact]
    public async Task Inserts_a_system_message_when_none_seeded()
    {
        // Facade hasn't seeded a system prompt — still runnable. This case
        // mainly supports tests and future orderings where the system
        // prompt is introduced by a later step.
        var step = new LogicPuzzleScaffoldStep();
        var puzzle = "Which weighs more, a pound of feathers or a pound of lead?";
        var features = RoutingFeatures.Extract(puzzle);
        Assert.True(features.IsLogicPuzzle);

        var ctx = WithMessages(
            userText: puzzle,
            features: features,
            // Only a user message present.
            ChatMessage.User(puzzle));

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.LlmMessages.Count);
        Assert.Equal("system", cont.Next.LlmMessages[0].Role);
        Assert.Contains("LOGIC PUZZLE MODE", cont.Next.LlmMessages[0].Content);
    }

    [Fact]
    public async Task Leaves_original_message_list_untouched_record_immutability()
    {
        // Records + with-expressions mean the incoming LlmMessages must
        // never be mutated — downstream tests/facades may still hold a
        // reference to it.
        var step = new LogicPuzzleScaffoldStep();
        var puzzle = "Which weighs more, a pound of feathers or a pound of lead?";
        var features = RoutingFeatures.Extract(puzzle);

        var seedSystem = ChatMessage.System("base");
        var ctx = WithMessages(userText: puzzle, features: features, seedSystem);
        var originalMessages = ctx.LlmMessages;

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Single(originalMessages);
        Assert.Same(seedSystem, originalMessages[0]);
        Assert.Equal("base", originalMessages[0].Content);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new LogicPuzzleScaffoldStep();
        var ctx = WithMessages(userText: "hi", features: null);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext WithMessages(
        string userText,
        RoutingFeatures? features,
        params ChatMessage[] messages)
        => new()
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = userText,
            Features = features,
            LlmMessages = messages,
        };
}
