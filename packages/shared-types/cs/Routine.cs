namespace Thaddeus.SharedTypes;

/// <summary>
/// A repeatable user-invoked workflow — a checklist of steps Sir Thaddeus walks
/// the user through. Unlike the previous automation feature, routines never
/// execute on their own: the user opens one, checks items off, optionally
/// records a note, and completes it. No schedules, no background fire, no
/// autonomous side effects.
/// </summary>
/// <param name="Id">Stable identifier, prefixed <c>rt_</c>.</param>
/// <param name="Name">Short display name.</param>
/// <param name="Description">Optional long description shown under the name.</param>
/// <param name="ChecklistItems">Ordered list of checkable items (may be empty for a freeform journal-style routine).</param>
/// <param name="PromptTemplate">Optional prompt the user can pick up and hand to the assistant for an AI-assisted pass.</param>
/// <param name="Enabled">When false, the routine is hidden from the primary list (kept in history).</param>
/// <param name="CreatedAt">When the routine was first saved.</param>
/// <param name="UpdatedAt">When the routine was last modified.</param>
/// <param name="LastRunAt">Convenience timestamp for the card subtitle; null when never run.</param>
public sealed record Routine(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<RoutineChecklistItem> ChecklistItems,
    string? PromptTemplate,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt);

/// <summary>One item in a <see cref="Routine"/>'s checklist template.</summary>
/// <param name="Id">Stable identifier within the routine; preserved when the routine is edited so historical runs can still cross-reference.</param>
/// <param name="Text">The user-visible label.</param>
/// <param name="SortOrder">0-based ordering. Ties resolve by insertion order (stable).</param>
public sealed record RoutineChecklistItem(
    string Id,
    string Text,
    int SortOrder);

/// <summary>
/// One recorded invocation of a <see cref="Routine"/>. Opened when the user
/// starts the run, updated as checkboxes flip, and sealed by <c>CompletedAt</c>
/// when the user hits Complete (or closes without completing — the runner
/// decides whether to persist an abandoned run based on whether anything was
/// actually changed).
/// </summary>
/// <param name="Id">Stable identifier, prefixed <c>rr_</c>.</param>
/// <param name="RoutineId">Parent routine id.</param>
/// <param name="StartedAt">UTC start of the run.</param>
/// <param name="CompletedAt">UTC completion timestamp; null while the run is still open.</param>
/// <param name="Items">Per-run snapshot of each checklist item and its completion state.</param>
/// <param name="UserNote">Free-form note the user typed during the run.</param>
/// <param name="GeneratedSummary">Reserved for a future opt-in AI summary; always null in MVP.</param>
public sealed record RoutineRun(
    string Id,
    string RoutineId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<RoutineRunItem> Items,
    string? UserNote,
    string? GeneratedSummary);

/// <summary>Per-run snapshot of one checklist item.</summary>
/// <param name="ChecklistItemId">References <see cref="RoutineChecklistItem.Id"/> in the parent routine.</param>
/// <param name="Text">Frozen copy of the item text at run start (surviving later routine edits).</param>
/// <param name="IsCompleted">Whether the user checked this item off during the run.</param>
/// <param name="CompletedAt">UTC timestamp the item was completed; null while unchecked.</param>
public sealed record RoutineRunItem(
    string ChecklistItemId,
    string Text,
    bool IsCompleted,
    DateTimeOffset? CompletedAt);
