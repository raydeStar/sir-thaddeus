using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class OnboardingInjectionStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("OnboardingInjection", new OnboardingInjectionStep(_ => OnboardingMode.NotNeeded).Name);

    [Fact]
    public async Task No_op_when_resolver_returns_NotNeeded()
    {
        var step = new OnboardingInjectionStep(_ => OnboardingMode.NotNeeded);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Appends_cold_onboarding_suffix_when_resolver_returns_Cold()
    {
        // Cold onboarding = brand-new user. The suffix contains a
        // distinctive [ONBOARDING] block and mentions memory_store_facts
        // for identity capture.
        var step = new OnboardingInjectionStep(_ => OnboardingMode.Cold);
        var ctx = WithSystemPrompt("You are Sir Thaddeus.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = Assert.Single(cont.Next.LlmMessages, m => m.Role == "system");
        Assert.StartsWith("You are Sir Thaddeus.", system.Content);
        Assert.Contains("[ONBOARDING]", system.Content);
        Assert.Contains("memory_store_facts", system.Content);
    }

    [Fact]
    public async Task Appends_follow_up_onboarding_suffix_when_resolver_returns_FollowUp()
    {
        // Follow-up is a softer version that doesn't re-introduce.
        var step = new OnboardingInjectionStep(_ => OnboardingMode.FollowUp);
        var ctx = WithSystemPrompt("base");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        var system = Assert.Single(cont.Next.LlmMessages, m => m.Role == "system");
        Assert.Contains("[ONBOARDING]", system.Content);
        // Cold-specific instruction ("Introduce yourself warmly") is NOT
        // part of the follow-up suffix — verifies we picked the right one.
        Assert.DoesNotContain("Introduce yourself warmly", system.Content);
    }

    [Fact]
    public async Task Inserts_system_message_when_none_seeded()
    {
        var step = new OnboardingInjectionStep(_ => OnboardingMode.Cold);
        var ctx = new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = "hi",
            LlmMessages = new[] { ChatMessage.User("hi") },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.LlmMessages.Count);
        Assert.Equal("system", cont.Next.LlmMessages[0].Role);
        Assert.Contains("[ONBOARDING]", cont.Next.LlmMessages[0].Content);
    }

    [Fact]
    public void Construction_rejects_null_resolver()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new OnboardingInjectionStep(null!));
    }

    [Fact]
    public async Task Resolver_is_consulted_with_current_context()
    {
        // The resolver gets the live context so runtime-specific signals
        // (isAutomationRun, feature flags, ThreadId-based caches) can all
        // contribute to the onboarding decision.
        TurnContext? observed = null;
        var step = new OnboardingInjectionStep(ctx => { observed = ctx; return OnboardingMode.NotNeeded; });
        var ctx = WithSystemPrompt("base");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Same(ctx, observed);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new OnboardingInjectionStep(_ => OnboardingMode.Cold);
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
}
