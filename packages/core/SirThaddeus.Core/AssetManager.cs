using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.Core;

/// <summary>
/// Structured progress report emitted during asset downloads.
/// </summary>
public sealed record AssetProgress(
    string AssetId,
    string Description,
    int AssetIndex,
    int TotalAssets,
    AssetProgressPhase Phase,
    int DownloadPercent = 0);

public enum AssetProgressPhase
{
    Checking,
    Downloading,
    Verifying,
    Extracting,
    Installed,
    AlreadyInstalled
}

public sealed class AssetManager
{
    private readonly AssetManifest _manifest;
    private readonly string _repoRoot;
    private readonly HttpClient _http;
    private readonly Action<string>? _log;

    public AssetManager(string repoRoot, Action<string>? log = null)
    {
        _repoRoot = repoRoot;
        _log = log;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("SirThaddeus-AssetManager/1.0");

        var manifestPath = Path.Combine(repoRoot, "assets", "manifest.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"Asset manifest not found: {manifestPath}");

        var json = File.ReadAllText(manifestPath);
        _manifest = JsonSerializer.Deserialize<AssetManifest>(json)
            ?? throw new InvalidOperationException("Failed to parse asset manifest.");
    }

    /// <summary>
    /// Ensures an asset is downloaded, verified, and extracted.
    /// Idempotent: if the asset is already installed and valid, this is a no-op.
    /// </summary>
    public async Task EnsureAssetAsync(string assetId, CancellationToken ct = default)
    {
        var asset = _manifest.Assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new ArgumentException($"Unknown asset: {assetId}");
        await EnsureAssetAsync(asset, 0, 1, null, ct);
    }

    private async Task EnsureAssetAsync(AssetEntry asset, int index, int total, IProgress<AssetProgress>? progress, CancellationToken ct)
    {
        var extractDir = Path.Combine(_repoRoot, asset.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        var markerPath = Path.Combine(extractDir, ".installed.marker");

        progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Checking));

        if (File.Exists(markerPath))
        {
            var markerContent = await File.ReadAllTextAsync(markerPath, ct);
            if (markerContent.Trim() == asset.Sha256 && HasInstalledPayload(extractDir))
            {
                _log?.Invoke($"Asset '{asset.Id}' already installed (sha256 matches).");
                progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.AlreadyInstalled));
                return;
            }
            else if (markerContent.Trim() == asset.Sha256)
            {
                _log?.Invoke($"Asset '{asset.Id}' marker is present but payload files are missing; reinstalling.");
            }
        }

        var url = _manifest.BaseUrl + asset.Filename;
        var tempZip = Path.Combine(Path.GetTempPath(), $"st-asset-{asset.Id}-{Guid.NewGuid():N}.zip");

        try
        {
            _log?.Invoke($"Downloading {asset.Filename} ({asset.SizeBytes / (1024 * 1024)} MB) from {url} ...");
            progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Downloading, 0));
            await DownloadFileAsync(url, tempZip, asset.SizeBytes, pct =>
            {
                progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Downloading, pct));
            }, ct);

            _log?.Invoke($"Verifying SHA-256 ...");
            progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Verifying));
            var actualHash = await ComputeSha256Async(tempZip, ct);
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {asset.Filename}: expected {asset.Sha256}, got {actualHash}");
            }

            _log?.Invoke($"Extracting to {extractDir} ...");
            progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Extracting));
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            await File.WriteAllTextAsync(markerPath, asset.Sha256, ct);
            _log?.Invoke($"Asset '{asset.Id}' installed successfully.");
            progress?.Report(new AssetProgress(asset.Id, asset.Description, index, total, AssetProgressPhase.Installed));
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Ensures all assets in the manifest are installed.
    /// </summary>
    public async Task EnsureAllAssetsAsync(CancellationToken ct = default)
    {
        foreach (var asset in _manifest.Assets)
        {
            await EnsureAssetAsync(asset.Id, ct);
        }
    }

    /// <summary>
    /// Ensures all assets are installed, reporting structured progress.
    /// </summary>
    public async Task EnsureAllAssetsAsync(IProgress<AssetProgress> progress, CancellationToken ct = default)
    {
        for (var i = 0; i < _manifest.Assets.Count; i++)
        {
            var asset = _manifest.Assets[i];
            await EnsureAssetAsync(asset, i, _manifest.Assets.Count, progress, ct);
        }
    }

    /// <summary>
    /// Returns true when every asset in the manifest is already installed.
    /// </summary>
    public bool AllAssetsInstalled()
    {
        return _manifest.Assets.All(a => IsInstalled(a.Id));
    }

    /// <summary>
    /// Returns the local extraction path for an asset.
    /// </summary>
    public string GetAssetPath(string assetId)
    {
        var asset = _manifest.Assets.FirstOrDefault(a => a.Id == assetId)
            ?? throw new ArgumentException($"Unknown asset: {assetId}");
        return Path.Combine(_repoRoot, asset.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Checks whether an asset is already installed and verified.
    /// </summary>
    public bool IsInstalled(string assetId)
    {
        var asset = _manifest.Assets.FirstOrDefault(a => a.Id == assetId);
        if (asset == null) return false;

        var extractDir = Path.Combine(_repoRoot, asset.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        var markerPath = Path.Combine(extractDir, ".installed.marker");
        if (!File.Exists(markerPath)) return false;

        var content = File.ReadAllText(markerPath).Trim();
        return string.Equals(content, asset.Sha256, StringComparison.OrdinalIgnoreCase) &&
               HasInstalledPayload(extractDir);
    }

    private static bool HasInstalledPayload(string extractDir)
    {
        if (!Directory.Exists(extractDir))
            return false;

        // A marker-only directory is a broken install (observed with interrupted
        // downloads/extracts). Require at least one additional file.
        return Directory.EnumerateFiles(extractDir, "*", SearchOption.AllDirectories)
            .Any(path => !string.Equals(Path.GetFileName(path), ".installed.marker", StringComparison.OrdinalIgnoreCase));
    }

    private async Task DownloadFileAsync(string url, string destPath, long expectedSize, Action<int>? onPercent, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int lastPct = -1;

        while (true)
        {
            var bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead == 0) break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;

            if (totalBytes > 0)
            {
                var pct = (int)(downloaded * 100 / totalBytes);
                if (pct != lastPct && pct % 2 == 0)
                {
                    if (pct % 10 == 0)
                        _log?.Invoke($"  {pct}% ({downloaded / (1024 * 1024)} MB / {totalBytes / (1024 * 1024)} MB)");
                    onPercent?.Invoke(pct);
                    lastPct = pct;
                }
            }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}

public sealed class AssetManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "";

    [JsonPropertyName("assets")]
    public List<AssetEntry> Assets { get; set; } = new();
}

public sealed class AssetEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("extractTo")]
    public string ExtractTo { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
