using SirThaddeus.Core;

namespace SirThaddeus.Tests;

public sealed class AssetManagerTests
{
    [Fact]
    public void IsInstalled_MarkerOnlyDirectory_ReturnsFalse()
    {
        var repoRoot = CreateTempRepoRoot();
        try
        {
            WriteManifest(repoRoot);
            var extractDir = Path.Combine(repoRoot, "apps", "voice-backend", "stt-models", "base");
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, ".installed.marker"), "abc123");

            var manager = new AssetManager(repoRoot);

            Assert.False(manager.IsInstalled("stt-model-whisper-base"));
        }
        finally
        {
            TryDeleteDirectory(repoRoot);
        }
    }

    [Fact]
    public void IsInstalled_MarkerAndPayload_ReturnsTrue()
    {
        var repoRoot = CreateTempRepoRoot();
        try
        {
            WriteManifest(repoRoot);
            var extractDir = Path.Combine(repoRoot, "apps", "voice-backend", "stt-models", "base");
            Directory.CreateDirectory(extractDir);
            File.WriteAllText(Path.Combine(extractDir, ".installed.marker"), "abc123");
            File.WriteAllText(Path.Combine(extractDir, "model.bin"), "payload");

            var manager = new AssetManager(repoRoot);

            Assert.True(manager.IsInstalled("stt-model-whisper-base"));
        }
        finally
        {
            TryDeleteDirectory(repoRoot);
        }
    }

    private static void WriteManifest(string repoRoot)
    {
        var assetsDir = Path.Combine(repoRoot, "assets");
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(
            Path.Combine(assetsDir, "manifest.json"),
            """
            {
              "version": "1",
              "baseUrl": "https://example.invalid/",
              "assets": [
                {
                  "id": "stt-model-whisper-base",
                  "filename": "stt-model-whisper-base.zip",
                  "sha256": "abc123",
                  "sizeBytes": 1,
                  "extractTo": "apps/voice-backend/stt-models/base",
                  "description": "test"
                }
              ]
            }
            """);
    }

    private static string CreateTempRepoRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "st-asset-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
