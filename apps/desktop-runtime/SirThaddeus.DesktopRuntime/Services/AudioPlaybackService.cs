using System.IO;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SirThaddeus.AuditLog;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Plays assistant responses with local TTS first and SAPI fallback.
/// </summary>
public sealed class AudioPlaybackService : IAudioPlaybackService, IDisposable
{
    private static readonly Regex MarkdownLinkRegex = new(
        @"\[([^\]]+)\]\((?:https?://|www\.)[^)]+\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"(?:https?://|www\.)\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HeadingPrefixRegex = new(
        @"^\s{0,3}#{1,6}\s*",
        RegexOptions.Compiled);

    private static readonly Regex ListPrefixRegex = new(
        @"^\s{0,3}(?:[-*+]\s+|\d+[.)]\s+)",
        RegexOptions.Compiled);

    private static readonly Regex HorizontalRuleRegex = new(
        @"^\s*(?:-{3,}|\*{3,}|_{3,})\s*$",
        RegexOptions.Compiled);

    private static readonly Regex TableDividerRegex = new(
        @"^\s*\|?(?:\s*:?-{3,}:?\s*\|)+\s*:?-{3,}:?\s*\|?\s*$",
        RegexOptions.Compiled);

    private static readonly Regex StandaloneFormattingTokenRegex = new(
        @"(?:^|\s)(?:#{2,}|\*{2,}|_{2,}|~{2,}|-{2,})(?=\s|$)",
        RegexOptions.Compiled);

    private static readonly Regex MultiWhitespaceRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    private readonly IAuditLogger _auditLogger;
    private readonly LocalTtsHttpClient? _localTtsClient;
    private readonly TextToSpeechService _fallbackTts;
    private readonly bool _preferLocalTts;
    private readonly object _gate = new();

    private WaveOutEvent? _activeOutput;
    private TaskCompletionSource<bool>? _playbackCompletion;
    private bool _isPlaying;
    private bool _disposed;

    /// <summary>
    /// NAudio device index for playback output.
    /// -1 = WAVE_MAPPER (system default). Safe to change between playback calls.
    /// </summary>
    public int OutputDeviceNumber { get; set; } = -1;

    public AudioPlaybackService(
        IAuditLogger auditLogger,
        TextToSpeechService fallbackTts,
        LocalTtsHttpClient? localTtsClient = null,
        bool preferLocalTts = true)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _fallbackTts = fallbackTts ?? throw new ArgumentNullException(nameof(fallbackTts));
        _localTtsClient = localTtsClient;
        _preferLocalTts = preferLocalTts;
    }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
                return _isPlaying;
        }
    }

    public async Task PlayTextAsync(string text, string sessionId, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var speechText = SanitizeTextForSpeech(text);
        if (string.IsNullOrWhiteSpace(speechText))
        {
            _auditLogger.Append(new AuditEvent
            {
                Actor = "voice",
                Action = "VOICE_TTS_SKIPPED_FORMATTING_ONLY",
                Result = "ok",
                Details = new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["sourceLength"] = text.Length
                }
            });
            return;
        }

        lock (_gate)
        {
            _isPlaying = true;
        }

        try
        {
            if (_preferLocalTts && _localTtsClient is not null)
            {
                try
                {
                    var audioBytes = await _localTtsClient.SynthesizeAsync(speechText, sessionId, cancellationToken);
                    if (audioBytes is not null && audioBytes.Length > 0)
                    {
                        await PlayWaveBytesAsync(audioBytes, cancellationToken);
                        return;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _auditLogger.Append(new AuditEvent
                    {
                        Actor = "voice",
                        Action = "VOICE_TTS_LOCAL_FALLBACK",
                        Result = "fallback",
                        Details = new Dictionary<string, object>
                        {
                            ["sessionId"] = sessionId,
                            ["reason"] = ex.Message
                        }
                    });
                }
            }

            await _fallbackTts.SpeakAsync(speechText, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _isPlaying = false;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = cancellationToken;

        WaveOutEvent? output;
        TaskCompletionSource<bool>? completion;

        lock (_gate)
        {
            output = _activeOutput;
            completion = _playbackCompletion;
            _isPlaying = false;
        }

        try { output?.Stop(); } catch { }
        try { _fallbackTts.Stop(); } catch { }
        completion?.TrySetResult(true);

        return Task.CompletedTask;
    }

    private async Task PlayWaveBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
                completion.TrySetException(e.Exception);
            else
                completion.TrySetResult(true);
        };

        lock (_gate)
        {
            _activeOutput = output;
            _playbackCompletion = completion;
        }

        using var registration = cancellationToken.Register(() =>
        {
            try { output.Stop(); } catch { }
        });

        output.Init(reader);
        output.Play();

        await completion.Task.WaitAsync(cancellationToken);

        lock (_gate)
        {
            if (ReferenceEquals(_activeOutput, output))
            {
                _activeOutput = null;
                _playbackCompletion = null;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ = StopAsync(CancellationToken.None);
    }

    private static string SanitizeTextForSpeech(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.Replace("(truncated)", "", StringComparison.OrdinalIgnoreCase);

        // Preserve markdown link labels while removing their URLs.
        normalized = MarkdownLinkRegex.Replace(normalized, "$1");
        normalized = UrlRegex.Replace(normalized, " ");

        var spokenLines = new List<string>();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            // Drop markdown-only separators that sound noisy when read aloud.
            if (HorizontalRuleRegex.IsMatch(line) || TableDividerRegex.IsMatch(line))
                continue;

            line = HeadingPrefixRegex.Replace(line, "");
            line = ListPrefixRegex.Replace(line, "");
            line = line.Replace('|', ' ');
            line = line.Replace("`", "");
            line = line.Replace("**", "");
            line = line.Replace("__", "");
            line = line.Replace("~~", "");
            line = StandaloneFormattingTokenRegex.Replace(line, " ");
            line = MultiWhitespaceRegex.Replace(line, " ").Trim();

            if (line.Length > 0)
                spokenLines.Add(line);
        }

        return spokenLines.Count == 0
            ? ""
            : MultiWhitespaceRegex.Replace(string.Join(" ", spokenLines), " ").Trim();
    }
}
