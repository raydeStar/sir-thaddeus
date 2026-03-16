using System.IO;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

internal static class KokoroVoiceCatalog
{
    private static readonly string[] WellKnownVoices =
    [
        "af_heart",
        "af_sky",
        "af_nicole",
        "af_sarah",
        "af_bella",
        "am_adam",
        "am_michael",
        "bf_emma",
        "bf_isabella",
        "bm_george",
        "bm_lewis"
    ];

    public static IReadOnlyList<string> Discover(string? voicesRootOverride = null)
    {
        var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidateRoot in ResolveVoicesRoots(voicesRootOverride))
        {
            if (!Directory.Exists(candidateRoot))
            {
                continue;
            }

            ScanDirectory(candidateRoot, installed);
        }

        var merged = new List<string>(WellKnownVoices.Length + installed.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var voiceId in WellKnownVoices)
        {
            if (seen.Add(voiceId))
            {
                merged.Add(voiceId);
            }
        }

        foreach (var voiceId in installed)
        {
            if (seen.Add(voiceId))
            {
                merged.Add(voiceId);
            }
        }

        return merged.AsReadOnly();
    }

    private static void ScanDirectory(string voicesRoot, SortedSet<string> results)
    {
        try
        {
            foreach (var voiceDir in Directory.GetDirectories(voicesRoot))
            {
                var manifestPath = Path.Combine(voiceDir, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    continue;
                }

                var voiceId = TryReadVoiceIdFromManifest(manifestPath) ?? Path.GetFileName(voiceDir);
                if (!string.IsNullOrWhiteSpace(voiceId))
                {
                    results.Add(voiceId.Trim());
                }
            }
        }
        catch
        {
            // Discovery should stay resilient to local filesystem issues.
        }
    }

    private static string? TryReadVoiceIdFromManifest(string manifestPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("voiceId", out var voiceIdElement) &&
                voiceIdElement.ValueKind == JsonValueKind.String)
            {
                return voiceIdElement.GetString();
            }
        }
        catch
        {
            // Ignore malformed or partial manifests.
        }

        return null;
    }

    private static IEnumerable<string> ResolveVoicesRoots(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            yield return explicitRoot;
            yield break;
        }

        var baseDir = AppContext.BaseDirectory;

        yield return Path.Combine(baseDir, "voices");
        yield return Path.Combine(baseDir, "bin", "voices");
        yield return Path.Combine(baseDir, "bin", "voice", "voices");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "voice-backend", "voices"));

        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", ".."));
        yield return Path.Combine(repoRoot, "apps", "voice-backend", "voices");

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && dir?.Parent is not null; i++)
        {
            dir = dir.Parent;
            var candidate = Path.Combine(dir.FullName, "apps", "voice-backend", "voices");
            if (Directory.Exists(candidate))
            {
                yield return candidate;
                break;
            }
        }
    }
}
