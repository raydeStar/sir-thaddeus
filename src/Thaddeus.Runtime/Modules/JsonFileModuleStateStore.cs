using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Thaddeus.Runtime.Modules;

public sealed class JsonFileModuleStateStore : IModuleStateStore
{
    private readonly string _path;
    private readonly ILogger<JsonFileModuleStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModuleStateDocument? _cached;

    public JsonFileModuleStateStore(string filePath, ILogger<JsonFileModuleStateStore> logger)
    {
        _path = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string FilePath => _path;

    public async Task<ModuleStateDocument> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
                return _cached;

            _cached = await LoadAsync(ct).ConfigureAwait(false);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ModuleStateDocument> ReplaceAsync(ModuleStateDocument document, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var normalized = Normalize(document);
            await WriteAtomicAsync(normalized, ct).ConfigureAwait(false);
            _cached = normalized;
            return normalized;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ModuleStateDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path))
            return ModuleStateDocument.Empty;

        try
        {
            await using var stream = File.OpenRead(_path);
            var doc = await JsonSerializer.DeserializeAsync(
                    stream,
                    ModuleStateJsonContext.Default.ModuleStateDocument,
                    ct)
                .ConfigureAwait(false);
            return Normalize(doc ?? ModuleStateDocument.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "modules.state_load_failed path={Path}", _path);
            return ModuleStateDocument.Empty;
        }
    }

    private async Task WriteAtomicAsync(ModuleStateDocument document, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var temp = _path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    ModuleStateJsonContext.Default.ModuleStateDocument,
                    ct)
                .ConfigureAwait(false);
        }

        if (File.Exists(_path)) File.Replace(temp, _path, destinationBackupFileName: null);
        else File.Move(temp, _path);
    }

    private static ModuleStateDocument Normalize(ModuleStateDocument document)
    {
        var modules = new Dictionary<string, ModuleStateRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, state) in document.Modules)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            modules[id] = new ModuleStateRecord(
                state.ApprovalStatus,
                state.Disabled,
                string.IsNullOrWhiteSpace(state.LastError) ? null : state.LastError,
                state.LastStatusCheck,
                state.LastInvocation,
                state.RecentAuditEvents?.TakeLast(50).ToArray() ?? Array.Empty<ModuleAuditEventDto>());
        }

        return new ModuleStateDocument(modules);
    }
}
