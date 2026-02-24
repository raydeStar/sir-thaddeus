using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.Core;

/// <summary>
/// Downloads, verifies, and caches binary assets from GitHub Releases.
/// Assets are defined in assets/manifest.json and extracted to a local cache.
/// </summary>
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

        var extractDir = Path.Combine(_repoRoot, asset.ExtractTo.Replace('/', Path.DirectorySeparatorChar));
        var markerPath = Path.Combine(extractDir, ".installed.marker");

        if (File.Exists(markerPath))
        {
            var markerContent = await File.ReadAllTextAsync(markerPath, ct);
            if (markerContent.Trim() == asset.Sha256)
            {
                _log?.Invoke($"Asset '{assetId}' already installed (sha256 matches).");
                return;
            }
        }

        var url = _manifest.BaseUrl + asset.Filename;
        var tempZip = Path.Combine(Path.GetTempPath(), $"st-asset-{assetId}-{Guid.NewGuid():N}.zip");

        try
        {
            _log?.Invoke($"Downloading {asset.Filename} ({asset.SizeBytes / (1024 * 1024)} MB) from {url} ...");
            await DownloadFileAsync(url, tempZip, asset.SizeBytes, ct);

            _log?.Invoke($"Verifying SHA-256 ...");
            var actualHash = await ComputeSha256Async(tempZip, ct);
            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA-256 mismatch for {asset.Filename}: expected {asset.Sha256}, got {actualHash}");
            }

            _log?.Invoke($"Extracting to {extractDir} ...");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            await File.WriteAllTextAsync(markerPath, asset.Sha256, ct);
            _log?.Invoke($"Asset '{assetId}' installed successfully.");
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
        return string.Equals(content, asset.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private async Task DownloadFileAsync(string url, string destPath, long expectedSize, CancellationToken ct)
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
                if (pct != lastPct && pct % 10 == 0)
                {
                    _log?.Invoke($"  {pct}% ({downloaded / (1024 * 1024)} MB / {totalBytes / (1024 * 1024)} MB)");
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
