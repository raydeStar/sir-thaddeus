using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Memory;

/// <summary>
/// Persists user-curated memos. Implementations must be safe for concurrent
/// use from multiple HTTP requests.
/// </summary>
public interface IMemoStore
{
    Task<IReadOnlyList<Memo>> ListAsync(CancellationToken ct);
    Task<Memo?> GetAsync(string id, CancellationToken ct);
    Task<Memo> CreateAsync(string title, string body, IReadOnlyList<string>? tags, bool pinned, CancellationToken ct);
    /// <summary>Partial update; null arguments leave the field unchanged.</summary>
    Task<Memo?> UpdateAsync(
        string id,
        string? title,
        string? body,
        IReadOnlyList<string>? tags,
        bool? pinned,
        CancellationToken ct);
    Task<bool> DeleteAsync(string id, CancellationToken ct);
}
