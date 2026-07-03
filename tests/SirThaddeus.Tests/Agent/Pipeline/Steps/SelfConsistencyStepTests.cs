using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class SelfConsistencyStepTests
{
    [Fact]
    public async Task Strong_consensus_terminates_with_winning_answer()
    {
        var llm = new QueueLlm(
            "Final answer: 42",
            "Final answer: 42",
            "Final answer: 42");
        var step = new SelfConsistencyStep(llm, samples: 5, samplingTemperature: 0.9, minAgreement: 2.0 / 3.0);

        var result = await step.ExecuteAsync(StrictNumericContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal("42", term.Response.Text);
        Assert.Equal(3, llm.CallCount);
    }

    [Fact]
    public async Task Weak_plurality_continues_to_normal_pipeline()
    {
        var llm = new QueueLlm(
            "Final answer: 42",
            "Final answer: 7",
            "Final answer: 42",
            "Final answer: 11",
            "Final answer: 42");
        var step = new SelfConsistencyStep(llm, samples: 5, samplingTemperature: 0.9, minAgreement: 2.0 / 3.0);
        var context = StrictNumericContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(context, cont.Next);
        Assert.Equal(5, llm.CallCount);
    }

    private static TurnContext StrictNumericContext() => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "What is 40+2? Put the final answer on its own line.",
        LlmMessages =
        [
            ChatMessage.System("You are Sir Thaddeus."),
            ChatMessage.User("What is 40+2? Put the final answer on its own line."),
        ],
    };

    private sealed class QueueLlm : ILlmClient
    {
        private readonly Queue<string> _responses;

        public QueueLlm(params string[] responses) => _responses = new Queue<string>(responses);

        public int CallCount { get; private set; }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, maxTokensOverride: 512, cancellationToken);

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var content = _responses.Count > 0 ? _responses.Dequeue() : "Final answer: 0";
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = content,
                FinishReason = "stop",
            });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake");
    }
}
