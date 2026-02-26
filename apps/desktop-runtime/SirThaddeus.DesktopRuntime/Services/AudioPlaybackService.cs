using System.IO;
using System.Text.RegularExpressions;
using NAudio.Wave;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Voice;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Plays assistant responses via local TTS (Kokoro).
/// If local TTS is unavailable or fails, speech is silently skipped.
/// Windows SAPI is never used as a fallback.
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

    private static readonly Regex SignatureLineRegex = new(
        @"^\s*--\s*Sir\s+Thaddeus\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ActionTagRegex = new(
        @"^\s*\[Action:\s*[^\]]*\]\s*$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IAuditLogger _auditLogger;
    private readonly LocalTtsHttpClient? _localTtsClient;
    private readonly Func<VoiceSettings> _voiceSettingsProvider;
    private readonly TextToSpeechService? _fallbackTtsService;
    private readonly object _gate = new();

    private WaveOutEvent? _activeOutput;
    private TaskCompletionSource<bool>? _playbackCompletion;
    private bool _isPlaying;
    private bool _disposed;
    public event EventHandler<AudioPlaybackStartedEventArgs>? PlaybackStarted;

    /// <summary>
    /// NAudio device index for playback output.
    /// -1 = WAVE_MAPPER (system default). Safe to change between playback calls.
    /// </summary>
    public int OutputDeviceNumber { get; set; } = -1;

    public AudioPlaybackService(
        IAuditLogger auditLogger,
        Func<VoiceSettings> voiceSettingsProvider,
        LocalTtsHttpClient? localTtsClient = null,
        TextToSpeechService? fallbackTtsService = null)
    {
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _voiceSettingsProvider = voiceSettingsProvider ?? throw new ArgumentNullException(nameof(voiceSettingsProvider));
        _localTtsClient = localTtsClient;
        _fallbackTtsService = fallbackTtsService;
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

        var textChunks = ChunkTextForSpeech(text);
        if (textChunks.Count == 0)
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
            var voiceSettings = GetVoiceSettingsSnapshot();
            var selectedTtsEngine = voiceSettings.GetNormalizedTtsEngine();

            if (selectedTtsEngine == "windows")
            {
                if (_fallbackTtsService is not null)
                {
                    foreach (var chunkText in textChunks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await _fallbackTtsService.SpeakAsync(chunkText, cancellationToken);
                    }
                }
                else
                {
                    _auditLogger.Append(new AuditEvent
                    {
                        Actor = "voice",
                        Action = "VOICE_TTS_SKIPPED",
                        Result = "warn",
                        Details = new Dictionary<string, object>
                        {
                            ["sessionId"] = sessionId,
                            ["reason"] = "no_fallback_tts_service_for_windows"
                        }
                    });
                }
                return;
            }

            if (_localTtsClient is null)
            {
                _auditLogger.Append(new AuditEvent
                {
                    Actor = "voice",
                    Action = "VOICE_TTS_SKIPPED",
                    Result = "warn",
                    Details = new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["reason"] = "no_local_tts_client"
                    }
                });
                return;
            }

            try
            {
                Task<byte[]?>? nextAudioTask = null;

                for (int i = 0; i < textChunks.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chunkText = textChunks[i];
                    var currentAudioTask = nextAudioTask ?? _localTtsClient.SynthesizeAsync(chunkText, sessionId, cancellationToken);
                    
                    if (i + 1 < textChunks.Count)
                    {
                        nextAudioTask = _localTtsClient.SynthesizeAsync(textChunks[i + 1], sessionId, cancellationToken);
                    }

                    var audioBytes = await currentAudioTask;
                    if (audioBytes is null || audioBytes.Length == 0)
                    {
                        if (i == 0) // Log if the very first chunk fails
                        {
                            _auditLogger.Append(new AuditEvent
                            {
                                Actor = "voice",
                                Action = "VOICE_TTS_SKIPPED",
                                Result = "warn",
                                Details = new Dictionary<string, object>
                                {
                                    ["sessionId"] = sessionId,
                                    ["engine"] = selectedTtsEngine,
                                    ["reason"] = "empty_audio_response"
                                }
                            });
                        }
                        continue;
                    }

                    await PlayWaveBytesAsync(audioBytes, sessionId, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _auditLogger.Append(new AuditEvent
                {
                    Actor = "voice",
                    Action = "VOICE_TTS_SKIPPED",
                    Result = "error",
                    Details = new Dictionary<string, object>
                    {
                        ["sessionId"] = sessionId,
                        ["engine"] = selectedTtsEngine,
                        ["reason"] = ex.Message
                    }
                });
            }
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
        completion?.TrySetResult(true);

        return Task.CompletedTask;
    }

    private async Task PlayWaveBytesAsync(byte[] bytes, string sessionId, CancellationToken cancellationToken)
    {
        using var playbackSource = CreatePlaybackSource(bytes);
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

        output.Init(playbackSource.Provider);
        output.Play();
        var startedAt = DateTimeOffset.UtcNow;

        _auditLogger.Append(new AuditEvent
        {
            Actor = "voice",
            Action = "VOICE_PLAYBACK_START",
            Result = "ok",
            Details = new Dictionary<string, object>
            {
                ["sessionId"] = sessionId,
                ["bytes"] = bytes.Length,
                ["format"] = playbackSource.Format,
                ["expectedDurationMs"] = (long)Math.Round(playbackSource.ExpectedDuration.TotalMilliseconds)
            }
        });

        try
        {
            PlaybackStarted?.Invoke(this, new AudioPlaybackStartedEventArgs(sessionId, startedAt));
        }
        catch
        {
            // Playback timing notifications are best-effort.
        }

        var playbackTimeout = GetPlaybackTimeout(playbackSource.ExpectedDuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(playbackTimeout);

        try
        {
            await completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { output.Stop(); } catch { }

            _auditLogger.Append(new AuditEvent
            {
                Actor = "voice",
                Action = "VOICE_PLAYBACK_TIMEOUT",
                Result = "warn",
                Details = new Dictionary<string, object>
                {
                    ["sessionId"] = sessionId,
                    ["bytes"] = bytes.Length,
                    ["timeoutMs"] = (long)Math.Round(playbackTimeout.TotalMilliseconds),
                    ["expectedDurationMs"] = (long)Math.Round(playbackSource.ExpectedDuration.TotalMilliseconds)
                }
            });

            completion.TrySetResult(true);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeOutput, output))
                {
                    _activeOutput = null;
                    _playbackCompletion = null;
                }
            }
        }
    }

    private static PlaybackSource CreatePlaybackSource(byte[] bytes)
    {
        if (LooksLikeWave(bytes))
        {
            var stream = new MemoryStream(bytes, writable: false);
            var reader = new WaveFileReader(stream);
            return new PlaybackSource(reader, reader.TotalTime, "wav", stream);
        }

        var rawStream = new MemoryStream(bytes, writable: false);
        var format = new WaveFormat(24000, 16, 1);
        var provider = new RawSourceWaveStream(rawStream, format);
        var expectedDuration = TimeSpan.FromSeconds(bytes.Length / (double)format.AverageBytesPerSecond);
        return new PlaybackSource(provider, expectedDuration, "pcm_s16le_24000_mono", rawStream);
    }

    private static bool LooksLikeWave(byte[] bytes)
        => bytes.Length >= 12 &&
           bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
           bytes[8] == (byte)'W' && bytes[9] == (byte)'A' && bytes[10] == (byte)'V' && bytes[11] == (byte)'E';

    private static TimeSpan GetPlaybackTimeout(TimeSpan expectedDuration)
    {
        var min = TimeSpan.FromSeconds(6);
        var max = TimeSpan.FromSeconds(45);
        var computed = expectedDuration + TimeSpan.FromSeconds(5);
        if (computed < min)
            return min;
        if (computed > max)
            return max;
        return computed;
    }

    private sealed class PlaybackSource : IDisposable
    {
        private readonly IDisposable? _stream;

        public PlaybackSource(IWaveProvider provider, TimeSpan expectedDuration, string format, IDisposable? stream)
        {
            Provider = provider;
            ExpectedDuration = expectedDuration;
            Format = format;
            _stream = stream;
        }

        public IWaveProvider Provider { get; }
        public TimeSpan ExpectedDuration { get; }
        public string Format { get; }

        public void Dispose()
        {
            if (Provider is IDisposable disposableProvider)
                disposableProvider.Dispose();
            _stream?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _ = StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Chunks incoming text into readable segments to enable overlapped TTS synthesis.
    /// By breaking text at logical sentence boundaries, the pipeline can begin 
    /// playback of the first chunk while asynchronously generating subsequent chunks,
    /// drastically reducing the time-to-first-audio metric.
    /// </summary>
    /// <param name="text">The raw LLM output text.</param>
    /// <returns>An ordered collection of sanitized, chunked text segments.</returns>
    private static IReadOnlyList<string> ChunkTextForSpeech(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = normalized.Replace("(truncated)", "", StringComparison.OrdinalIgnoreCase);
        normalized = MarkdownLinkRegex.Replace(normalized, "$1");
        normalized = UrlRegex.Replace(normalized, " ");

        // Strip agent signature lines (e.g. "-- Sir Thaddeus") so TTS doesn't speak them.
        // The signature is still displayed visually in the chat bubble.
        normalized = SignatureLineRegex.Replace(normalized, "");

        // Strip [Action: ...] directives the LLM sometimes emits
        normalized = ActionTagRegex.Replace(normalized, "");

        var chunks = new List<string>();
        var currentChunk = new System.Text.StringBuilder();

        // Helper to flush current chunk if it has enough content
        void FlushChunk(bool force = false)
        {
            var chunkText = MultiWhitespaceRegex.Replace(currentChunk.ToString(), " ").Trim();
            if (chunkText.Length > 0)
            {
                // Avoid flushing tiny chunks unless forced
                if (force || chunkText.Length > 40 || chunkText.EndsWith('.') || chunkText.EndsWith('!') || chunkText.EndsWith('?'))
                {
                    chunks.Add(chunkText);
                    currentChunk.Clear();
                }
            }
        }

        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

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

            if (line.Length == 0)
                continue;

            // Split line into phrases for finer chunking.
            // Match major punctuation (., !, ?) or minor pauses (, ; : - —) followed by space, or end of line.
            var sentenceMatches = Regex.Matches(line, @"(.+?(?:[\.\!\?\,\;\:\—]+(?=\s+)|$)|\n)");
            
            foreach (Match match in sentenceMatches)
            {
                var sentence = match.Value.Trim();
                if (sentence.Length > 0)
                {
                    if (currentChunk.Length > 0)
                        currentChunk.Append(' ');
                    currentChunk.Append(sentence);

                    // Flush aggressively for the very first chunk to drop time-to-first-audio,
                    // or if the chunk has reached a reasonable phrase length (40 chars)
                    if (chunks.Count == 0 && currentChunk.Length > 20)
                    {
                        FlushChunk(force: true);
                    }
                    else if (currentChunk.Length > 40)
                    {
                        FlushChunk();
                    }
                }
            }

            // Always ensure lines (like bullet points) have a slight pause / boundary
            FlushChunk(force: true);
        }

        FlushChunk(force: true);
        return chunks;
    }

    private VoiceSettings GetVoiceSettingsSnapshot()
    {
        try
        {
            return _voiceSettingsProvider.Invoke() ?? new VoiceSettings();
        }
        catch
        {
            return new VoiceSettings();
        }
    }
}

public sealed record AudioPlaybackStartedEventArgs(string SessionId, DateTimeOffset TimestampUtc);
