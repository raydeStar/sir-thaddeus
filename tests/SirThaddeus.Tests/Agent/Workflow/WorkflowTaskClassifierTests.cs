using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowTaskClassifierTests
{
    private readonly ITaskClassifier _classifier = new TaskClassifier();

    // ── Trivial prompts ──────────────────────────────────────────────────────

    [Fact]
    public async Task ShortArithmeticQuery_ClassifiesAsTrivial()
    {
        var envelope = await _classifier.ClassifyAsync("What is 2+2?", CancellationToken.None);

        Assert.Equal(TaskComplexity.Trivial, envelope.Complexity);
        Assert.False(envelope.ShowChecklist);
        Assert.Equal("direct_answer", envelope.Intent);
    }

    [Fact]
    public async Task TrivialPrompt_SetsThirtySecondTimeBudget()
    {
        var envelope = await _classifier.ClassifyAsync("Hello!", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(30), envelope.TimeBudget);
    }

    // ── SimpleLookup prompts ─────────────────────────────────────────────────

    [Fact]
    public async Task MediumLengthNeutralQuery_ClassifiesAsSimpleLookup()
    {
        var envelope = await _classifier.ClassifyAsync("What is the capital of France?", CancellationToken.None);

        Assert.Equal(TaskComplexity.SimpleLookup, envelope.Complexity);
        Assert.True(envelope.ShowChecklist);
    }

    [Fact]
    public async Task TodayKeyword_ForcesNeedsTools()
    {
        var envelope = await _classifier.ClassifyAsync("What day is it today?", CancellationToken.None);

        Assert.Equal(TaskComplexity.Trivial, envelope.Complexity);
        Assert.Equal("lookup", envelope.Intent);
        Assert.True(envelope.NeedsTools);
        Assert.False(envelope.ShowChecklist);
    }

    [Fact]
    public async Task DeterministicUtilityPrompt_StaysDirectAnswer()
    {
        var envelope = await _classifier.ClassifyAsync("What time is it right now?", CancellationToken.None);

        Assert.Equal(TaskComplexity.Trivial, envelope.Complexity);
        Assert.Equal("direct_answer", envelope.Intent);
        Assert.False(envelope.NeedsTools);
        Assert.False(envelope.ShowChecklist);
    }

    [Fact]
    public async Task CasualCheckInWithToday_StaysDirectAnswer()
    {
        var envelope = await _classifier.ClassifyAsync(
            "Hey, how are you doing today? Just wanted to say thanks for helping me out.",
            CancellationToken.None);

        Assert.Equal(TaskComplexity.Trivial, envelope.Complexity);
        Assert.Equal("direct_answer", envelope.Intent);
        Assert.False(envelope.NeedsTools);
        Assert.False(envelope.ShowChecklist);
    }

    // ── MultiStepResearch prompts ────────────────────────────────────────────

    [Fact]
    public async Task ResearchKeyword_ClassifiesAsMultiStepResearch()
    {
        var envelope = await _classifier.ClassifyAsync("Research the history of the Roman Empire", CancellationToken.None);

        Assert.Equal(TaskComplexity.MultiStepResearch, envelope.Complexity);
        Assert.True(envelope.ShowChecklist);
        Assert.True(envelope.NeedsTools);
    }

    [Fact]
    public async Task GithubPricingKeywords_ClassifiesAsMultiStepResearch()
    {
        var envelope = await _classifier.ClassifyAsync("Can you get me details on GitHub pricing?", CancellationToken.None);

        Assert.Equal(TaskComplexity.MultiStepResearch, envelope.Complexity);
        Assert.True(envelope.ShowChecklist);
    }

    [Fact]
    public async Task MultiStepResearch_SetsSixtySecondTimeBudget()
    {
        var envelope = await _classifier.ClassifyAsync("Compare the pricing plans of GitHub billing", CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(60), envelope.TimeBudget);
    }

    [Fact]
    public async Task CompareKeyword_ClassifiesAsMultiStepResearch()
    {
        var envelope = await _classifier.ClassifyAsync("Compare React and Angular frameworks", CancellationToken.None);

        Assert.Equal(TaskComplexity.MultiStepResearch, envelope.Complexity);
    }

    [Fact]
    public async Task FlightAndAvailabilityPrompt_ClassifiesAsMultiStepResearch()
    {
        var envelope = await _classifier.ClassifyAsync(
            "Can you find the cheapest flight from Boise to Tokyo next month and verify it's still available?",
            CancellationToken.None);

        Assert.Equal(TaskComplexity.MultiStepResearch, envelope.Complexity);
        Assert.Equal(TimeSpan.FromSeconds(60), envelope.TimeBudget);
        Assert.True(envelope.NeedsTools);
    }

    [Fact]
    public async Task StockLookupPrompt_ClassifiesAsMultiStepResearch()
    {
        var envelope = await _classifier.ClassifyAsync(
            "Find a PS5 in stock under $500 near me and check availability.",
            CancellationToken.None);

        Assert.Equal(TaskComplexity.MultiStepResearch, envelope.Complexity);
        Assert.Equal(TimeSpan.FromSeconds(60), envelope.TimeBudget);
        Assert.True(envelope.NeedsTools);
    }

    // ── Envelope field invariants ────────────────────────────────────────────

    [Fact]
    public async Task AllPrompts_PopulateTaskIdAndUserRequest()
    {
        var prompt = "What is 2+2?";
        var envelope = await _classifier.ClassifyAsync(prompt, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(envelope.TaskId));
        Assert.Equal(prompt, envelope.UserRequest);
    }

    [Fact]
    public async Task AllPrompts_SetMaxToolCallsToEight()
    {
        var envelope = await _classifier.ClassifyAsync("What is 2+2?", CancellationToken.None);

        Assert.Equal(8, envelope.MaxToolCalls);
    }

    [Fact]
    public async Task NullishPrompt_DoesNotThrow()
    {
        var envelope = await _classifier.ClassifyAsync("", CancellationToken.None);

        Assert.Equal(TaskComplexity.Trivial, envelope.Complexity);
    }
}
