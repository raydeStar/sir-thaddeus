using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Voice;

namespace Thaddeus.Runtime.Tests;

public sealed class WhisperCppSpeechToTextProviderTests : IDisposable
{
    private readonly List<string> _tempPaths = new();

    [Fact]
    public void IsAvailable_false_when_paths_missing()
    {
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions(), new FakeRunner(), NullLogger<WhisperCppSpeechToTextProvider>.Instance);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void IsAvailable_false_when_binary_missing_even_if_model_exists()
    {
        var modelFile = NewTempFile(".bin");
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = "/does/not/exist", ModelPath = modelFile },
            new FakeRunner(),
            NullLogger<WhisperCppSpeechToTextProvider>.Instance);
        Assert.False(provider.IsAvailable);
    }

    [Fact]
    public void IsAvailable_true_when_both_paths_exist()
    {
        var binary = NewTempFile(".exe");
        var model = NewTempFile(".bin");
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = binary, ModelPath = model },
            new FakeRunner(),
            NullLogger<WhisperCppSpeechToTextProvider>.Instance);
        Assert.True(provider.IsAvailable);
    }

    [Fact]
    public async Task Transcribe_invokes_runner_with_expected_arguments_and_returns_transcript()
    {
        var binary = NewTempFile(".exe");
        var model = NewTempFile(".bin");
        var runner = new FakeRunner { StdoutToReturn = " Hello world.\n" };
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = binary, ModelPath = model, Language = "en", Threads = 4 },
            runner,
            NullLogger<WhisperCppSpeechToTextProvider>.Instance);

        var result = await provider.TranscribeAsync(new byte[3200], CancellationToken.None);

        Assert.Equal("Hello world.", result.Transcript);
        Assert.NotNull(runner.LastSpec);
        Assert.Equal(binary, runner.LastSpec!.FileName);
        Assert.Contains("-m", runner.LastSpec.Arguments);
        Assert.Contains(model, runner.LastSpec.Arguments);
        Assert.Contains("-f", runner.LastSpec.Arguments);
        Assert.Contains("-nt", runner.LastSpec.Arguments);
        Assert.Contains("-np", runner.LastSpec.Arguments);
        Assert.Contains("-l", runner.LastSpec.Arguments);
        Assert.Contains("en", runner.LastSpec.Arguments);
        Assert.Contains("-t", runner.LastSpec.Arguments);
        Assert.Contains("4", runner.LastSpec.Arguments);
    }

    [Fact]
    public async Task Transcribe_writes_riff_wav_header_to_temp_file()
    {
        var binary = NewTempFile(".exe");
        var model = NewTempFile(".bin");
        var capturedWavPath = NewTempFile(".wav");
        File.Delete(capturedWavPath); // factory will recreate it
        byte[] capturedBytes = Array.Empty<byte>();

        var runner = new FakeRunner
        {
            BeforeReturn = spec =>
            {
                // Read the wav file from the -f argument before the provider deletes it.
                var args = spec.Arguments;
                for (var i = 0; i < args.Count - 1; i++)
                {
                    if (args[i] == "-f")
                    {
                        capturedBytes = File.ReadAllBytes(args[i + 1]);
                        break;
                    }
                }
            },
            StdoutToReturn = "ack",
        };

        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = binary, ModelPath = model },
            runner,
            NullLogger<WhisperCppSpeechToTextProvider>.Instance,
            tempPathFactory: () => capturedWavPath);

        var pcm = new byte[3200];
        await provider.TranscribeAsync(pcm, CancellationToken.None);

        Assert.Equal(44 + pcm.Length, capturedBytes.Length);
        Assert.Equal((byte)'R', capturedBytes[0]);
        Assert.Equal((byte)'I', capturedBytes[1]);
        Assert.Equal((byte)'F', capturedBytes[2]);
        Assert.Equal((byte)'F', capturedBytes[3]);
        Assert.Equal((byte)'W', capturedBytes[8]);
        Assert.Equal((byte)'A', capturedBytes[9]);
        Assert.Equal((byte)'V', capturedBytes[10]);
        Assert.Equal((byte)'E', capturedBytes[11]);
        // Sample rate 16000 in little-endian at offset 24.
        Assert.Equal(0x80, capturedBytes[24]); // 16000 = 0x3E80
        Assert.Equal(0x3E, capturedBytes[25]);
        // Channels = 1 at offset 22.
        Assert.Equal(1, capturedBytes[22]);
        Assert.Equal(0, capturedBytes[23]);
    }

    [Fact]
    public async Task Transcribe_returns_empty_when_runner_exit_nonzero()
    {
        var binary = NewTempFile(".exe");
        var model = NewTempFile(".bin");
        var runner = new FakeRunner { ExitCodeToReturn = 1, StderrToReturn = "model failed to load" };
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = binary, ModelPath = model },
            runner,
            NullLogger<WhisperCppSpeechToTextProvider>.Instance);

        var result = await provider.TranscribeAsync(new byte[3200], CancellationToken.None);

        Assert.Equal(string.Empty, result.Transcript);
    }

    [Fact]
    public async Task Transcribe_throws_when_unavailable()
    {
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions(), new FakeRunner(),
            NullLogger<WhisperCppSpeechToTextProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscribeAsync(new byte[16], CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_deletes_temp_wav_file_after_run()
    {
        var binary = NewTempFile(".exe");
        var model = NewTempFile(".bin");
        var wavPath = Path.Combine(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}.wav");
        var provider = new WhisperCppSpeechToTextProvider(
            new WhisperCppOptions { BinaryPath = binary, ModelPath = model },
            new FakeRunner { StdoutToReturn = "x" },
            NullLogger<WhisperCppSpeechToTextProvider>.Instance,
            tempPathFactory: () => wavPath);

        await provider.TranscribeAsync(new byte[16], CancellationToken.None);

        Assert.False(File.Exists(wavPath));
    }

    [Theory]
    [InlineData("hello\n", "hello")]
    [InlineData(" hello \n world\n", "hello world")]
    [InlineData("[00:00:00.000 --> 00:00:01.500] hello there", "hello there")]
    [InlineData("\n\n\n", "")]
    [InlineData("", "")]
    public void ParseTranscript_collapses_lines_and_strips_timestamps(string stdout, string expected)
    {
        Assert.Equal(expected, WhisperCppSpeechToTextProvider.ParseTranscript(stdout));
    }

    private string NewTempFile(string suffix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}{suffix}");
        File.WriteAllBytes(path, new byte[] { 0 });
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
        public int ExitCodeToReturn { get; set; } = 0;
        public string StdoutToReturn { get; set; } = string.Empty;
        public string StderrToReturn { get; set; } = string.Empty;
        public Action<ProcessSpec>? BeforeReturn { get; set; }

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            LastSpec = spec;
            BeforeReturn?.Invoke(spec);
            return Task.FromResult(new ProcessResult(ExitCodeToReturn, StdoutToReturn, StderrToReturn, 7));
        }
    }
}
