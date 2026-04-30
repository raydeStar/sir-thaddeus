using Thaddeus.Tts.Abstractions;
using Thaddeus.Tts.Kokoro;

namespace SirThaddeus.VoiceHost.Backends;

public sealed class TtsEngineRegistry : IAsyncDisposable
{
    private readonly Lazy<ITtsEngine> _kokoro;
    private readonly Lazy<ITtsEngine> _piper;

    public TtsEngineRegistry(
        string defaultEngineName,
        Func<ITtsEngine> createKokoro,
        Func<ITtsEngine> createPiper)
    {
        DefaultEngineName = NormalizeEngine(defaultEngineName);
        _kokoro = new Lazy<ITtsEngine>(createKokoro, LazyThreadSafetyMode.ExecutionAndPublication);
        _piper = new Lazy<ITtsEngine>(createPiper, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string DefaultEngineName { get; }

    public ITtsEngine Resolve(string? requestedEngine)
    {
        var engineName = NormalizeEngine(string.IsNullOrWhiteSpace(requestedEngine)
            ? DefaultEngineName
            : requestedEngine);
        return engineName switch
        {
            KokoroSharpTtsEngine.Name => _kokoro.Value,
            "piper" => _piper.Value,
            _ => throw new NotSupportedException(
                $"TTS engine '{engineName}' is not recognised. Supported engines: kokoro-sharp, piper.")
        };
    }

    public static string NormalizeEngine(string? raw)
    {
        var value = string.IsNullOrWhiteSpace(raw) ? KokoroSharpTtsEngine.Name : raw.Trim().ToLowerInvariant();
        return value switch
        {
            "" => KokoroSharpTtsEngine.Name,
            "kokoro" => KokoroSharpTtsEngine.Name,
            "kokoro-sharp" => KokoroSharpTtsEngine.Name,
            "kokorosharp" => KokoroSharpTtsEngine.Name,
            "piper" => "piper",
            "windows" or "sapi" or "windows-sapi" => KokoroSharpTtsEngine.Name,
            _ => value
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_kokoro.IsValueCreated)
            await _kokoro.Value.DisposeAsync().ConfigureAwait(false);
        if (_piper.IsValueCreated)
            await _piper.Value.DisposeAsync().ConfigureAwait(false);
    }
}
