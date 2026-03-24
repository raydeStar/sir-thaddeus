using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Input envelope for routing. Intentionally capability-oriented and tool-name free.
/// </summary>
public sealed record RouterRequest
{
    public required string UserMessage { get; init; }
    public bool HasRecentFirstPrinciplesRationale { get; init; }
    public bool HasRecentSearchResults { get; init; }
}

/// <summary>
/// Routes user messages to structured intent requirements.
/// </summary>
public interface IRouter
{
    /// <summary>
    /// Classifies the user's message and returns structured intent requirements
    /// that downstream stages use to select an execution strategy.
    /// </summary>
    /// <param name="request">Routing input containing the user message and conversation state hints.</param>
    /// <param name="cancellationToken">Token to cancel the classification.</param>
    /// <returns>A routing decision with intent, confidence, and capability flags.</returns>
    Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default);
}

