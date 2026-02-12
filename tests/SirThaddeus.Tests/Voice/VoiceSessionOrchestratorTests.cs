using SirThaddeus.AuditLog;
using SirThaddeus.Voice;

namespace SirThaddeus.Tests.Voice;

public sealed class VoiceSessionOrchestratorTests
{
    [Fact]
    public async Task RepeatedMicDownWhileListening_DoesNotStartMultipleRecordings()
    {
        var capture = new FakeCaptureService();
        var playback = new FakePlaybackService();
        var asr = new FakeAsrService("hello");
        var agent = new FakeAgentService("world");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        orchestrator.EnqueueMicDown();

        await WaitForAsync(() => capture.StartCalls >= 1, TimeSpan.FromSeconds(1));

        Assert.Equal(1, capture.StartCalls);
        Assert.Equal(VoiceState.Listening, orchestrator.CurrentState);
    }

    [Fact]
    public async Task MicUpWhileNotListening_IsIgnored()
    {
        var capture = new FakeCaptureService();
        var playback = new FakePlaybackService();
        var asr = new FakeAsrService("hello");
        var agent = new FakeAgentService("world");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicUp();
        await Task.Delay(75);

        Assert.Equal(0, capture.StopCalls);
        Assert.Equal(VoiceState.Idle, orchestrator.CurrentState);
    }

    [Fact]
    public async Task ShutupWhileSpeaking_StopsPlaybackAndReturnsIdle()
    {
        var capture = new FakeCaptureService();
        var playback = new FakePlaybackService(blockPlayback: true);
        var asr = new FakeAsrService("hello");
        var agent = new FakeAgentService("world");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        orchestrator.EnqueueMicUp();
        await WaitForStateAsync(orchestrator, VoiceState.Speaking, TimeSpan.FromSeconds(2));

        orchestrator.EnqueueShutup();
        await WaitForStateAsync(orchestrator, VoiceState.Idle, TimeSpan.FromSeconds(2));

        Assert.True(playback.StopCalls >= 1);
        Assert.False(playback.IsPlaying);
    }

    [Fact]
    public async Task MicDownDuringTranscribing_CancelsStaleResultBeforeAgent()
    {
        var capture = new FakeCaptureService();
        var playback = new FakePlaybackService();
        var asr = new BlockingAsrService();
        var agent = new FakeAgentService("world");
        var audit = new TestAuditLogger();

        await using var orchestrator = new VoiceSessionOrchestrator(capture, playback, asr, agent, audit);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        orchestrator.EnqueueMicUp();
        await WaitForStateAsync(orchestrator, VoiceState.Transcribing, TimeSpan.FromSeconds(2));

        orchestrator.EnqueueMicDown();
        asr.Release("stale transcript");

        await WaitForStateAsync(orchestrator, VoiceState.Listening, TimeSpan.FromSeconds(2));

        Assert.Equal(2, capture.StartCalls);
        Assert.Equal(0, agent.CallCount);
    }

    [Fact]
    public async Task EnqueueFault_TransitionsFaultedThenReturnsIdleDeterministically()
    {
        var capture = new FakeCaptureService();
        var playback = new FakePlaybackService();
        var asr = new FakeAsrService("hello");
        var agent = new FakeAgentService("world");
        var audit = new TestAuditLogger();

        var transitions = new List<VoiceStateChangedEventArgs>();
        await using var orchestrator = new VoiceSessionOrchestrator(capture, playback, asr, agent, audit);
        orchestrator.StateChanged += (_, e) => transitions.Add(e);
        await orchestrator.StartAsync();

        orchestrator.EnqueueMicDown();
        await WaitForStateAsync(orchestrator, VoiceState.Listening, TimeSpan.FromSeconds(2));

        orchestrator.EnqueueFault("Voice component missing.");
        await WaitForStateAsync(orchestrator, VoiceState.Idle, TimeSpan.FromSeconds(2));

        var faultIndex = transitions.FindIndex(t =>
            t.CurrentState == VoiceState.Faulted &&
            string.Equals(t.Reason, "Voice component missing.", StringComparison.Ordinal));
        Assert.True(faultIndex >= 0, "Expected a Faulted transition with the actionable reason.");

        var idleAfterFault = transitions
            .Skip(faultIndex + 1)
            .Any(t => t.CurrentState == VoiceState.Idle);
        Assert.True(idleAfterFault, "Expected deterministic Faulted -> Idle transition.");
    }

