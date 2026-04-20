using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Automations;

/// <summary>Persists user-defined automations.</summary>
public interface IAutomationStore
{
    Task<IReadOnlyList<Automation>> ListAsync(CancellationToken ct);
    Task<Automation?> GetAsync(string id, CancellationToken ct);
    Task<Automation> CreateAsync(string name, string description, IReadOnlyList<string> steps, bool enabled, CancellationToken ct);
    Task<Automation?> UpdateAsync(
        string id,
        string? name,
        string? description,
        IReadOnlyList<string>? steps,
        bool? enabled,
        CancellationToken ct);
    /// <summary>Stamps <see cref="Automation.LastRunAt"/> to now.</summary>
    Task<Automation?> RecordRunAsync(string id, CancellationToken ct);
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
