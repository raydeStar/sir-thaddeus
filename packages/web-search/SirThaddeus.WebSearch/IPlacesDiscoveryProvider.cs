namespace SirThaddeus.WebSearch;

public interface IPlacesDiscoveryProvider
{
    string Name { get; }

    Task<PlacesDiscoveryResult> DiscoverAsync(
        string query,
        string? userLocationHint,
        PlaceDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}