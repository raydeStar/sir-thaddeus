using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

public sealed record SearchFallbackRequest
{
    public string UserMessage { get; init; } = "";
    public string MemoryPackText { get; init; } = "";
    public IList<ChatMessage> History { get; init; } = [];
    public IList<ToolCallRecord> ToolCallsMade { get; init; } = [];
    public int RoundTrips { get; init; }
    public Action<string, string>? LogEvent { get; init; }
}

public interface ISearchFallbackExecutor
{
    Task<AgentResponse> ExecuteAsync(
        SearchFallbackRequest request,
        CancellationToken cancellationToken = default);
}
