using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Routines;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class JsonFileRoutineStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileRoutineStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-routine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Create_assigns_ids_and_persists()
    {
        var store = NewStore();
        var items = new[]
        {
            new RoutineChecklistItem("", " review goals ", 0),
            new RoutineChecklistItem("", "", 1), // blank — dropped
            new RoutineChecklistItem("", "pick top 3", 2),
        };

        var routine = await store.CreateRoutineAsync(
            " Morning ", "start the day", items, promptTemplate: "help me plan",
            enabled: true, CancellationToken.None);

        Assert.StartsWith("rt_", routine.Id);
        Assert.Equal("Morning", routine.Name);
        Assert.Equal(2, routine.ChecklistItems.Count);
        Assert.Equal("review goals", routine.ChecklistItems[0].Text);
        Assert.All(routine.ChecklistItems, i => Assert.StartsWith("ci_", i.Id));
        Assert.Equal(0, routine.ChecklistItems[0].SortOrder);
        Assert.Equal(1, routine.ChecklistItems[1].SortOrder);

        var reloaded = NewStore();
        var fetched = await reloaded.GetRoutineAsync(routine.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(routine.Name, fetched!.Name);
        Assert.Equal(routine.ChecklistItems.Count, fetched.ChecklistItems.Count);
    }

    [Fact]
    public async Task SeedRoutine_is_noop_when_id_exists()
    {
        var store = NewStore();
        var seed = BuildSeed("rt_fixed_a", "Original");

        await store.SeedRoutineAsync(seed, CancellationToken.None);

        // User edits the name — seeder must not clobber this on the next boot.
        await store.UpdateRoutineAsync("rt_fixed_a",
            name: "User renamed", description: null, checklistItems: null, promptTemplate: null,
            enabled: null, CancellationToken.None);

        // Simulate second app start: reload store, call seed again.
        var reloaded = NewStore();
        var after = await reloaded.SeedRoutineAsync(seed, CancellationToken.None);
        Assert.Equal("User renamed", after.Name);
    }

    [Fact]
    public async Task UpdateRoutine_preserves_unspecified_fields()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "orig", "desc",
            new[] { new RoutineChecklistItem("", "a", 0) },
            promptTemplate: "tmpl", enabled: true, CancellationToken.None);

        var updated = await store.UpdateRoutineAsync(
            routine.Id, name: "renamed", description: null, checklistItems: null,
            promptTemplate: null, enabled: false, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("renamed", updated!.Name);
        Assert.Equal("desc", updated.Description);
        Assert.Single(updated.ChecklistItems);
        Assert.Equal("tmpl", updated.PromptTemplate);
        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task StartRun_snapshots_checklist_and_stamps_last_run_at()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "r", "",
            new[]
            {
                new RoutineChecklistItem("", "one", 0),
                new RoutineChecklistItem("", "two", 1),
            },
            promptTemplate: null, enabled: true, CancellationToken.None);

        var run = await store.StartRunAsync(routine.Id, CancellationToken.None);

        Assert.NotNull(run);
        Assert.StartsWith("rr_", run!.Id);
        Assert.Null(run.CompletedAt);
        Assert.Equal(2, run.Items.Count);
        Assert.All(run.Items, i => Assert.False(i.IsCompleted));

        var bumped = await store.GetRoutineAsync(routine.Id, CancellationToken.None);
        Assert.NotNull(bumped!.LastRunAt);
    }

    [Fact]
    public async Task UpdateRun_toggles_items_and_computes_completion_percent()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "r", "",
            new[]
            {
                new RoutineChecklistItem("", "a", 0),
                new RoutineChecklistItem("", "b", 1),
                new RoutineChecklistItem("", "c", 2),
                new RoutineChecklistItem("", "d", 3),
            },
            promptTemplate: null, enabled: true, CancellationToken.None);

        var run = await store.StartRunAsync(routine.Id, CancellationToken.None);
        Assert.NotNull(run);

        var updates = new Dictionary<string, bool>
        {
            [run!.Items[0].ChecklistItemId] = true,
            [run.Items[2].ChecklistItemId] = true,
        };
        var updated = await store.UpdateRunAsync(run.Id, updates, userNote: "halfway there", CancellationToken.None);

        Assert.NotNull(updated);
        Assert.True(updated!.Items[0].IsCompleted);
        Assert.False(updated.Items[1].IsCompleted);
        Assert.True(updated.Items[2].IsCompleted);
        Assert.False(updated.Items[3].IsCompleted);

        var completed = updated.Items.Count(i => i.IsCompleted);
        var pct = (int)Math.Round(100.0 * completed / updated.Items.Count);
        Assert.Equal(50, pct);
        Assert.Equal("halfway there", updated.UserNote);
    }

    [Fact]
    public async Task CompleteRun_is_idempotent_and_seals_the_run()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "r", "",
            new[] { new RoutineChecklistItem("", "a", 0) },
            promptTemplate: null, enabled: true, CancellationToken.None);

        var run = await store.StartRunAsync(routine.Id, CancellationToken.None);
        Assert.NotNull(run);

        var sealedRun = await store.CompleteRunAsync(run!.Id, "done", CancellationToken.None);
        Assert.NotNull(sealedRun!.CompletedAt);

        // Second complete returns the same record unchanged — idempotent.
        var second = await store.CompleteRunAsync(run.Id, "different note", CancellationToken.None);
        Assert.Equal(sealedRun.CompletedAt, second!.CompletedAt);
        Assert.Equal("done", second.UserNote);

        // Further mutations on a sealed run are no-ops.
        var patched = await store.UpdateRunAsync(run.Id,
            new Dictionary<string, bool> { [run.Items[0].ChecklistItemId] = false },
            userNote: "revise", CancellationToken.None);
        Assert.NotNull(patched!.CompletedAt);
        Assert.True(patched.Items[0].IsCompleted == sealedRun.Items[0].IsCompleted);
    }

    [Fact]
    public async Task Delete_routine_cascades_its_runs()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "r", "",
            new[] { new RoutineChecklistItem("", "a", 0) },
            promptTemplate: null, enabled: true, CancellationToken.None);

        var run = await store.StartRunAsync(routine.Id, CancellationToken.None);
        Assert.NotNull(run);

        Assert.True(await store.DeleteRoutineAsync(routine.Id, CancellationToken.None));

        Assert.Null(await store.GetRoutineAsync(routine.Id, CancellationToken.None));
        Assert.Null(await store.GetRunAsync(run!.Id, CancellationToken.None));
        Assert.Empty(await store.ListRunsAsync(routine.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ListRunsAsync_orders_newest_first()
    {
        var store = NewStore();
        var routine = await store.CreateRoutineAsync(
            "r", "",
            new[] { new RoutineChecklistItem("", "a", 0) },
            promptTemplate: null, enabled: true, CancellationToken.None);

        var first = await store.StartRunAsync(routine.Id, CancellationToken.None);
        await Task.Delay(15);
        var second = await store.StartRunAsync(routine.Id, CancellationToken.None);

        var list = await store.ListRunsAsync(routine.Id, CancellationToken.None);
        Assert.Equal(new[] { second!.Id, first!.Id }, list.Select(r => r.Id).ToArray());
    }

    private JsonFileRoutineStore NewStore() =>
        new(_tempDir, NullLogger<JsonFileRoutineStore>.Instance);

    private static Routine BuildSeed(string id, string name) =>
        new(
            Id: id,
            Name: name,
            Description: "",
            ChecklistItems: new[] { new RoutineChecklistItem("ci_one", "one", 0) },
            PromptTemplate: null,
            Enabled: true,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            LastRunAt: null);
}
