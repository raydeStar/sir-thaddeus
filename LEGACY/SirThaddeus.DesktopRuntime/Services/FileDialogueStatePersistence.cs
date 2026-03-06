using System.IO;
using System.Text.Json;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.Services;

public sealed class FileDialogueStatePersistence : IDialogueStatePersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly IAuditLogger _audit;

    public FileDialogueStatePersistence(string filePath, IAuditLogger audit)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<DialogueState?> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<DialogueState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "DIALOGUE_STATE_LOAD_FAILED",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            });
            return null;
        }
    }

    public async Task SaveAsync(DialogueState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(state, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken);
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "DIALOGUE_STATE_SAVE_FAILED",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            });
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = "DIALOGUE_STATE_CLEAR_FAILED",
                Result = "error",
                Details = new Dictionary<string, object>
                {
                    ["error"] = ex.Message
                }
            });
        }

        return Task.CompletedTask;
    }
}
