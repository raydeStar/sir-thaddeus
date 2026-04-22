using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.State;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class RuntimeStateMachineTests
{
    private static RuntimeStateMachine NewMachine() =>
        new(NullLogger<RuntimeStateMachine>.Instance);

    [Fact]
    public void Starts_in_idle()
    {
        Assert.Equal(RuntimeState.Idle, NewMachine().Current);
    }

    [Fact]
    public void Idle_text_submission_moves_to_thinking()
    {
        var m = NewMachine();
        Assert.True(m.TryTransition(StateTrigger.UserTextSubmitted));
        Assert.Equal(RuntimeState.Thinking, m.Current);
    }

    [Fact]
    public void Listening_short_release_returns_to_idle()
    {
        var m = NewMachine();
        m.TryTransition(StateTrigger.UserPttPress);
        Assert.True(m.TryTransition(StateTrigger.UserPttReleaseSilent));
        Assert.Equal(RuntimeState.Idle, m.Current);
    }

    [Fact]
    public void Voice_mode_text_only_plan_speaks()
    {
        var m = NewMachine();
        m.TryTransition(StateTrigger.UserTextSubmitted);
        Assert.True(m.TryTransition(StateTrigger.PlanTextOnly, voiceMode: true));
        Assert.Equal(RuntimeState.Speaking, m.Current);
    }

    [Fact]
    public void Stop_all_from_thinking_drains_via_stopping()
    {
        var m = NewMachine();
        m.TryTransition(StateTrigger.UserTextSubmitted);
        Assert.True(m.TryTransition(StateTrigger.UserStopAll));
        Assert.Equal(RuntimeState.Stopping, m.Current);
        Assert.True(m.TryTransition(StateTrigger.StoppingComplete));
        Assert.Equal(RuntimeState.Idle, m.Current);
    }

    [Fact]
    public void Illegal_trigger_is_logged_and_ignored()
    {
        var m = NewMachine();
        // Idle -> ToolsDone is not a defined transition.
        Assert.False(m.TryTransition(StateTrigger.ToolsDone));
        Assert.Equal(RuntimeState.Idle, m.Current);
    }

    [Fact]
    public void Transitioned_event_fires_on_change_only()
    {
        var m = NewMachine();
        var count = 0;
        m.Transitioned += (_, _, _) => count++;

        m.TryTransition(StateTrigger.UserTextSubmitted); // -> Thinking
        m.TryTransition(StateTrigger.UserStopAll);       // -> Stopping
        m.TryTransition(StateTrigger.StoppingComplete);  // -> Idle
        m.TryTransition(StateTrigger.UserStopAll);       // Idle -> Idle (no transition)

        Assert.Equal(3, count);
    }
}
