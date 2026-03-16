using System.IO;

namespace SirThaddeus.UI.Avalonia;

internal static class PiperVoiceCatalog
{
    internal sealed record PiperVoiceEntry(
        string VoiceId,
        string DisplayName,
        string Gender,
        string Quality,
        bool IsInstalled);

    private static readonly (string Id, string Name, string Gender)[] WellKnownVoices =
    [
        ("en_US-amy-medium", "Amy", "female"),
        ("en_US-arctic-medium", "Arctic", "male"),
        ("en_US-bryce-medium", "Bryce", "male"),
        ("en_US-hfc_female-medium", "HFC Female", "female"),
        ("en_US-hfc_male-medium", "HFC Male", "male"),
        ("en_US-joe-medium", "Joe", "male"),
        ("en_US-john-medium", "John", "male"),
        ("en_US-kristin-medium", "Kristin", "female"),
        ("en_US-kusal-medium", "Kusal", "male"),
        ("en_US-l2arctic-medium", "L2 Arctic", "male"),
        ("en_US-lessac-medium", "Lessac", "female"),
        ("en_US-libritts_r-medium", "LibriTTS-R", "neutral"),
        ("en_US-ljspeech-medium", "LJ Speech", "female"),
        ("en_US-norman-medium", "Norman", "male"),
        ("en_US-ryan-medium", "Ryan", "male"),
    ];

    public static IReadOnlyList<PiperVoiceEntry> Discover(string? piperVoicesRootOverride = null)
    {
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ResolvePiperVoicesRoots(piperVoicesRootOverride))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            ScanDirectory(root, installedIds);
        }

        var result = new List<PiperVoiceEntry>(WellKnownVoices.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name, gender) in WellKnownVoices)
        {
            if (!seen.Add(id))
            {
                continue;
            }

            result.Add(new PiperVoiceEntry(
                VoiceId: id,
                DisplayName: name,
                Gender: gender,
                Quality: "medium",
                IsInstalled: installedIds.Contains(id)));
        }

        foreach (var id in installedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(id))
            {
                continue;
            }

            result.Add(new PiperVoiceEntry(
                VoiceId: id,
                DisplayName: id,
                Gender: "unknown",
                Quality: "unknown",
                IsInstalled: true));
        }

        return result.AsReadOnly();
    }

    private static void ScanDirectory(string piperVoicesRoot, HashSet<string> results)
    {
        try
        {
            foreach (var voiceDir in Directory.GetDirectories(piperVoicesRoot))
            {
                var dirName = Path.GetFileName(voiceDir);
                var onnxPath = Path.Combine(voiceDir, $"{dirName}.onnx");
                var configPath = Path.Combine(voiceDir, $"{dirName}.onnx.json");

                if (File.Exists(onnxPath) && File.Exists(configPath))
                {
                    results.Add(dirName);
                }
            }
        }
        catch
        {
            // Discovery should stay resilient to local filesystem issues.
        }
    }

    private static IEnumerable<string> ResolvePiperVoicesRoots(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            yield return explicitRoot;
            yield break;
        }

        var baseDir = AppContext.BaseDirectory;

        yield return Path.Combine(baseDir, "piper-voices");
        yield return Path.Combine(baseDir, "bin", "piper-voices");
        yield return Path.Combine(baseDir, "bin", "voice", "piper-voices");

        yield return Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..",
            "voice-backend", "piper-voices"));

        var repoRoot = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", ".."));
        yield return Path.Combine(repoRoot, "apps", "voice-backend", "piper-voices");

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "apps", "voice-backend", "piper-voices");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
        }
    }
}
