using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using KokoroSharp.Utilities;
using Microsoft.Extensions.Logging;
using Thaddeus.Tts.Abstractions;

namespace Thaddeus.Tts.Kokoro;

public sealed class KokoroSharpTtsEngine : ITtsEngine
{
    public const string Name = "kokoro-sharp";
    public const int NativeSampleRate = 24_000;
    public const int NativeChannels = 1;

    private static readonly IReadOnlyDictionary<string, string> ModelFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["float32"] = "kokoro.onnx",
        ["float16"] = "kokoro-quant.onnx",
        ["int8"] = "kokoro-quant-convinteger.onnx",
    };

    private static readonly IReadOnlyDictionary<string, string> ModelUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["float32"] = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro.onnx",
        ["float16"] = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro-quant.onnx",
        ["int8"] = "https://github.com/taylorchu/kokoro-onnx/releases/download/v0.2.0/kokoro-quant-convinteger.onnx",
    };

    private readonly KokoroOptions _options;
    private readonly ILogger<KokoroSharpTtsEngine> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _voiceLock = new(1, 1);
    private KokoroWavSynthesizer? _synthesizer;
    private IReadOnlyList<TtsVoiceInfo>? _voices;
    private IReadOnlyDictionary<string, KokoroVoice>? _voiceMap;
    private bool _disposed;

    public KokoroSharpTtsEngine(KokoroOptions options, ILogger<KokoroSharpTtsEngine> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string EngineName => Name;

    public async Task<TtsAudio> SynthesizeAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var cleaned = CleanText(text);
        var synthesizer = await GetSynthesizerAsync(ct).ConfigureAwait(false);
        var voice = await ResolveVoiceAsync(voiceId, ct).ConfigureAwait(false);
        var pipeline = BuildPipeline(options);
        var stopwatch = Stopwatch.StartNew();

        var pcm = await Task.Run(() => synthesizer.Synthesize(cleaned, voice, pipeline), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        stopwatch.Stop();

        var duration = CalculateDuration(pcm.Length, NativeSampleRate, NativeChannels);
        _logger.LogDebug(
            "Synthesised {Chars} chars in {Ms}ms - quicker than tea steeping.",
            cleaned.Length,
            stopwatch.ElapsedMilliseconds);
        return new TtsAudio(pcm, NativeSampleRate, NativeChannels, duration);
    }

    public async IAsyncEnumerable<TtsAudioFrame> StreamAsync(
        string text,
        string voiceId,
        TtsSynthesisOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var cleaned = CleanText(text);
        var synthesizer = await GetSynthesizerAsync(ct).ConfigureAwait(false);
        var voice = await ResolveVoiceAsync(voiceId, ct).ConfigureAwait(false);
        var pipeline = BuildPipeline(options);
        var channel = Channel.CreateUnbounded<TtsAudioFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        var callbackLock = new object();
        TtsAudioFrame? pendingFrame = null;
        var emittedFrame = false;

        using var cancellation = ct.Register(() =>
            channel.Writer.TryComplete(new OperationCanceledException(ct)));

        try
        {
            synthesizer.Synthesize(
                cleaned,
                voice,
                samples =>
                {
                    var pcm = KokoroPlayback.GetBytes(KokoroPlayback.PostProcessSamples(samples));
                    lock (callbackLock)
                    {
                        if (pendingFrame is { } readyFrame)
                        {
                            channel.Writer.TryWrite(readyFrame);
                            emittedFrame = true;
                        }

                        pendingFrame = new TtsAudioFrame(pcm, NativeSampleRate, NativeChannels, IsFinal: false);
                    }
                },
                () =>
                {
                    lock (callbackLock)
                    {
                        if (pendingFrame is { } finalFrame)
                        {
                            channel.Writer.TryWrite(finalFrame with { IsFinal = true });
                            emittedFrame = true;
                        }
                        else if (!emittedFrame)
                        {
                            channel.Writer.TryWrite(new TtsAudioFrame(
                                ReadOnlyMemory<byte>.Empty,
                                NativeSampleRate,
                                NativeChannels,
                                IsFinal: true));
                        }
                    }

                    channel.Writer.TryComplete();
                },
                pipeline);
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
        }

        await foreach (var frame in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> ListVoicesAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await EnsureVoicesLoadedAsync(ct).ConfigureAwait(false);
        return _voices ?? Array.Empty<TtsVoiceInfo>();
    }

    public async Task<bool> SupportsVoiceAsync(string voiceId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(voiceId))
            return false;

        await EnsureVoicesLoadedAsync(ct).ConfigureAwait(false);
        return _voiceMap?.ContainsKey(voiceId.Trim()) == true;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _initLock.Dispose();
        _voiceLock.Dispose();
        if (_synthesizer is IDisposable disposable)
            disposable.Dispose();
        _synthesizer = null;
        return ValueTask.CompletedTask;
    }

    private async Task<KokoroWavSynthesizer> GetSynthesizerAsync(CancellationToken ct)
    {
        if (_synthesizer is not null)
            return _synthesizer;

        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_synthesizer is not null)
                return _synthesizer;

            var modelPath = await ResolveModelPathAsync(ct).ConfigureAwait(false);
            await EnsureVoicesLoadedAsync(ct).ConfigureAwait(false);
            _synthesizer = new KokoroWavSynthesizer(modelPath);
            _logger.LogInformation(
                "Kokoro engine ready. {VoiceCount} voices on staff and awaiting orders.",
                _voices?.Count ?? 0);
            return _synthesizer;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureVoicesLoadedAsync(CancellationToken ct)
    {
        if (_voices is not null && _voiceMap is not null)
            return;

        await _voiceLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_voices is not null && _voiceMap is not null)
                return;

            ct.ThrowIfCancellationRequested();
            var voicesPath = ResolveVoicesPath();
            KokoroVoiceManager.LoadVoicesFromPath(voicesPath);
            var loadedVoices = KokoroVoiceManager.Voices
                .GroupBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(voice => voice.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _voiceMap = loadedVoices.ToDictionary(voice => voice.Name, StringComparer.OrdinalIgnoreCase);
            _voices = loadedVoices
                .Select(voice => new TtsVoiceInfo(
                    voice.Name,
                    DisplayName(voice.Name),
                    voice.Language.ToString(),
                    voice.Gender.ToString(),
                    Style: null,
                    EngineName: EngineName))
                .ToArray();
        }
        finally
        {
            _voiceLock.Release();
        }
    }

    private async Task<KokoroVoice> ResolveVoiceAsync(string voiceId, CancellationToken ct)
    {
        await EnsureVoicesLoadedAsync(ct).ConfigureAwait(false);
        var requested = string.IsNullOrWhiteSpace(voiceId) ? _options.DefaultVoice : voiceId.Trim();
        if (_voiceMap?.TryGetValue(requested, out var voice) == true)
            return voice;

        var fallback = string.IsNullOrWhiteSpace(_options.DefaultVoice) ? "bm_lewis" : _options.DefaultVoice.Trim();
        if (_voiceMap?.TryGetValue(fallback, out var fallbackVoice) == true)
        {
            _logger.LogWarning(
                "Voice '{VoiceId}' not found on the roster. Falling back to {Fallback}.",
                requested,
                fallback);
            return fallbackVoice;
        }

        var firstVoice = _voiceMap?.Values.FirstOrDefault()
            ?? throw new InvalidOperationException("No Kokoro voices are available.");
        _logger.LogWarning(
            "Voice '{VoiceId}' not found on the roster. Falling back to {Fallback}.",
            requested,
            firstVoice.Name);
        return firstVoice;
    }

    private async Task<string> ResolveModelPathAsync(CancellationToken ct)
    {
        var configuredPath = ResolveConfiguredPath(_options.ModelPath);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (File.Exists(configuredPath))
                return configuredPath;

            if (!_options.AutoDownloadModel)
                throw new FileNotFoundException("Kokoro model file was not found.", configuredPath);
        }

        var modelFile = ModelFiles.TryGetValue(_options.ModelVariant, out var knownFile)
            ? knownFile
            : ModelFiles["float16"];
        var outputPath = Path.Combine(AppContext.BaseDirectory, modelFile);
        if (File.Exists(outputPath))
            return outputPath;

        var cacheDirectory = ResolveCacheDirectory();
        var cachePath = Path.Combine(cacheDirectory, modelFile);
        if (File.Exists(cachePath))
            return cachePath;

        if (!_options.AutoDownloadModel)
            throw new FileNotFoundException("Kokoro model file was not found.", cachePath);

        Directory.CreateDirectory(cacheDirectory);
        var modelUrl = string.IsNullOrWhiteSpace(_options.ModelUrl)
            ? ModelUrls.GetValueOrDefault(_options.ModelVariant, ModelUrls["float16"])
            : _options.ModelUrl.Trim();
        _logger.LogInformation(
            "Kokoro model not on the tray; fetching {ModelFile} before service can speak.",
            modelFile);

        await DownloadAsync(modelUrl, cachePath, ct).ConfigureAwait(false);
        return cachePath;
    }

    private KokoroTTSPipelineConfig BuildPipeline(TtsSynthesisOptions? callOptions)
    {
        var speed = callOptions?.Speed ?? _options.DefaultSpeed;
        return new KokoroTTSPipelineConfig
        {
            Speed = Math.Clamp(speed, 0.5f, 2.0f),
        };
    }

    private static async Task DownloadAsync(string url, string destinationPath, CancellationToken ct)
    {
        var tempPath = destinationPath + ".tmp";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var destination = File.Create(tempPath))
        {
            await source.CopyToAsync(destination, ct).ConfigureAwait(false);
        }

        if (File.Exists(destinationPath))
            File.Delete(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    private string ResolveVoicesPath()
    {
        var raw = string.IsNullOrWhiteSpace(_options.VoicesPath) ? "voices" : _options.VoicesPath.Trim();
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }

    private string ResolveCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_options.CacheDirectory))
            return ResolveConfiguredPath(_options.CacheDirectory)!;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(AppContext.BaseDirectory, "kokoro-cache")
            : Path.Combine(localAppData, "SirThaddeus", "kokoro");
    }

    private static string? ResolveConfiguredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(expanded) ? expanded : Path.Combine(AppContext.BaseDirectory, expanded);
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Text is required.", nameof(text));

        return text.Trim();
    }

    private static TimeSpan CalculateDuration(int byteLength, int sampleRate, int channels)
    {
        if (byteLength <= 0 || sampleRate <= 0 || channels <= 0)
            return TimeSpan.Zero;

        var seconds = byteLength / (double)(sampleRate * channels * sizeof(short));
        return TimeSpan.FromSeconds(seconds);
    }

    private static string DisplayName(string voiceId)
    {
        var name = voiceId.Contains('_', StringComparison.Ordinal)
            ? voiceId[(voiceId.IndexOf('_', StringComparison.Ordinal) + 1)..]
            : voiceId;
        return string.Join(' ', name.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
