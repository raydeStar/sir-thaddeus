using Microsoft.Extensions.Hosting;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Routines;

/// <summary>
/// Populates the routine store with the five MVP default routines on first
/// boot. Uses deterministic routine ids so a second run sees the existing
/// records and leaves them alone — user edits always win. If the user has
/// deleted a default, the seeder restores it only when the routine file is
/// gone; it does not resurrect a defaulted-then-edited definition.
/// </summary>
public sealed class RoutineSeeder : IHostedService
{
    private readonly IRoutineStore _store;
    private readonly ILogger<RoutineSeeder> _logger;

    public RoutineSeeder(IRoutineStore store, ILogger<RoutineSeeder> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "routine.seed_failed");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    internal async Task SeedAsync(CancellationToken ct)
    {
        var seeds = BuildDefaults(DateTimeOffset.UtcNow);
        var seeded = 0;
        foreach (var seed in seeds)
        {
            var existing = await _store.GetRoutineAsync(seed.Id, ct).ConfigureAwait(false);
            if (existing is not null) continue;
            await _store.SeedRoutineAsync(seed, ct).ConfigureAwait(false);
            seeded++;
        }
        if (seeded > 0)
            _logger.LogInformation("routine.seeded count={Count}", seeded);
    }

    internal static IReadOnlyList<Routine> BuildDefaults(DateTimeOffset now)
    {
        return new[]
        {
            BuildRoutine(
                id: "rt_default_morning_launch",
                name: "Morning Launch",
                description: "Start the day with clarity and choose the 3 most important priorities.",
                items: new[]
                {
                    "Review yesterday's shutdown note",
                    "Review current top goal",
                    "Pick today's 3 priorities",
                    "Choose the first task",
                    "Identify one likely distraction or risk",
                    "Commit to the first action",
                },
                promptTemplate:
                    "Help me create a focused plan for today. Use my current goals, recent notes, " +
                    "and yesterday's shutdown note if available. Keep the plan limited to the 3 " +
                    "most important priorities.",
                now: now),

            BuildRoutine(
                id: "rt_default_evening_shutdown",
                name: "Evening Shutdown",
                description: "Close the day honestly and carry momentum into tomorrow.",
                items: new[]
                {
                    "Record what got done",
                    "Record what slipped",
                    "Capture blockers",
                    "Pick tomorrow's first move",
                    "Save one lesson from today",
                },
                promptTemplate:
                    "Help me review the day honestly. Summarize what moved forward, what slipped, " +
                    "and what tomorrow's first sensible action should be.",
                now: now),

            BuildRoutine(
                id: "rt_default_fitness_checkin",
                name: "Fitness Check-In",
                description: "Stay accountable to training and nutrition without theatrics.",
                items: new[]
                {
                    "Log today's body weight if available",
                    "Log workout or rest day",
                    "Estimate calories so far",
                    "Estimate protein so far",
                    "Note hunger/energy level",
                    "Decide the next meal or recovery action",
                },
                promptTemplate:
                    "Help me assess today's fitness and nutrition progress. Keep it practical. " +
                    "Focus on calories, protein, training, recovery, and the next best action.",
                now: now),

            BuildRoutine(
                id: "rt_default_project_focus",
                name: "Project Focus",
                description: "Pick one project and make deliberate progress for a session.",
                items: new[]
                {
                    "Select the project",
                    "Review current project state",
                    "Identify the next high-leverage task",
                    "Identify the main risk or blocker",
                    "Decide what \"done\" means for this session",
                },
                promptTemplate:
                    "Help me focus on one project. Identify the next high-leverage task, the main " +
                    "risk, and a clear definition of done for this work session.",
                now: now),

            BuildRoutine(
                id: "rt_default_weekly_review",
                name: "Weekly Review",
                description: "Step back, look at the week, and choose next week's focus.",
                items: new[]
                {
                    "Review wins from the week",
                    "Review misses from the week",
                    "Review active goals",
                    "Identify one thing to stop doing",
                    "Identify one thing to double down on",
                    "Choose the top focus for next week",
                },
                promptTemplate:
                    "Help me perform a weekly review. Identify patterns, wins, misses, and the " +
                    "highest-leverage focus for next week.",
                now: now),
        };
    }

    private static Routine BuildRoutine(
        string id,
        string name,
        string description,
        string[] items,
        string promptTemplate,
        DateTimeOffset now)
    {
        var checklist = new RoutineChecklistItem[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            checklist[i] = new RoutineChecklistItem(
                Id: $"{id}__item_{i + 1:00}",
                Text: items[i],
                SortOrder: i);
        }

        return new Routine(
            Id: id,
            Name: name,
            Description: description,
            ChecklistItems: checklist,
            PromptTemplate: promptTemplate,
            Enabled: true,
            CreatedAt: now,
            UpdatedAt: now,
            LastRunAt: null);
    }
}