    private static async Task WaitForStateAsync(
        VoiceSessionOrchestrator orchestrator,
        VoiceState expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (orchestrator.CurrentState == expected)
                return;
            await Task.Delay(15);
        }

        Assert.Fail($"Timed out waiting for state '{expected}'. Current='{orchestrator.CurrentState}'.");
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(15);
        }

        Assert.Fail("Timed out waiting for condition.");
    }

    private sealed class FakeCaptureService : IAudioCaptureService
    {
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int AbortCalls { get; private set; }
        public bool IsCapturing { get; private set; }

        public Task StartCaptureAsync(string sessionId, CancellationToken cancellationToken)
        {
            _ = sessionId;
            cancellationToken.ThrowIfCancellationRequested();
            StartCalls++;
            IsCapturing = true;
            return Task.CompletedTask;
        }

        public Task<VoiceAudioClip?> StopCaptureAsync(string sessionId, CancellationToken cancellationToken)
        {
            _ = sessionId;
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            IsCapturing = false;
            return Task.FromResult<VoiceAudioClip?>(new VoiceAudioClip
            {
                AudioBytes = [1, 2, 3],
                ContentType = "audio/wav",
                SampleRateHz = 16000,
                Channels = 1,
                BitsPerSample = 16
            });
        }

        public Task AbortCaptureAsync(string sessionId, CancellationToken cancellationToken)
        {
            _ = sessionId;
            _ = cancellationToken;
            AbortCalls++;
            IsCapturing = false;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlaybackService : IAudioPlaybackService
    {
        private readonly bool _blockPlayback;
        private TaskCompletionSource<bool>? _playbackCompletion;

        public FakePlaybackService(bool blockPlayback = false)
        {
            _blockPlayback = blockPlayback;
        }

        public int PlayCalls { get; private set; }
        public int StopCalls { get; private set; }
        public bool IsPlaying { get; private set; }

        public async Task PlayTextAsync(string text, string sessionId, CancellationToken cancellationToken)
        {
            _ = text;
            _ = sessionId;

            PlayCalls++;
            IsPlaying = true;
            try
            {
                if (_blockPlayback)
                {
                    _playbackCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var reg = cancellationToken.Register(() => _playbackCompletion.TrySetResult(true));
                    await _playbackCompletion.Task.WaitAsync(cancellationToken);
                }
            }
            finally
            {
                IsPlaying = false;
                _playbackCompletion = null;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            StopCalls++;
            IsPlaying = false;
            _playbackCompletion?.TrySetResult(true);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAsrService : IAsrService
    {
        private readonly string _transcript;

        public FakeAsrService(string transcript)
        {
            _transcript = transcript;
        }

        public Task<string> TranscribeAsync(
            VoiceAudioClip clip,
            string sessionId,
            CancellationToken cancellationToken)
        {
            _ = clip;
            _ = sessionId;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_transcript);
        }
    }

    private sealed class BlockingAsrService : IAsrService
    {
        private readonly TaskCompletionSource<string> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> TranscribeAsync(
            VoiceAudioClip clip,
            string sessionId,
            CancellationToken cancellationToken)
        {
            _ = clip;
            _ = sessionId;
            _ = cancellationToken;
            return _completion.Task;
        }

        public void Release(string transcript) => _completion.TrySetResult(transcript);
    }

    private sealed class FakeAgentService : IVoiceAgentService
    {
        private readonly string _responseText;

        public FakeAgentService(string responseText)
        {
            _responseText = responseText;
        }

        public int CallCount { get; private set; }

        public Task<VoiceAgentResponse> ProcessAsync(
            string transcript,
            string sessionId,
            CancellationToken cancellationToken)
        {
            _ = transcript;
            _ = sessionId;
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new VoiceAgentResponse
            {
                Text = _responseText,
                Success = true
            });
        }
    }
}
