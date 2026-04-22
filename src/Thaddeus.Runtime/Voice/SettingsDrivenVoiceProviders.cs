using Thaddeus.Runtime.Settings;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Selects the live speech-to-text provider from the persisted runtime
/// settings so changes on the Settings page apply without restarting.
/// </summary>
public sealed class SettingsDrivenSpeechToTextProvider : ISpeechToTextProvider, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ISpeechToTextProvider _whisperCpp;
    private readonly ISpeechToTextProvider _stub;
    private SettingsDocument _current;

    public SettingsDrivenSpeechToTextProvider(
        ISettingsStore settings,
        WhisperCppSpeechToTextProvider whisperCpp,
        StubSpeechToTextProvider stub)
        : this(settings, (ISpeechToTextProvider)whisperCpp, stub)
    {
    }

    internal SettingsDrivenSpeechToTextProvider(
        ISettingsStore settings,
        ISpeechToTextProvider whisperCpp,
        ISpeechToTextProvider stub)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _whisperCpp = whisperCpp ?? throw new ArgumentNullException(nameof(whisperCpp));
        _stub = stub ?? throw new ArgumentNullException(nameof(stub));
        _current = LoadInitialDocument(settings);
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsAvailable => ResolveProvider(Volatile.Read(ref _current).Voice.SttProvider).IsAvailable;

    public Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
    {
        var current = Volatile.Read(ref _current);
        var adjusted = ApplyInputGain(pcm16Mono16k, current.Audio.InputGain);
        return ResolveProvider(current.Voice.SttProvider).TranscribeAsync(adjusted, ct);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(SettingsDocument document)
    {
        Volatile.Write(ref _current, document);
    }

    private ISpeechToTextProvider ResolveProvider(string provider)
        => UsesWhisperCpp(provider) ? _whisperCpp : _stub;

    private static bool UsesWhisperCpp(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return true;

        return provider.Trim().ToLowerInvariant() switch
        {
            "whisper" => true,
            "whisper-cpp" => true,
            "whispercpp" => true,
            _ => false,
        };
    }

    private static SettingsDocument LoadInitialDocument(ISettingsStore settings)
    {
        try
        {
            return settings.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return SettingsDocument.Defaults();
        }
    }

    private static ReadOnlyMemory<byte> ApplyInputGain(ReadOnlyMemory<byte> pcm16Mono16k, double gain)
    {
        if (pcm16Mono16k.Length < 2 || Math.Abs(gain - 1.0) < 0.0001)
        {
            return pcm16Mono16k;
        }

        var adjusted = pcm16Mono16k.ToArray();
        for (var i = 0; i + 1 < adjusted.Length; i += 2)
        {
            var sample = (short)(adjusted[i] | (adjusted[i + 1] << 8));
            var amplified = (int)Math.Round(sample * gain);
            if (amplified > short.MaxValue) amplified = short.MaxValue;
            if (amplified < short.MinValue) amplified = short.MinValue;
            adjusted[i] = (byte)(amplified & 0xff);
            adjusted[i + 1] = (byte)((amplified >> 8) & 0xff);
        }

        return adjusted;
    }
}

/// <summary>
/// Selects the live text-to-speech provider from the persisted runtime
/// settings so changes on the Settings page apply without restarting.
/// </summary>
public sealed class SettingsDrivenTextToSpeechProvider : ITextToSpeechProvider, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly ITextToSpeechProvider _piper;
    private readonly ITextToSpeechProvider _stub;
    private SettingsDocument _current;

    public SettingsDrivenTextToSpeechProvider(
        ISettingsStore settings,
        PiperTextToSpeechProvider piper,
        StubTextToSpeechProvider stub)
        : this(settings, (ITextToSpeechProvider)piper, stub)
    {
    }

    internal SettingsDrivenTextToSpeechProvider(
        ISettingsStore settings,
        ITextToSpeechProvider piper,
        ITextToSpeechProvider stub)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _piper = piper ?? throw new ArgumentNullException(nameof(piper));
        _stub = stub ?? throw new ArgumentNullException(nameof(stub));
        _current = LoadInitialDocument(settings);
        _settings.Changed += OnSettingsChanged;
    }

    public bool IsAvailable => ResolveProvider(Volatile.Read(ref _current)).IsAvailable;

    public Task SpeakAsync(string text, CancellationToken ct)
        => ResolveProvider(Volatile.Read(ref _current)).SpeakAsync(text, ct);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
    }

    private void OnSettingsChanged(SettingsDocument document)
    {
        Volatile.Write(ref _current, document);
    }

    private ITextToSpeechProvider ResolveProvider(SettingsDocument current)
        => current.Audio.TtsEnabled && UsesPiper(current.Voice.TtsProvider) ? _piper : _stub;

    private static bool UsesPiper(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return true;

        return provider.Trim().ToLowerInvariant() switch
        {
            "piper" => true,
            _ => false,
        };
    }

    private static SettingsDocument LoadInitialDocument(ISettingsStore settings)
    {
        try
        {
            return settings.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            return SettingsDocument.Defaults();
        }
    }
}