using SirThaddeus.Agent;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Transparent harness-only MCP decorator that records the exact result before
/// later pipeline stages can replace or discard the final response trace.
/// </summary>
internal sealed class HarnessEvidenceMcpToolClient : IMcpToolClient
{
    private readonly IMcpToolClient _inner;
    private readonly HarnessToolEvidenceStore _store;
    private readonly string _messageId;

    public HarnessEvidenceMcpToolClient(
        IMcpToolClient inner,
        HarnessToolEvidenceStore store,
        string messageId)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _messageId = string.IsNullOrWhiteSpace(messageId)
            ? throw new ArgumentException("Message id is required.", nameof(messageId))
            : messageId;
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        _inner.ListToolsAsync(cancellationToken);

    public async Task<string> CallToolAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _inner.CallToolAsync(toolName, argumentsJson, cancellationToken)
                .ConfigureAwait(false);
            _store.Append(_messageId, toolName, argumentsJson, result, success: true);
            return result;
        }
        catch (Exception ex)
        {
            _store.Append(_messageId, toolName, argumentsJson, ex.Message, success: false);
            throw;
        }
    }
}
