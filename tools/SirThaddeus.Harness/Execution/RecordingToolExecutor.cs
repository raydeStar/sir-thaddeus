using SirThaddeus.Agent;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

public sealed class RecordingToolExecutor : IToolExecutor
{
    private readonly IToolExecutor _inner;
    private readonly List<RecordedToolTurn> _recordedTurns = [];
    private readonly List<string> _availableTools = [];
    private int _index;

    public RecordingToolExecutor(IToolExecutor inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<RecordedToolTurn> RecordedTurns => _recordedTurns;
    public IReadOnlyList<string> AvailableTools => _availableTools.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var list = await _inner.ListToolsAsync(cancellationToken);
        _availableTools.AddRange(list.Select(tool => tool.Name));
        return list;
    }

    public async Task<ToolExecutionEnvelope> ExecuteAsync(
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ExecuteAsync(toolName, argumentsJson, cancellationToken);
        _recordedTurns.Add(new RecordedToolTurn
        {
            Index = Interlocked.Increment(ref _index) - 1,
            ToolName = toolName,
            ArgumentsJson = argumentsJson,
            ResultText = result.Success ? result.ResultText : result.Error?.Message ?? result.ResultText,
            Success = result.Success
        });
        _availableTools.Add(toolName);
        return result;
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
