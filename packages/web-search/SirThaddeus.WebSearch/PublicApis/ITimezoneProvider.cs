namespace SirThaddeus.WebSearch;

/// <summary>
/// Resolves timezone identifiers from coordinates.
/// </summary>
public interface ITimezoneProvider
{
    Task<TimezoneResolution> ResolveAsync(
        double latitude,
        double longitude,
        string? countryCode = null,
        CancellationToken cancellationToken = default);
}
