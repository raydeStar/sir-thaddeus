namespace SirThaddeus.WebSearch;

/// <summary>
/// Fetches and parses RSS/Atom feeds over HTTP.
/// </summary>
public interface IFeedProvider
{
    Task<FeedSnapshot> FetchAsync(
        string url,
        int maxItems = 5,
        CancellationToken cancellationToken = default);
}
