using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.PersonalityEngine.Prompting;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class PersonalityInjectionStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("PersonalityInjection", new PersonalityInjectionStep(runtime: null).Name);

    [Fact]
    public async Task No_op_when_runtime_is_null()
    {
        // Runtime opted out — step passes through untouched.
        var step = new PersonalityInjectionStep(runtime: null);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Wraps_existing_system_message_with_personality_prompt()
    {
        // The runtime's BuildSystemPrompt stitches the task instruction
        // into the personality framing. After the step runs, the system
        // message must reflect the wrapped version.
        var runtime = new StubRuntime(buildPrompt: task => $"[persona]\n{task}\n[/persona]");
        var step = new PersonalityInjectionStep(runtime);
        var ctx = WithSystemPrompt("You are Sir Thaddeus.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = cont.Next.LlmMessages.First(m => m.Role == "system");
        Assert.Equal("[persona]\nYou are Sir Thaddeus.\n[/persona]", system.Content);
    }

    [Fact]
    public async Task Inserts_few_shot_examples_between_system_and_history()
    {
        // Few-shot pairs live between the system block and the chat
        // history so the model sees them as "here's how I answer" rather
        // than prior user turns. Order: system, user-example, assistant-example, ... user-message.
        var runtime = new StubRuntime(
            buildPrompt: task => task,
            fewShots: new[]
            {
                new PersonalityFewShotExample { User = "how are you?", Assistant = "Quite well, thank you." },
                new PersonalityFewShotExample { User = "what time?", Assistant = "A moment; I will check." },
            });
        var step = new PersonalityInjectionStep(runtime);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            LlmMessages = new[]
            {
                ChatMessage.System("You are Sir Thaddeus."),
                ChatMessage.User("hi"),
            },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var roles = cont.Next.LlmMessages.Select(m => m.Role).ToArray();
        Assert.Equal(new[] { "system", "user", "assistant", "user", "assistant", "user" }, roles);
        Assert.Equal("how are you?", cont.Next.LlmMessages[1].Content);
        Assert.Equal("Quite well, thank you.", cont.Next.LlmMessages[2].Content);
    }

    [Fact]
    public async Task Skips_examples_with_blank_user_or_assistant_field()
    {
        // Schema says both fields are required but profiles on disk may
        // have half-finished edits. Defensively skip rather than crash.
        var runtime = new StubRuntime(
            buildPrompt: task => task,
            fewShots: new[]
            {
                new PersonalityFewShotExample { User = "", Assistant = "nope" },
                new PersonalityFewShotExample { User = "good one", Assistant = "indeed" },
                new PersonalityFewShotExample { User = "   ", Assistant = "skip this too" },
            });
        var step = new PersonalityInjectionStep(runtime);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        // Only one valid example → 2 inserted messages before the real
        // "hi" user turn. The blank examples must be dropped entirely.
        Assert.Equal("user", cont.Next.LlmMessages[1].Role);
        Assert.Equal("good one", cont.Next.LlmMessages[1].Content);
        Assert.Equal("assistant", cont.Next.LlmMessages[2].Role);
        Assert.Equal("indeed", cont.Next.LlmMessages[2].Content);
        Assert.Equal("user", cont.Next.LlmMessages[3].Role);
        Assert.Equal("hi", cont.Next.LlmMessages[3].Content);
        // And the blank examples never made it into the list.
        Assert.DoesNotContain(cont.Next.LlmMessages, m => m.Content == "nope");
        Assert.DoesNotContain(cont.Next.LlmMessages, m => m.Content == "skip this too");
    }

    [Fact]
    public async Task Inserts_system_message_when_none_seeded()
    {
        // Rare but supported — if the pipeline is configured without a
        // base system prompt, personality still produces one.
        var runtime = new StubRuntime(buildPrompt: _ => "synthetic persona prompt");
        var step = new PersonalityInjectionStep(runtime);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            LlmMessages = new[] { ChatMessage.User("hi") },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("system", cont.Next.LlmMessages[0].Role);
        Assert.Equal("synthetic persona prompt", cont.Next.LlmMessages[0].Content);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var runtime = new StubRuntime(buildPrompt: task => task);
        var step = new PersonalityInjectionStep(runtime);
        var ctx = WithSystemPrompt("base");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext WithSystemPrompt(string prompt) => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "hi",
        LlmMessages = new[] { ChatMessage.System(prompt), ChatMessage.User("hi") },
    };

    private sealed class StubRuntime : IPersonalityRuntime
    {
        private readonly Func<string, string> _buildPrompt;
        private readonly PersonalityRuntimeSnapshot _snapshot;

        public StubRuntime(
            Func<string, string> buildPrompt,
            IReadOnlyList<PersonalityFewShotExample>? fewShots = null)
        {
            _buildPrompt = buildPrompt;
            _snapshot = new PersonalityRuntimeSnapshot
            {
                Profile = new PersonalityProfile
                {
                    Id = "stub",
                    Instructions = new PersonalityInstructions
                    {
                        FewShotExamples = fewShots ?? Array.Empty<PersonalityFewShotExample>(),
                    },
                },
                ProfileHash = "stub-hash",
                SourcePath = "stub",
                ProfilesDirectory = "stub",
            };
        }

        public PersonalityRuntimeSnapshot Snapshot => _snapshot;

        public PersonalityRuntimeSnapshot Reload(string activeProfileId, string profilesDirectory) => _snapshot;

        public string BuildSystemPrompt(string taskInstruction, IEnumerable<PromptBlock>? extraBlocks = null)
            => _buildPrompt(taskInstruction);

        // Not exercised by PersonalityInjectionStep; the step only calls
        // BuildSystemPrompt and reads Snapshot.Profile.Instructions.FewShotExamples.
        public PersonalityEngine.Context.PersonalityTurnContext BuildTurnContext(string? latestUserMessage)
            => throw new NotSupportedException("Stub: BuildTurnContext is not exercised by the step under test.");

        public string BuildAnchor(string turnTag, string? latestUserMessage = null) => string.Empty;
    }
}
