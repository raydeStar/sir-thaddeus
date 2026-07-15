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

    public void Capture(string messageId, IReadOnlyList<ToolCallRecord> toolCalls)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentNullException.ThrowIfNull(toolCalls);
        _byMessageId[messageId] = toolCalls.Select(call => call with { }).ToArray();
    }

    public IReadOnlyList<ToolCallRecord> Get(string messageId) =>
        _byMessageId.TryGetValue(messageId, out var toolCalls)
            ? toolCalls.Select(call => call with { }).ToArray()
            : [];

    public void Clear() => _byMessageId.Clear();
}
