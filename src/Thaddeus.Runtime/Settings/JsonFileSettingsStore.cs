using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Settings;

/// <summary>
/// File-backed settings store. The whole document lives in a single
/// JSON file; mutations are serialized through a private semaphore and
/// written via a temp-file-then-replace pattern to prevent torn writes.
/// </summary>
public sealed class JsonFileSettingsStore : ISettingsStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SettingsDocument? _cached;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonFileSettingsStore(string filePath, ILogger<JsonFileSettingsStore> logger)
    {
        _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Test seam — returns the file path settings are persisted to.</summary>
    public string FilePath => _path;

    public event Action<SettingsDocument>? Changed;

    public async Task<SettingsDocument> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            _cached = await LoadFromDiskAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally { _gate.Release(); }
    }

    public async Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAtomicAsync(document, ct).ConfigureAwait(false);
            _cached = document;
        }
        finally { _gate.Release(); }

        // Raise the event after lock release so handlers do not run under the gate.
        Changed?.Invoke(document);
        _logger.LogInformation("settings.replaced provider={Provider} model={Model}",
            document.Llm.Provider, document.Llm.ModelId);
        return document;
    }

    private async Task<SettingsDocument> LoadFromDiskAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
        {
            _logger.LogInformation("settings.defaults_used path={Path}", _path);
            return SettingsDocument.Defaults();
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var doc = await JsonSerializer
                .DeserializeAsync<SettingsDocument>(stream, s_jsonOptions, ct)
                .ConfigureAwait(false);
            return doc ?? SettingsDocument.Defaults();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "settings.load_failed path={Path} returning_defaults=true", _path);
            return SettingsDocument.Defaults();
        }
    }

    private async Task WriteAtomicAsync(SettingsDocument document, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, document, s_jsonOptions, ct)
                .ConfigureAwait(false);
        }

        if (File.Exists(_path)) File.Replace(tempPath, _path, destinationBackupFileName: null);
        else File.Move(tempPath, _path);
    }
}
