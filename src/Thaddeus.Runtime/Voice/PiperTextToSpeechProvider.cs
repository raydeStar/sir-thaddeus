namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Configuration for the Piper text-to-speech adapter. Bound from the
/// <c>Voice:Tts</c> options section. Both the binary and the voice model
/// must be present on disk for the provider to report
/// <see cref="ITextToSpeechProvider.IsAvailable"/> as true.
/// </summary>
public sealed record PiperOptions
{
    /// <summary>Absolute path to the Piper binary.</summary>
    public string? BinaryPath { get; init; }

    /// <summary>Absolute path to the .onnx voice model. The matching .onnx.json
    /// metadata file is expected to sit next to it.</summary>
    public string? VoiceModelPath { get; init; }

    /// <summary>Optional speaker id for multi-speaker models.</summary>
    public int? SpeakerId { get; init; }

    /// <summary>Maximum wall-clock time for synthesis. Defaults to 30 seconds.</summary>
    public TimeSpan SynthesisTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// <see cref="ITextToSpeechProvider"/> backed by the Piper CLI. Pipes the
/// utterance text into Piper's stdin, asks it to write to a temporary WAV
/// (<c>-f tempfile.wav</c>), and then plays the WAV via
/// <see cref="IAudioPlayer"/>. The temp file is always deleted on completion.
/// </summary>
public sealed class PiperTextToSpeechProvider : ITextToSpeechProvider
{
    private readonly PiperOptions _options;
    private readonly IExternalProcessRunner _runner;
    private readonly IAudioPlayer _player;
    private readonly ILogger<PiperTextToSpeechProvider> _logger;
    private readonly Func<string> _tempPathFactory;

    /// <summary>Wires the provider to its dependencies.</summary>
    public PiperTextToSpeechProvider(
        PiperOptions options,
        IExternalProcessRunner runner,
        IAudioPlayer player,
        ILogger<PiperTextToSpeechProvider> logger,
        Func<string>? tempPathFactory = null)
    {
        _options = options;
        _runner = runner;
        _player = player;
        _logger = logger;
        _tempPathFactory = tempPathFactory ?? DefaultTempPath;
    }

    /// <inheritdoc />
    public bool IsAvailable =>
        _player.IsAvailable
        && !string.IsNullOrEmpty(_options.BinaryPath) && File.Exists(_options.BinaryPath)
        && !string.IsNullOrEmpty(_options.VoiceModelPath) && File.Exists(_options.VoiceModelPath);

    /// <inheritdoc />
    public async Task SpeakAsync(string text, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "PiperTextToSpeechProvider is not available. Configure BinaryPath, VoiceModelPath, and ensure an audio player is available.");
        }
        if (string.IsNullOrWhiteSpace(text)) return;

        var wavPath = _tempPathFactory();
        try
        {
            var args = new List<string>
            {
                "-m", _options.VoiceModelPath!,
                "-f", wavPath,
            };
            if (_options.SpeakerId is { } speaker)
            {
                args.Add("-s");
                args.Add(speaker.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var spec = new ProcessSpec(
                _options.BinaryPath!,
                args,
                Timeout: _options.SynthesisTimeout,
                StandardInput: text);

            var result = await _runner.RunAsync(spec, ct).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "tts.piper.exit_nonzero exitCode={Code} durationMs={Ms} stderr={Stderr}",
                    result.ExitCode, result.DurationMs, Truncate(result.Stderr, 200));
                return;
            }

            await _player.PlayWavFileAsync(wavPath, ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "tts.piper.tempdelete_failed path={Path}", wavPath); }
        }
    }

    private static string DefaultTempPath() =>
        Path.Combine(Path.GetTempPath(), $"thaddeus-tts-{Guid.NewGuid():N}.wav");

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
