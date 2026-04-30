namespace SirThaddeus.Agent.Routing;

/// <summary>
/// Footman router variant for latency-sensitive chat paths where an LLM
/// gatekeeper would contend with the primary model. It preserves the
/// deterministic fast paths and fails open when no high-confidence route is
/// available.
/// </summary>
public sealed class HeuristicFootmanRouter : IFootmanRouter
{
    private readonly Action<string, string>? _logEvent;

    public HeuristicFootmanRouter(Action<string, string>? logEvent = null)
    {
        _logEvent = logEvent;
    }

    public Task<RoutingDecision> RouteAsync(
        string userMessage,
        RoutingFeatures features,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestId = Guid.NewGuid().ToString("N")[..12];
        var deterministic = FastLlmFootmanRouter.TryDeterministicRoute(features, requestId);
        if (deterministic is not null)
        {
            _logEvent?.Invoke("FOOTMAN_HEURISTIC_DECISION",
                $"requestId={requestId} state={deterministic.NextState} reason={deterministic.ReasonCode}");
            return Task.FromResult(deterministic);
        }

        _logEvent?.Invoke("FOOTMAN_HEURISTIC_NO_MATCH",
            $"requestId={requestId} - falling back without LLM gatekeeper");

        return Task.FromResult(RoutingDecision.CreateFallback(requestId, "heuristic_no_match"));
    }
}