namespace SirThaddeus.Config;

/// <summary>
/// Runtime policy for location handling.
/// Device geolocation is intentionally disabled in this build.
/// </summary>
public sealed record LocationPolicy
{
    /// <summary>
    /// Hard policy: runtime must not request OS/device geolocation.
    /// </summary>
    public bool AllowDeviceLocation { get; init; } = false;

    /// <summary>
    /// Manual coarse location text (city/ZIP/country) is allowed.
    /// </summary>
    public bool AllowManualLocation { get; init; } = true;

    public static LocationPolicy ManualOnly { get; } = new();
}
