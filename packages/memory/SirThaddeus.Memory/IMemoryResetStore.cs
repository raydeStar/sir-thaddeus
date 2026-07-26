namespace SirThaddeus.Memory;

/// <summary>
/// Optional destructive administration contract implemented by stores that can
/// permanently remove all durable memory rows. Kept separate from ordinary
/// memory CRUD so an accidental generic-store call cannot masquerade as reset.
/// </summary>
public interface IMemoryResetStore
{
    /// <returns>The number of durable rows permanently removed.</returns>
    Task<int> ResetAllAsync(CancellationToken ct = default);
}
