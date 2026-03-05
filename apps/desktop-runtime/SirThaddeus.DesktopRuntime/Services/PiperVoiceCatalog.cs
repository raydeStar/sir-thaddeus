using System.IO;
using System.Text.Json;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Enumerates available Piper TTS voices by scanning the local
/// <c>apps/voice-backend/piper-voices/</c> directory tree and merging
/// with the built-in catalog JSON for voices that can be downloaded.
///
/// Returns a sorted list of voice entries suitable for binding to
/// a settings dropdown.
/// </summary>
public static class PiperVoiceCatalog
{
    public record PiperVoiceEntry(
        string VoiceId,
        string DisplayName,
        string Gender,
        string Quality,
        bool IsInstalled);

    /// <summary>
    /// Well-known en_US Piper voices. Always included in the dropdown.
    /// </summary>
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

    /// <summary>
    /// Discovers installed Piper voice models and merges with well-known list.
    /// </summary>
    public static IReadOnlyList<PiperVoiceEntry> Discover(string? piperVoicesRootOverride = null)
    {
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in ResolvePiperVoicesRoots(piperVoicesRootOverride))
        {
            if (!Directory.Exists(root))
                continue;

            ScanDirectory(root, installedIds);
        }

        var result = new List<PiperVoiceEntry>(WellKnownVoices.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, name, gender) in WellKnownVoices)
        {
            if (seen.Add(id))
            {
                result.Add(new PiperVoiceEntry(
                    VoiceId: id,
                    DisplayName: name,
                    Gender: gender,
                    Quality: "medium",
                    IsInstalled: installedIds.Contains(id)));
            }
        }

        // Add any extra locally installed voices not in the well-known list
        foreach (var id in installedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Add(id))
            {
                result.Add(new PiperVoiceEntry(
                    VoiceId: id,
                    DisplayName: id,
                    Gender: "unknown",
                    Quality: "unknown",
                    IsInstalled: true));
            }
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
                    results.Add(dirName);
            }
        }
        catch
        {
            // Filesystem errors are non-fatal for discovery.
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

        // Published layout: piper-voices/ adjacent to exe or in bin/ support folder
        yield return Path.Combine(baseDir, "piper-voices");
        yield return Path.Combine(baseDir, "bin", "piper-voices");

        // Packaged release layout: bin/voice/piper-voices (from release-package.ps1)
        yield return Path.Combine(baseDir, "bin", "voice", "piper-voices");

        // Dev build layout: bin\Debug\net10.0-windows\win-x64 → up 6 to apps\
        var devRoot = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..",
            "voice-backend", "piper-voices"));
        yield return devRoot;

        // Repo-root relative: up 7 from win-x64 to repo root
        var repoRoot = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", "..", ".."));
        yield return Path.Combine(repoRoot, "apps", "voice-backend", "piper-voices");

        // Walk-up fallback: find apps/voice-backend/piper-voices from any depth
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
