using System.IO;
using System.Text.Json;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Enumerates installed Kokoro voice packs by scanning the local
/// <c>apps/voice-backend/voices/</c> directory tree for folders
/// that contain a valid <c>manifest.json</c> with a <c>voiceId</c>.
///
/// Returns a sorted, deduplicated list of voice IDs suitable for
/// binding to a settings dropdown. No network calls, no backend
/// dependency — purely filesystem discovery.
/// </summary>
public static class KokoroVoiceCatalog
{
    /// <summary>
    /// Well-known Kokoro preset voice IDs. Always included in the
    /// dropdown so users can quickly select baseline presets even
    /// before installing additional local packs.
    /// </summary>
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

    /// <summary>
    /// Discovers installed Kokoro voice IDs from the local filesystem
    /// and merges them with the well-known base preset list.
    /// </summary>
    /// <param name="voicesRootOverride">
    /// Optional explicit path to the voices root directory (for testing).
    /// When null, resolves from <see cref="AppContext.BaseDirectory"/>.
    /// </param>
    public static IReadOnlyList<string> Discover(string? voicesRootOverride = null)
    {
        var installed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidateRoot in ResolveVoicesRoots(voicesRootOverride))
        {
            if (!Directory.Exists(candidateRoot))
                continue;

            ScanDirectory(candidateRoot, installed);
        }

        var merged = new List<string>(WellKnownVoices.Length + installed.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Keep base presets in a stable, curated order.
        foreach (var voiceId in WellKnownVoices)
        {
            if (seen.Add(voiceId))
                merged.Add(voiceId);
        }

        // Add any extra locally installed/custom voices after presets.
        foreach (var voiceId in installed)
        {
            if (seen.Add(voiceId))
                merged.Add(voiceId);
        }

        return merged.AsReadOnly();
    }

    // ─── Internals ──────────────────────────────────────────────────

    private static void ScanDirectory(string voicesRoot, SortedSet<string> results)
    {
        try
        {
            foreach (var voiceDir in Directory.GetDirectories(voicesRoot))
            {
                var manifestPath = Path.Combine(voiceDir, "manifest.json");
                if (!File.Exists(manifestPath))
                    continue;

                var voiceId = TryReadVoiceIdFromManifest(manifestPath)
                              ?? Path.GetFileName(voiceDir);

                if (!string.IsNullOrWhiteSpace(voiceId))
                    results.Add(voiceId.Trim());
            }
        }
        catch
        {
            // Filesystem errors are non-fatal for discovery.
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
            // Malformed manifests are silently skipped.
        }

        return null;
    }

    /// <summary>
    /// Builds a prioritized list of candidate directories where
    /// Kokoro voice packs might live. Supports both dev builds
    /// (relative to bin output) and published single-directory layouts.
    /// </summary>
    private static IEnumerable<string> ResolveVoicesRoots(string? explicitRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            yield return explicitRoot;
            yield break;
        }

        var baseDir = AppContext.BaseDirectory;

        // Published layout: voices/ folder adjacent to the executable,
        // or inside the bin/ support subfolder for cleanest layout.
        yield return Path.Combine(baseDir, "voices");
        yield return Path.Combine(baseDir, "bin", "voices");

        // Dev build layout:
        //   AppContext.BaseDirectory = apps/desktop-runtime/.../bin/Debug/net8.0-windows/
        //   5 parent jumps → apps/
        //   Then sibling into voice-backend/voices/
        var devRoot = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..",
            "voice-backend", "voices"));
        yield return devRoot;

        // Repo-root relative (6 parent jumps from bin output → repo root).
        var repoRoot = Path.GetFullPath(Path.Combine(
            baseDir, "..", "..", "..", "..", "..", ".."));
        yield return Path.Combine(repoRoot, "apps", "voice-backend", "voices");
    }
}
