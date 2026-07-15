using System.Collections.Concurrent;
using SirThaddeus.Agent;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Holds exact model-visible tool results for the isolated harness process.
/// Callers must gate capture and access with harness mode; production turns do
/// not populate this store.
/// </summary>
public sealed class HarnessToolEvidenceStore
{
    private readonly ConcurrentDictionary<string, ToolCallRecord[]> _byMessageId =
        new(StringComparer.Ordinal);

    public void Append(
        string messageId,
        string toolName,
        string arguments,
        string result,
        bool success)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var call = new ToolCallRecord
        {
            ToolName = toolName,
            Arguments = arguments,
            Result = result,
            Success = success
        };
        _byMessageId.AddOrUpdate(
            messageId,
            _ => [call],
            (_, existing) => [.. existing, call]);
    }

    public IReadOnlyList<ToolCallRecord> Get(string messageId) =>
        _byMessageId.TryGetValue(messageId, out var toolCalls)
            ? toolCalls.Select(call => call with { }).ToArray()
            : [];

    public void Clear() => _byMessageId.Clear();
}
