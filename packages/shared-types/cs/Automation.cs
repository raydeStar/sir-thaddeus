namespace Thaddeus.SharedTypes;

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
public sealed record Automation(
    string Id,
    string Name,
    string Description,
    string Trigger,
    IReadOnlyList<string> Steps,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastRunAt);
