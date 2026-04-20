using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.State;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class VoiceModeControllerTests
{
    [Fact]
    public async Task Captured_release_with_transcript_drives_state_through_thinking()
    {
        var (machine, controller, _, _) = NewControllerWith(
            stt: new ScriptedStt(transcript: "hello world"),
            tts: new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        Assert.Equal(RuntimeState.Listening, machine.Current);

        // Hold long enough to count as captured.
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var transcript = await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Equal("hello world", transcript);
        Assert.Equal(RuntimeState.Thinking, machine.Current);
    }

    [Fact]
    public async Task Silent_release_returns_to_idle_without_invoking_stt()
    {
        var stt = new ScriptedStt(transcript: "should not be returned");
        var (machine, controller, _, _) = NewControllerWith(stt, new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        Assert.Equal(RuntimeState.Listening, machine.Current);

        // Release immediately → below the minimum capture duration.
        var transcript = await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Null(transcript);
        Assert.Equal(RuntimeState.Idle, machine.Current);
        Assert.Equal(0, stt.InvocationCount);
    }

    [Fact]
    public async Task Empty_transcript_returns_to_idle()
    {
        var (machine, controller, _, _) = NewControllerWith(
            stt: new ScriptedStt(transcript: "   "),
            tts: new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var transcript = await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Null(transcript);
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    [Fact]
    public async Task Stt_unavailable_short_circuits_to_idle_without_throwing()
    {
        var (machine, controller, _, _) = NewControllerWith(
            stt: new StubSpeechToTextProvider(),
            tts: new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var transcript = await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Null(transcript);
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    [Fact]
    public async Task Speak_drains_state_back_to_idle_when_tts_available()
    {
        var (machine, controller, _, tts) = NewControllerWith(
            stt: new StubSpeechToTextProvider(),
            tts: new ScriptedTts());

        // Drive the machine to Speaking via the normal path: text → thinking → text-only plan.
        machine.TryTransition(StateTrigger.UserTextSubmitted);
        machine.TryTransition(StateTrigger.PlanTextOnly, voiceMode: true);
        Assert.Equal(RuntimeState.Speaking, machine.Current);

        await controller.SpeakAsync("on it", CancellationToken.None);

        Assert.Equal(RuntimeState.Idle, machine.Current);
        Assert.Equal(1, ((ScriptedTts)tts).InvocationCount);
    }

    [Fact]
    public async Task Cancellation_during_stt_propagates_and_resets_state()
    {
        var stt = new BlockingStt();
        var (machine, controller, _, _) = NewControllerWith(stt, new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        var task = controller.EndPushToTalkAsync(new byte[3200], cts.Token);
        cts.Cancel();
        stt.Release();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        // SttDoneEmpty was dispatched in the catch path.
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    private static (RuntimeStateMachine machine, VoiceModeController controller,
        ISpeechToTextProvider stt, ITextToSpeechProvider tts)
        NewControllerWith(ISpeechToTextProvider stt, ITextToSpeechProvider tts)
    {
        var machine = new RuntimeStateMachine(NullLogger<RuntimeStateMachine>.Instance);
        var controller = new VoiceModeController(machine, stt, tts, NullLogger<VoiceModeController>.Instance);
        return (machine, controller, stt, tts);
    }

    private sealed class ScriptedStt : ISpeechToTextProvider
    {
        private readonly string _transcript;
        public int InvocationCount { get; private set; }

        public ScriptedStt(string transcript) => _transcript = transcript;

        public bool IsAvailable => true;

        public Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(new SttResult(_transcript, 12));
        }
    }

    private sealed class BlockingStt : ISpeechToTextProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;

        public void Release() => _gate.TrySetResult();

        public async Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
        {
            using var registration = ct.Register(() => _gate.TrySetCanceled(ct));
            await _gate.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new SttResult("never", 0);
        }
    }

    private sealed class ScriptedTts : ITextToSpeechProvider
    {
        public int InvocationCount { get; private set; }
        public bool IsAvailable => true;

        public Task SpeakAsync(string text, CancellationToken ct)
        {
            InvocationCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StopAll_cancels_in_flight_tts_and_resets_to_idle()
    {
        var tts = new BlockingTts();
        var (machine, controller, _, _) = NewControllerWith(new StubSpeechToTextProvider(), tts);

        machine.TryTransition(StateTrigger.UserTextSubmitted);
        machine.TryTransition(StateTrigger.PlanTextOnly, voiceMode: true);
        Assert.Equal(RuntimeState.Speaking, machine.Current);

        var speakTask = controller.SpeakAsync("a long answer", CancellationToken.None);
        controller.StopAll();

        await speakTask; // SpeakAsync swallows the cancellation in finally
        Assert.True(tts.WasCancelled);
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    [Fact]
    public async Task StopAll_cancels_in_flight_stt_and_returns_to_idle()
    {
        var stt = new BlockingStt();
        var (machine, controller, _, _) = NewControllerWith(stt, new StubTextToSpeechProvider());

        controller.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var task = controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        controller.StopAll();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    [Fact]
    public void StopAll_when_idle_is_safe_noop()
    {
        var (machine, controller, _, _) = NewControllerWith(
            new StubSpeechToTextProvider(),
            new StubTextToSpeechProvider());

        controller.StopAll();
        Assert.Equal(RuntimeState.Idle, machine.Current);
    }

    [Fact]
    public async Task StopAll_does_not_poison_subsequent_voice_operations()
    {
        var tts = new BlockingTts();
        var (machine, controller, _, _) = NewControllerWith(new StubSpeechToTextProvider(), tts);

        machine.TryTransition(StateTrigger.UserTextSubmitted);
        machine.TryTransition(StateTrigger.PlanTextOnly, voiceMode: true);
        var first = controller.SpeakAsync("first", CancellationToken.None);
        controller.StopAll();
        await first;

        // Second utterance after stop must run cleanly with a fresh CTS.
        var stt2 = new ScriptedStt("hello again");
        var (machine2, controller2, _, _) = NewControllerWith(stt2, new StubTextToSpeechProvider());
        controller2.StopAll(); // even if stop is the very first call
        controller2.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var transcript = await controller2.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Equal("hello again", transcript);
        Assert.Equal(RuntimeState.Thinking, machine2.Current);
    }

    private sealed class BlockingTts : ITextToSpeechProvider
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WasCancelled { get; private set; }
        public bool IsAvailable => true;

        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            using var registration = ct.Register(() =>
            {
                WasCancelled = true;
                _gate.TrySetCanceled(ct);
            });
            try { await _gate.Task.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* swallow so SpeakAsync returns cleanly */ }
        }
    }

    [Fact]
    public async Task Captured_voice_turn_records_ok_activity_entry_with_transcript()
    {
        var machine = new RuntimeStateMachine(NullLogger<RuntimeStateMachine>.Instance);
        var activity = new InMemoryActivityLog(capacity: 16);
        var controller = new VoiceModeController(
            machine,
            new ScriptedStt("hello world"),
            new StubTextToSpeechProvider(),
            NullLogger<VoiceModeController>.Instance,
            activity);

        controller.BeginPushToTalk();
        await Task.Delay(VoiceModeController.MinimumCaptureDuration + TimeSpan.FromMilliseconds(50));
        var transcript = await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        Assert.Equal("hello world", transcript);
        var entries = activity.List(10);
        var entry = Assert.Single(entries);
        Assert.Equal(ActivityKind.VoiceTurn, entry.Kind);
        Assert.Equal(ActivityStatus.Ok, entry.Status);
        Assert.Equal("hello world", entry.Summary);
        Assert.Equal("hello world", entry.Detail);
        Assert.NotNull(entry.CompletedAt);
    }

    [Fact]
    public async Task Silent_release_records_cancelled_activity_entry()
    {
        var machine = new RuntimeStateMachine(NullLogger<RuntimeStateMachine>.Instance);
        var activity = new InMemoryActivityLog(capacity: 16);
        var controller = new VoiceModeController(
            machine,
            new ScriptedStt("never"),
            new StubTextToSpeechProvider(),
            NullLogger<VoiceModeController>.Instance,
            activity);

        controller.BeginPushToTalk();
        // Release immediately → silent.
        await controller.EndPushToTalkAsync(new byte[3200], CancellationToken.None);

        var entry = Assert.Single(activity.List(10));
        Assert.Equal(ActivityStatus.Cancelled, entry.Status);
    }
}
