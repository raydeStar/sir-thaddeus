namespace Thaddeus.Runtime.Voice;

/// <summary>
/// Curated list of well-known Piper voices, merged with whatever is actually
/// installed on disk under the piper-voices root. Voices not on disk are tagged
/// with a download hint so the UI can mark them as such.
/// </summary>
public static class PiperVoiceCatalog
{
    private static readonly (string Id, string DisplayName, string Gender)[] WellKnown =
    {
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
    };

    public static IReadOnlyList<PiperVoiceEntry> Discover(string? rootOverride = null)
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in ResolveRoots(rootOverride))
        {
            if (!Directory.Exists(root)) continue;
            Scan(root, installed);
        }

        var result = new List<PiperVoiceEntry>(WellKnown.Length + installed.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name, gender) in WellKnown)
        {
            if (!seen.Add(id)) continue;
            result.Add(new PiperVoiceEntry(
                VoiceId: id,
                DisplayName: name,
                Gender: gender,
                Quality: "medium",
                IsInstalled: installed.Contains(id)));
        }

        foreach (var id in installed.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(id)) continue;
            result.Add(new PiperVoiceEntry(
                VoiceId: id,
                DisplayName: id,
                Gender: "unknown",
                Quality: "unknown",
                IsInstalled: true));
        }

        return result;
    }

    private static void Scan(string root, HashSet<string> installed)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root))
            {
                var name = Path.GetFileName(dir);
                var onnx = Path.Combine(dir, $"{name}.onnx");
                var cfg = Path.Combine(dir, $"{name}.onnx.json");
                if (File.Exists(onnx) && File.Exists(cfg)) installed.Add(name);
            }
        }
        catch { /* best-effort discovery */ }
    }

    private static IEnumerable<string> ResolveRoots(string? explicitRoot)
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

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "apps", "voice-backend", "piper-voices");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                yield break;
            }
        }
    }
}

/// <summary>Single Piper voice entry.</summary>
public sealed record PiperVoiceEntry(
    string VoiceId,
    string DisplayName,
    string Gender,
    string Quality,
    bool IsInstalled);
