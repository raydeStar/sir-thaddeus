using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Voice;

namespace Thaddeus.Runtime.Tests;

public sealed class PiperTextToSpeechProviderTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    [Fact]
    public void IsAvailable_false_when_paths_missing()
    {
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions(),
            new FakeRunner(),
            new AlwaysAvailablePlayer(),
            NullLogger<PiperTextToSpeechProvider>.Instance);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void IsAvailable_false_when_audio_player_unavailable()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice },
            new FakeRunner(),
            new UnavailablePlayer(),
            NullLogger<PiperTextToSpeechProvider>.Instance);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void IsAvailable_true_when_all_components_present()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice },
            new FakeRunner(),
            new AlwaysAvailablePlayer(),
            NullLogger<PiperTextToSpeechProvider>.Instance);
        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public async Task Speak_pipes_text_to_stdin_and_then_plays_wav()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var wavPath = NewTempPath(".wav");
        var runner = new FakeRunner
        {
            OnRun = spec => File.WriteAllBytes(GetArg(spec.Arguments, "-f"), new byte[] { 0xAA }),
        };
        var player = new RecordingPlayer();
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice, SpeakerId = 3 },
            runner,
            player,
            NullLogger<PiperTextToSpeechProvider>.Instance,
            tempPathFactory: () => wavPath);

        await provider.SpeakAsync("hello world", CancellationToken.None);

        Assert.NotNull(runner.LastSpec);
        Assert.Equal("hello world", runner.LastSpec!.StandardInput);
        Assert.Equal(wavPath, GetArg(runner.LastSpec.Arguments, "-f"));
        Assert.Equal(voice, GetArg(runner.LastSpec.Arguments, "-m"));
        Assert.Equal("3", GetArg(runner.LastSpec.Arguments, "-s"));
        Assert.Equal(wavPath, player.LastPlayedPath);
    }

    [Fact]
    public async Task Speak_skips_player_when_synth_exits_nonzero()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var runner = new FakeRunner { ExitCodeToReturn = 2, StderrToReturn = "model load failed" };
        var player = new RecordingPlayer();
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice },
            runner,
            player,
            NullLogger<PiperTextToSpeechProvider>.Instance);

        await provider.SpeakAsync("nope", CancellationToken.None);

        Assert.Null(player.LastPlayedPath);
    }

    [Fact]
    public async Task Speak_noop_on_blank_text()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var runner = new FakeRunner();
        var player = new RecordingPlayer();
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice },
            runner,
            player,
            NullLogger<PiperTextToSpeechProvider>.Instance);

        await provider.SpeakAsync("   ", CancellationToken.None);

        Assert.Null(runner.LastSpec);
        Assert.Null(player.LastPlayedPath);
    }

    [Fact]
    public async Task Speak_throws_when_provider_unavailable()
    {
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions(),
            new FakeRunner(),
            new UnavailablePlayer(),
            NullLogger<PiperTextToSpeechProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.SpeakAsync("hi", CancellationToken.None));
    }

    [Fact]
    public async Task Speak_deletes_temp_wav_after_run()
    {
        var binary = NewTempFile(".exe");
        var voice = NewTempFile(".onnx");
        var wavPath = NewTempPath(".wav");
        var runner = new FakeRunner
        {
            OnRun = spec => File.WriteAllBytes(GetArg(spec.Arguments, "-f"), new byte[] { 0 }),
        };
        var provider = new PiperTextToSpeechProvider(
            new PiperOptions { BinaryPath = binary, VoiceModelPath = voice },
            runner,
            new AlwaysAvailablePlayer(),
            NullLogger<PiperTextToSpeechProvider>.Instance,
            tempPathFactory: () => wavPath);

        await provider.SpeakAsync("clean me up", CancellationToken.None);

        Assert.False(File.Exists(wavPath));
    }

    private static string GetArg(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag) return args[i + 1];
        }
        return string.Empty;
    }

    private string NewTempFile(string suffix)
    {
        var path = NewTempPath(suffix);
        File.WriteAllBytes(path, new byte[] { 0 });
        return path;
    }

    private string NewTempPath(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-test-{Guid.NewGuid():N}{suffix}");
        _tempPaths.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var p in _tempPaths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best effort */ }
        }
    }

    private sealed class FakeRunner : IExternalProcessRunner
    {
        public ProcessSpec? LastSpec { get; private set; }
        public int ExitCodeToReturn { get; set; }
        public string StdoutToReturn { get; set; } = string.Empty;
        public string StderrToReturn { get; set; } = string.Empty;
        public Action<ProcessSpec>? OnRun { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            LastSpec = spec;
            OnRun?.Invoke(spec);
            return Task.FromResult(new ProcessResult(ExitCodeToReturn, StdoutToReturn, StderrToReturn, 4));
        }
    }

    private sealed class AlwaysAvailablePlayer : IAudioPlayer
    {
        public bool IsAvailable => true;
        public Task PlayWavFileAsync(string wavPath, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class UnavailablePlayer : IAudioPlayer
    {
        public bool IsAvailable => false;
        public Task PlayWavFileAsync(string wavPath, CancellationToken ct) =>
            throw new InvalidOperationException("not available");
    }

    private sealed class RecordingPlayer : IAudioPlayer
    {
        public string? LastPlayedPath { get; private set; }
        public bool IsAvailable => true;
        public Task PlayWavFileAsync(string wavPath, CancellationToken ct)
        {
            LastPlayedPath = wavPath;
            return Task.CompletedTask;
        }
    }
}
