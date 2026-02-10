namespace SirThaddeus.WebSearch;

/// <summary>
/// Reads public holiday data from keyless public endpoints.
/// </summary>
public interface IHolidaysProvider
{
    Task<HolidaySetResult> GetHolidaysAsync(
        string countryCode,
        int year,
        string? regionCode = null,
        int maxItems = 50,
        CancellationToken cancellationToken = default);

    Task<HolidayTodayResult> IsTodayPublicHolidayAsync(
        string countryCode,
        string? regionCode = null,
        CancellationToken cancellationToken = default);

    Task<HolidayNextResult> GetNextPublicHolidaysAsync(
        string countryCode,
        string? regionCode = null,
        int maxItems = 8,
        CancellationToken cancellationToken = default);
}
