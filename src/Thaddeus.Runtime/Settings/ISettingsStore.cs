using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Settings;

/// <summary>
/// Persists the v2 hybrid runtime's settings document. The store is
/// expected to be safe to call from multiple threads.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Read the current settings, applying defaults if no file exists yet.</summary>
    Task<SettingsDocument> GetAsync(CancellationToken ct);

    /// <summary>Replace the entire settings document atomically.</summary>
    Task<SettingsDocument> ReplaceAsync(SettingsDocument document, CancellationToken ct);

    /// <summary>Raised after a successful Replace. Subscribers must be cheap.</summary>
    event Action<SettingsDocument>? Changed;
}
