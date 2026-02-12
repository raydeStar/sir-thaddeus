using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

/// <summary>
/// Executes web-search fallback when chat-only output is unusable.
/// </summary>
public sealed class SearchFallbackExecutor : ISearchFallbackExecutor
{
    private readonly SearchOrchestrator _searchOrchestrator;

    public SearchFallbackExecutor(SearchOrchestrator searchOrchestrator)
    {
        _searchOrchestrator = searchOrchestrator ?? throw new ArgumentNullException(nameof(searchOrchestrator));
    }

    public async Task<AgentResponse> ExecuteAsync(
        SearchFallbackRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var history = request.History as List<ChatMessage> ?? request.History.ToList();
            var toolCallsMade = request.ToolCallsMade as List<ToolCallRecord> ?? request.ToolCallsMade.ToList();

            var response = await _searchOrchestrator.ExecuteAsync(
                request.UserMessage,
                request.MemoryPackText,
                history,
                toolCallsMade,
                cancellationToken);

            if (response.Success)
                history.Add(ChatMessage.Assistant(response.Text));

            return response with { ToolCallsMade = toolCallsMade };
        }
        catch (Exception ex)
        {
            request.LogEvent?.Invoke("FALLBACK_SEARCH_FAIL", ex.Message);
            var fallbackMsg = "I wasn't able to generate a clean answer for that. Could you try asking a different way?";

            if (request.History is List<ChatMessage> listHistory)
                listHistory.Add(ChatMessage.Assistant(fallbackMsg));

            return new AgentResponse
            {
                Text = fallbackMsg,
                Success = false,
                Error = ex.Message,
                ToolCallsMade = request.ToolCallsMade.ToList(),
                LlmRoundTrips = request.RoundTrips
            };
        }
    }
}
