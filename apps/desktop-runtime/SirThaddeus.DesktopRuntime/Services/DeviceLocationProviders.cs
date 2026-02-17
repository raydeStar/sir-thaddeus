using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Structured result for device-location provider calls.
/// </summary>
public sealed record DeviceLocationResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";

    public static DeviceLocationResult Disabled(string error) =>
        new()
        {
            Success = false,
            Error = string.IsNullOrWhiteSpace(error)
                ? "Device location disabled by policy"
                : error.Trim()
        };
}

public interface IDeviceLocationProvider
{
    Task<DeviceLocationResult> TryGetLocationAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Default provider: hard-disabled by policy.
/// </summary>
public sealed class NullDeviceLocationProvider(LocationPolicy policy) : IDeviceLocationProvider
{
    private readonly LocationPolicy _policy = policy ?? LocationPolicy.ManualOnly;

    public Task<DeviceLocationResult> TryGetLocationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var reason = _policy.AllowDeviceLocation
            ? "Device location provider unavailable in this build"
            : "Device location disabled by policy";

        return Task.FromResult(DeviceLocationResult.Disabled(reason));
    }
}

/// <summary>
/// Placeholder for future optional OS geolocation support.
/// Not registered in the current runtime.
/// </summary>
public sealed class WindowsDeviceLocationProvider : IDeviceLocationProvider
{
    public Task<DeviceLocationResult> TryGetLocationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DeviceLocationResult.Disabled("Device location disabled by policy"));
    }
}

/// <summary>
/// Central registration point for device-location providers.
/// </summary>
public static class DeviceLocationProviderRegistry
{
    public static IReadOnlyList<Type> RegisteredProviderTypes { get; } =
    [
        typeof(NullDeviceLocationProvider)
    ];

    public static IDeviceLocationProvider CreateDefault(LocationPolicy policy)
    {
        if (RegisteredProviderTypes.Contains(typeof(WindowsDeviceLocationProvider)))
        {
            throw new InvalidOperationException(
                "WindowsDeviceLocationProvider is not allowed in this build. " +
                "Device location must remain disabled by policy.");
        }

        return new NullDeviceLocationProvider(policy);
    }
}
