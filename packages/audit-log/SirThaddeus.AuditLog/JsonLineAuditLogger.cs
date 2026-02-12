using System.Text;
using System.Text.Json;

namespace SirThaddeus.AuditLog;

/// <summary>
/// Audit logger that writes to a JSON Lines (.jsonl) file.
/// Thread-safe for concurrent appends.
/// </summary>
public sealed class JsonLineAuditLogger : IAuditLogger, IDisposable
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed;

    /// <summary>
    /// Creates a new JSON Lines audit logger.
    /// </summary>
    /// <param name="filePath">Path to the .jsonl file. Created if it doesn't exist.</param>
    public JsonLineAuditLogger(string filePath)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        
        // Ensure the directory exists
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    /// <summary>
    /// Gets the default audit log path under %LOCALAPPDATA%.
    /// </summary>
    public static string GetDefaultPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "SirThaddeus", "audit.jsonl");
    }

    /// <summary>
    /// Creates a logger using the default path.
    /// </summary>
    public static JsonLineAuditLogger CreateDefault() => new(GetDefaultPath());

    /// <inheritdoc />
    public void Append(AuditEvent auditEvent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(auditEvent);

        var json = JsonSerializer.Serialize(auditEvent, _jsonOptions);
        
        lock (_writeLock)
        {
            File.AppendAllText(_filePath, json + Environment.NewLine, Encoding.UTF8);
        }
    }

    /// <inheritdoc />
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(auditEvent);

        var json = JsonSerializer.Serialize(auditEvent, _jsonOptions);
        
        // Use a semaphore for async locking in a production scenario;
        // for V0, we'll just await the sync write on a background thread
        await Task.Run(() =>
        {
            lock (_writeLock)
            {
                File.AppendAllText(_filePath, json + Environment.NewLine, Encoding.UTF8);
            }
        }, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (maxEvents <= 0)
            return [];

        if (!File.Exists(_filePath))
            return [];

        // Open with FileShare.ReadWrite so UI reads never block concurrent appends.
        var lines = new List<string>();
        using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
                lines.Add(line);
        }

        var relevantLines = lines.TakeLast(maxEvents);

        var events = new List<AuditEvent>();
        foreach (var line in relevantLines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var evt = JsonSerializer.Deserialize<AuditEvent>(line, _jsonOptions);
                if (evt != null)
                    events.Add(evt);
            }
            catch (JsonException)
            {
                // Skip malformed lines; log corruption shouldn't crash the reader
            }
        }

        return events;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AuditEvent>> ReadTailAsync(
        int maxEvents, 
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        return await Task.Run(() => ReadTail(maxEvents), cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
