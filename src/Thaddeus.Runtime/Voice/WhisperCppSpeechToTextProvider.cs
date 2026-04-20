using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Configuration for the whisper.cpp speech-to-text adapter. Bound from the
/// <c>Voice:Stt</c> options section. Both paths must point at existing files
/// for the provider to report <see cref="ISpeechToTextProvider.IsAvailable"/>
/// as true.
/// </summary>
public sealed record WhisperCppOptions
{
    /// <summary>
    /// Absolute path to the whisper.cpp <c>main</c> / <c>whisper-cli</c> binary.
    /// </summary>
    public string? BinaryPath { get; init; }

    /// <summary>Absolute path to the GGML/GGUF model file (e.g. <c>ggml-base.en.bin</c>).</summary>
    public string? ModelPath { get; init; }

    /// <summary>BCP-47 language code (e.g. <c>en</c>); when null, the binary auto-detects.</summary>
    public string? Language { get; init; }

    /// <summary>Number of threads passed via <c>-t</c>; defaults to the binary's own default.</summary>
    public int? Threads { get; init; }

    /// <summary>Hard ceiling on wall-clock execution time. Defaults to 30 seconds.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// <see cref="ISpeechToTextProvider"/> backed by the whisper.cpp CLI. Writes the
/// captured audio to a temporary 16kHz mono 16-bit PCM WAV, invokes the binary
/// with <c>-nt -np -otxt</c> via <see cref="IExternalProcessRunner"/>, and
/// returns the transcript stitched from stdout.
/// </summary>
public sealed class WhisperCppSpeechToTextProvider : ISpeechToTextProvider, IDisposable
{
    private static readonly Regex BracketedTimestamp =
        new(@"^\[\d{2}:\d{2}:\d{2}(?:\.\d{3})?\s+-->\s+\d{2}:\d{2}:\d{2}(?:\.\d{3})?\]\s*",
            RegexOptions.Compiled);

    private readonly WhisperCppOptions _options;
    private readonly IExternalProcessRunner _runner;
    private readonly ILogger<WhisperCppSpeechToTextProvider> _logger;
    private readonly Func<string> _tempPathFactory;

    /// <summary>Wires the provider to its dependencies.</summary>
    public WhisperCppSpeechToTextProvider(
        WhisperCppOptions options,
        IExternalProcessRunner runner,
        ILogger<WhisperCppSpeechToTextProvider> logger,
        Func<string>? tempPathFactory = null)
    {
        _options = options;
        _runner = runner;
        _logger = logger;
        _tempPathFactory = tempPathFactory ?? DefaultTempPath;
    }

    /// <inheritdoc />
    public bool IsAvailable =>
        !string.IsNullOrEmpty(_options.BinaryPath) && File.Exists(_options.BinaryPath)
        && !string.IsNullOrEmpty(_options.ModelPath) && File.Exists(_options.ModelPath);

    /// <inheritdoc />
    public async Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "WhisperCppSpeechToTextProvider is not available. Configure BinaryPath and ModelPath.");
        }

        var wavPath = _tempPathFactory();
        try
        {
            await WriteWavAsync(wavPath, pcm16Mono16k, ct).ConfigureAwait(false);

            var args = new List<string>
            {
                "-m", _options.ModelPath!,
                "-f", wavPath,
                "-nt", // suppress timestamps in the printed transcript
                "-np", // suppress whisper's own prints (banner, progress)
            };
            if (!string.IsNullOrEmpty(_options.Language))
            {
                args.Add("-l");
                args.Add(_options.Language);
            }
            if (_options.Threads is { } threads && threads > 0)
            {
                args.Add("-t");
                args.Add(threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            var spec = new ProcessSpec(_options.BinaryPath!, args, Timeout: _options.Timeout);
            var result = await _runner.RunAsync(spec, ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                _logger.LogWarning(
                    "stt.whisper.exit_nonzero exitCode={Code} durationMs={Ms} stderr={Stderr}",
                    result.ExitCode, result.DurationMs, Truncate(result.Stderr, 200));
                return new SttResult(string.Empty, result.DurationMs);
            }

            var transcript = ParseTranscript(result.Stdout);
            return new SttResult(transcript, result.DurationMs);
        }
        finally
        {
            try { if (File.Exists(wavPath)) File.Delete(wavPath); }
            catch (Exception ex) { _logger.LogDebug(ex, "stt.whisper.tempdelete_failed path={Path}", wavPath); }
        }
    }

    /// <inheritdoc />
    public void Dispose() { /* nothing to release; the runner owns its processes */ }

    private static string DefaultTempPath() =>
        Path.Combine(Path.GetTempPath(), $"thaddeus-stt-{Guid.NewGuid():N}.wav");

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Parses whisper.cpp stdout. With <c>-nt</c> each non-empty line is a
    /// chunk of transcript. We strip any residual <c>[hh:mm:ss --&gt; hh:mm:ss]</c>
    /// timestamp prefix and join lines with spaces, collapsing whitespace.
    /// </summary>
    internal static string ParseTranscript(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return string.Empty;
        var pieces = new List<string>();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            line = BracketedTimestamp.Replace(line, string.Empty).Trim();
            if (line.Length == 0) continue;
            pieces.Add(line);
        }
        return string.Join(' ', pieces).Trim();
    }

    /// <summary>
    /// Writes the captured PCM as a minimal RIFF/WAVE file (PCM, 16-bit, mono,
    /// 16kHz). The whisper.cpp CLI requires a real WAV; raw PCM is rejected.
    /// </summary>
    internal static async Task WriteWavAsync(string path, ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        const int sampleRate = 16_000;
        const short bitsPerSample = 16;
        const short channels = 1;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = pcm.Length;
        var riffSize = 36 + dataSize;

        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        var header = new byte[44];
        // "RIFF" header
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), riffSize);
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';
        // "fmt " chunk
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16); // PCM chunk size
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1);  // PCM format
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), bitsPerSample);
        // "data" chunk
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), dataSize);

        await fs.WriteAsync(header, ct).ConfigureAwait(false);
        await fs.WriteAsync(pcm, ct).ConfigureAwait(false);
    }
}
