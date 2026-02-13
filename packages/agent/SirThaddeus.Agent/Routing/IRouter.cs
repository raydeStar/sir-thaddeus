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
    Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default);
}

