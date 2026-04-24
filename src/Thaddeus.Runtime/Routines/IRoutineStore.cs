using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Routines;

/// <summary>
/// Local-first persistence surface for <see cref="Routine"/>s and their
/// <see cref="RoutineRun"/>s. Implementations are responsible for keeping
/// writes durable and concurrency-safe; the API layer just translates HTTP
/// shapes.
///
/// No method on this interface executes a routine — routines are UI-driven
/// checklists, not background jobs. The store only reads/writes the
/// definitions and the run records.
/// </summary>
public interface IRoutineStore
{
    /// <summary>All routines, newest-updated first.</summary>
    Task<IReadOnlyList<Routine>> ListRoutinesAsync(CancellationToken ct);

    /// <summary>Fetch a routine by id, or null if missing.</summary>
    Task<Routine?> GetRoutineAsync(string id, CancellationToken ct);

    /// <summary>Create a new routine. Id, timestamps, and item ids are assigned by the store.</summary>
    Task<Routine> CreateRoutineAsync(
        string name,
        string description,
        IReadOnlyList<RoutineChecklistItem> checklistItems,
        string? promptTemplate,
        bool enabled,
        CancellationToken ct);

    /// <summary>
    /// Upsert an existing routine from a seed. If a routine with the same
    /// <paramref name="seed"/> id already exists on disk, this is a no-op —
    /// user edits survive redeployment. Returns the persisted routine.
    /// </summary>
    Task<Routine> SeedRoutineAsync(Routine seed, CancellationToken ct);

    /// <summary>
    /// Patch-style update. Null fields are left unchanged. <c>checklistItems</c>,
    /// when non-null, fully replaces the item list (caller is responsible for
    /// preserving ids of items it wants to keep).
    /// </summary>
    Task<Routine?> UpdateRoutineAsync(
        string id,
        string? name,
        string? description,
        IReadOnlyList<RoutineChecklistItem>? checklistItems,
        string? promptTemplate,
        bool? enabled,
        CancellationToken ct);

    /// <summary>Delete the routine and every run that referenced it.</summary>
    Task<bool> DeleteRoutineAsync(string id, CancellationToken ct);

    /// <summary>
    /// All runs for a routine, newest first. Returns an empty list when the
    /// routine has never been run (or doesn't exist — the API layer checks
    /// routine existence separately).
    /// </summary>
    Task<IReadOnlyList<RoutineRun>> ListRunsAsync(string routineId, CancellationToken ct);

    /// <summary>Fetch a single run by id, or null if missing.</summary>
    Task<RoutineRun?> GetRunAsync(string runId, CancellationToken ct);

    /// <summary>
    /// Start a new run for <paramref name="routineId"/>. The store snapshots
    /// the routine's current checklist items (freezing their text) so later
    /// routine edits don't rewrite history. Updates <c>Routine.LastRunAt</c>.
    /// </summary>
    Task<RoutineRun?> StartRunAsync(string routineId, CancellationToken ct);

    /// <summary>
    /// Patch a run in place: toggle checklist items, update the note, or
    /// both. <paramref name="itemUpdates"/>, when non-null, is a set of
    /// (checklistItemId → isCompleted) pairs applied atomically.
    /// </summary>
    Task<RoutineRun?> UpdateRunAsync(
        string runId,
        IReadOnlyDictionary<string, bool>? itemUpdates,
        string? userNote,
        CancellationToken ct);

    /// <summary>
    /// Mark the run complete. Stamps <c>CompletedAt</c> and records the final
    /// note (when supplied). Idempotent — completing an already-complete run
    /// returns the existing record unchanged.
    /// </summary>
    Task<RoutineRun?> CompleteRunAsync(string runId, string? userNote, CancellationToken ct);

    /// <summary>
    /// Discard a run without sealing it. Used when the user opens a routine
    /// but leaves without completing it; the API layer decides whether to
    /// call this based on whether any item was actually checked.
    /// </summary>
    Task<bool> DiscardRunAsync(string runId, CancellationToken ct);
}
