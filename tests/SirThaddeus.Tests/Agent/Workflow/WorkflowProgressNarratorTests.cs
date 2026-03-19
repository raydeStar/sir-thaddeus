using SirThaddeus.Agent.Workflow;

namespace SirThaddeus.Tests;

public sealed class WorkflowProgressNarratorTests
{
    private readonly IProgressNarrator _narrator = new ProgressNarrator();
    private static readonly TaskRunState _anyState = new();

    // ── Every named trigger yields a non-empty message ───────────────────────

    [Theory]
    [InlineData(ProgressTrigger.TaskStarted)]
    [InlineData(ProgressTrigger.ChecklistInitialized)]
    [InlineData(ProgressTrigger.MilestoneReached)]
    [InlineData(ProgressTrigger.ContradictionDetected)]
    [InlineData(ProgressTrigger.RetryStarted)]
    [InlineData(ProgressTrigger.PartialAnswerReady)]
    [InlineData(ProgressTrigger.Finalizing)]
    [InlineData(ProgressTrigger.Completed)]
    [InlineData(ProgressTrigger.TimedOut)]
    [InlineData(ProgressTrigger.Cancelled)]
    [InlineData(ProgressTrigger.Failed)]
    public async Task KnownTrigger_ReturnsNonEmptyMessage(ProgressTrigger trigger)
    {
        var message = await _narrator.BuildUpdateAsync(_anyState, trigger, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    // ── Key message content spot-checks ─────────────────────────────────────

    [Fact]
    public async Task RetryStarted_MentionsConfidenceOrStrategy()
    {
        var message = await _narrator.BuildUpdateAsync(_anyState, ProgressTrigger.RetryStarted, CancellationToken.None);

        Assert.Contains("confidence", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Finalizing_MentionsFinalizingOrAnswer()
    {
        var message = await _narrator.BuildUpdateAsync(_anyState, ProgressTrigger.Finalizing, CancellationToken.None);

        Assert.True(
            message!.Contains("finalizing", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("answer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TimedOut_MentionsTimeBudget()
    {
        var message = await _narrator.BuildUpdateAsync(_anyState, ProgressTrigger.TimedOut, CancellationToken.None);

        Assert.Contains("time", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_ReturnsShortStoppedMessage()
    {
        var message = await _narrator.BuildUpdateAsync(_anyState, ProgressTrigger.Cancelled, CancellationToken.None);

        Assert.NotNull(message);
        Assert.True(message!.Length < 60, $"Expected short cancellation message; got: {message}");
    }

    // ── Unknown trigger yields null (no message) ─────────────────────────────

    [Fact]
    public async Task UnknownTriggerValue_ReturnsNull()
    {
        var unknownTrigger = (ProgressTrigger)999;

        var message = await _narrator.BuildUpdateAsync(_anyState, unknownTrigger, CancellationToken.None);

        Assert.Null(message);
    }

    // ── Messages are distinct across triggers ────────────────────────────────

    [Fact]
    public async Task AllKnownTriggers_ProduceDistinctMessages()
    {
        var triggers = new[]
        {
            ProgressTrigger.TaskStarted,
            ProgressTrigger.ChecklistInitialized,
            ProgressTrigger.MilestoneReached,
            ProgressTrigger.ContradictionDetected,
            ProgressTrigger.RetryStarted,
            ProgressTrigger.PartialAnswerReady,
            ProgressTrigger.Finalizing,
            ProgressTrigger.Completed,
            ProgressTrigger.TimedOut,
            ProgressTrigger.Cancelled,
            ProgressTrigger.Failed
        };

        var messages = new List<string>();
        foreach (var trigger in triggers)
        {
            var m = await _narrator.BuildUpdateAsync(_anyState, trigger, CancellationToken.None);
            if (m is not null) messages.Add(m);
        }

        var distinct = messages.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(messages.Count, distinct);
    }
}
