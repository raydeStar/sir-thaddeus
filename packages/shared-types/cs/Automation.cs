namespace Thaddeus.SharedTypes;

/// <summary>
/// Recurring / one-shot schedule for an <see cref="Automation"/>. Mirrors
/// the shape most cron-style UIs expose:
/// <list type="bullet">
///   <item><c>Kind = "off"</c> — disabled, runner ignores the automation.</item>
///   <item><c>Kind = "cron"</c> — standard 5-field cron in <c>Cron</c>; scheduler evaluates against <c>Timezone</c>.</item>
///   <item><c>Kind = "one-shot"</c> — runs once at <c>RunAt</c> (UTC), then auto-disables.</item>
/// </list>
/// <c>NextRunAt</c> and <c>LastFiredAt</c> are computed by the runtime and
/// echoed back to the UI; clients should not set them directly.
/// </summary>
public sealed record AutomationSchedule(
    string Kind,
    string? Cron,
    DateTimeOffset? RunAt,
    string? Timezone,
    DateTimeOffset? NextRunAt,
    DateTimeOffset? LastFiredAt);

/// <summary>
/// A simple user-defined automation (Phase 7.2). For v1 the only trigger is
/// "manual" (user clicks Run) — schedules and webhooks are deferred.
/// Steps are free-form prompts the agent will execute in order.
/// </summary>
/// <param name="Id">Stable identifier, prefixed <c>auto_</c>.</param>
/// <param name="Name">Short display name.</param>
/// <param name="Description">Optional long description.</param>
/// <param name="Trigger">For v1: always <c>manual</c>.</param>
/// <param name="Steps">Ordered list of prompts.</param>
/// <param name="Enabled">If false the Run button is disabled.</param>
/// <param name="CreatedAt">When the automation was first created.</param>
/// <param name="UpdatedAt">When the automation was last modified.</param>
/// <param name="LastRunAt">When the automation was last executed (null = never).</param>
/// <param name="AllowedTools">
/// Explicit list of MCP tool names that this automation can call without a
/// permission prompt. Empty or null means "defer to the global permission
/// policy" (same behavior as before allowlists existed). Anything the model
/// tries that isn't on this list still triggers the normal modal — so
/// silent tool use is impossible.
/// </param>
public sealed record Automation(
    string Id,
    string Name,
    string Description,
    string Trigger,
    IReadOnlyList<string> Steps,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt,
    IReadOnlyList<string>? AllowedTools = null,
    AutomationSchedule? Schedule = null);
