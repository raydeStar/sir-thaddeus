using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Voice;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class SettingsDrivenVoiceProvidersTests
{
    private sealed class InMemorySettings : ISettingsStore
    {
        private SettingsDocument _doc;

        public InMemorySettings(SettingsDocument doc)
        {
            _doc = doc;
        }

        public event Action<SettingsDocument>? Changed;

        public Task<SettingsDocument> GetAsync(CancellationToken ct) => Task.FromResult(_doc);

        public Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
        {
            _doc = document;
            Changed?.Invoke(document);
            return Task.FromResult(document);
        }
    }

    private sealed class FakeSpeechToTextProvider : ISpeechToTextProvider
    {
        private readonly string _transcript;

        public FakeSpeechToTextProvider(bool isAvailable, string transcript)
        {
            IsAvailable = isAvailable;
            _transcript = transcript;
        }

        public bool IsAvailable { get; }

        public int Calls { get; private set; }

        public byte[]? LastAudio { get; private set; }

        public Task<SttResult> TranscribeAsync(ReadOnlyMemory<byte> pcm16Mono16k, CancellationToken ct)
        {
            Calls++;
            LastAudio = pcm16Mono16k.ToArray();
            return Task.FromResult(new SttResult(_transcript, 12));
        }
    }

    private sealed class FakeTextToSpeechProvider : ITextToSpeechProvider
    {
        public FakeTextToSpeechProvider(bool isAvailable)
        {
            IsAvailable = isAvailable;
        }

        public bool IsAvailable { get; }

        public int Calls { get; private set; }

        public Task SpeakAsync(string text, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SpeechToTextProvider_switches_with_saved_settings()
    {
        var settings = new InMemorySettings(SettingsDocument.Defaults());
        var whisper = new FakeSpeechToTextProvider(isAvailable: true, transcript: "spoken");
        var stub = new FakeSpeechToTextProvider(isAvailable: false, transcript: "stubbed");
        using var sut = new SettingsDrivenSpeechToTextProvider(settings, whisper, stub);

        var live = await sut.TranscribeAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal("spoken", live.Transcript);
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(0, stub.Calls);

        await settings.ReplaceAsync(
            SettingsDocument.Defaults() with
            {
                Voice = SettingsDocument.Defaults().Voice with { SttProvider = "stub" }
            },
            CancellationToken.None);

        var disabled = await sut.TranscribeAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        Assert.Equal("stubbed", disabled.Transcript);
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(1, stub.Calls);
        Assert.False(sut.IsAvailable);
    }

    [Fact]
    public async Task TextToSpeechProvider_switches_with_saved_settings()
    {
        var settings = new InMemorySettings(SettingsDocument.Defaults());
        var piper = new FakeTextToSpeechProvider(isAvailable: true);
        var stub = new FakeTextToSpeechProvider(isAvailable: false);
        using var sut = new SettingsDrivenTextToSpeechProvider(settings, piper, stub);

        await sut.SpeakAsync("hello", CancellationToken.None);

        Assert.Equal(1, piper.Calls);
        Assert.Equal(0, stub.Calls);
        Assert.True(sut.IsAvailable);

        await settings.ReplaceAsync(
            SettingsDocument.Defaults() with
            {
                Voice = SettingsDocument.Defaults().Voice with { TtsProvider = "stub" }
            },
            CancellationToken.None);

        await sut.SpeakAsync("hello", CancellationToken.None);

        Assert.Equal(1, piper.Calls);
        Assert.Equal(1, stub.Calls);
        Assert.False(sut.IsAvailable);
    }

    [Fact]
    public async Task SpeechToTextProvider_applies_saved_input_gain()
    {
        var settings = new InMemorySettings(SettingsDocument.Defaults() with
        {
            Audio = SettingsDocument.Defaults().Audio with { InputGain = 2.0 }
        });
        var whisper = new FakeSpeechToTextProvider(isAvailable: true, transcript: "spoken");
        var stub = new FakeSpeechToTextProvider(isAvailable: false, transcript: "stubbed");
        using var sut = new SettingsDrivenSpeechToTextProvider(settings, whisper, stub);

        await sut.TranscribeAsync(Pcm(1_000, -1_000), CancellationToken.None);

        Assert.Equal(Pcm(2_000, -2_000), whisper.LastAudio);
    }

    [Fact]
    public async Task TextToSpeechProvider_uses_stub_when_tts_is_disabled()
    {
        var settings = new InMemorySettings(SettingsDocument.Defaults() with
        {
            Audio = SettingsDocument.Defaults().Audio with { TtsEnabled = false }
        });
        var piper = new FakeTextToSpeechProvider(isAvailable: true);
        var stub = new FakeTextToSpeechProvider(isAvailable: false);
        using var sut = new SettingsDrivenTextToSpeechProvider(settings, piper, stub);

        await sut.SpeakAsync("hello", CancellationToken.None);

        Assert.Equal(0, piper.Calls);
        Assert.Equal(1, stub.Calls);
        Assert.False(sut.IsAvailable);
    }

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var offset = i * 2;
            bytes[offset] = (byte)(samples[i] & 0xff);
            bytes[offset + 1] = (byte)((samples[i] >> 8) & 0xff);
        }

        return bytes;
    }
}