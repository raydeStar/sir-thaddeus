namespace SirThaddeus.Agent.Memory;

public interface ISelfMemorySummarizer
{
    bool IsSelfMemoryKnowledgeRequest(string message);

    bool IsPersonalizedUsingKnownSelfContextRequest(
        string message,
        bool hasLoadedProfileContext);

    Task<AgentResponse> BuildSummaryResponseAsync(
        string? activeProfileId,
        IList<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken = default);

    Task<string> BuildContextBlockAsync(
        string? activeProfileId,
        IList<ToolCallRecord> toolCallsMade,
        Action<string, string>? logEvent = null,
        CancellationToken cancellationToken = default);
}
