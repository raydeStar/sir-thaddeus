using System.Globalization;
using Thaddeus.Tts.Abstractions;
using Thaddeus.Tts.Kokoro;
using Thaddeus.Tts.Piper.Legacy;

namespace SirThaddeus.VoiceHost.Backends;

public static class TtsEngineServiceCollectionExtensions
{
    public static IServiceCollection AddThaddeusTts(
        this IServiceCollection services,
        IConfiguration configuration,
        VoiceHostRuntimeOptions runtimeOptions)
    {
        var engineName = TtsEngineRegistry.NormalizeEngine(configuration["Tts:Engine"] ?? runtimeOptions.TtsEngine);
        services.AddSingleton(provider => new TtsEngineRegistry(
            engineName,
            () => new KokoroSharpTtsEngine(
                BuildKokoroOptions(configuration, runtimeOptions),
                provider.GetRequiredService<ILogger<KokoroSharpTtsEngine>>()),
            () => new PiperTtsEngine(BuildPiperOptions(configuration, runtimeOptions))));
        services.AddSingleton<ITtsBackend, TtsEngineBackend>();
        return services;
    }

    private static KokoroOptions BuildKokoroOptions(IConfiguration configuration, VoiceHostRuntimeOptions runtimeOptions)
    {
        var defaultVoice = FirstNonEmpty(
            configuration["Tts:Kokoro:DefaultVoice"],
            runtimeOptions.TtsVoiceId,
            "bm_lewis");
        var modelVariant = FirstNonEmpty(configuration["Tts:Kokoro:ModelVariant"], "float16");
        var modelPath = configuration["Tts:Kokoro:ModelPath"] ?? "";
        var runtimeModel = runtimeOptions.TtsModelId;
        if (!string.IsNullOrWhiteSpace(runtimeModel))
        {
            var trimmed = runtimeModel.Trim();
            if (trimmed.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
                modelPath = trimmed;
            else
                modelVariant = trimmed;
        }

        return new KokoroOptions
        {
            DefaultVoice = defaultVoice,
            ModelPath = modelPath,
            ModelVariant = modelVariant,
            ModelUrl = configuration["Tts:Kokoro:ModelUrl"] ?? "",
            CacheDirectory = configuration["Tts:Kokoro:CacheDirectory"] ?? "",
            VoicesPath = FirstNonEmpty(configuration["Tts:Kokoro:VoicesPath"], "voices"),
            AutoDownloadModel = ParseBool(configuration["Tts:Kokoro:AutoDownloadModel"], fallback: true),
            DefaultSpeed = ParseFloat(configuration["Tts:Kokoro:DefaultSpeed"], fallback: 1.0f)
        };
    }

    private static PiperOptions BuildPiperOptions(IConfiguration configuration, VoiceHostRuntimeOptions runtimeOptions)
    {
        var timeoutSeconds = ParseFloat(configuration["Tts:Piper:SynthesisTimeoutSeconds"], fallback: 30.0f);
        return new PiperOptions
        {
            BinaryPath = configuration["Tts:Piper:BinaryPath"],
            VoiceModelPath = FirstNonEmpty(configuration["Tts:Piper:VoiceModelPath"], runtimeOptions.TtsModelId),
            SynthesisTimeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }

        return "";
    }

    private static bool ParseBool(string? value, bool fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback
        };
    }

    private static float ParseFloat(string? value, float fallback)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
}
