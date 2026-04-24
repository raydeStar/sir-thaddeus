using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Routines;

namespace Thaddeus.Runtime.Tests;

public sealed class RoutineSeederTests : IDisposable
{
    private readonly string _tempDir;

    public RoutineSeederTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-routine-seed-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Seeder_installs_the_five_default_routines_on_cold_boot()
    {
        var store = NewStore();
        var seeder = new RoutineSeeder(store, NullLogger<RoutineSeeder>.Instance);

        await seeder.SeedAsync(CancellationToken.None);

        var routines = await store.ListRoutinesAsync(CancellationToken.None);
        var names = routines.Select(r => r.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { "Evening Shutdown", "Fitness Check-In", "Morning Launch", "Project Focus", "Weekly Review" },
            names);

        Assert.All(routines, r =>
        {
            Assert.True(r.Enabled);
            Assert.NotEmpty(r.ChecklistItems);
            Assert.False(string.IsNullOrWhiteSpace(r.PromptTemplate));
        });
    }

    [Fact]
    public async Task Seeder_is_idempotent_across_reboots()
    {
        var store = NewStore();
        var seeder = new RoutineSeeder(store, NullLogger<RoutineSeeder>.Instance);
        await seeder.SeedAsync(CancellationToken.None);

        var firstPass = await store.ListRoutinesAsync(CancellationToken.None);
        var firstUpdated = firstPass.ToDictionary(r => r.Id, r => r.UpdatedAt);

        // Simulate a reboot: new store instance over the same directory.
        var reloaded = NewStore();
        var seeder2 = new RoutineSeeder(reloaded, NullLogger<RoutineSeeder>.Instance);
        await seeder2.SeedAsync(CancellationToken.None);

        var secondPass = await reloaded.ListRoutinesAsync(CancellationToken.None);
        Assert.Equal(firstPass.Count, secondPass.Count);
        foreach (var r in secondPass)
        {
            Assert.True(firstUpdated.ContainsKey(r.Id), $"Unexpected new routine {r.Id}");
            Assert.Equal(firstUpdated[r.Id], r.UpdatedAt);
        }
    }

    [Fact]
    public async Task Seeder_does_not_overwrite_user_edits()
    {
        var store = NewStore();
        var seeder = new RoutineSeeder(store, NullLogger<RoutineSeeder>.Instance);
        await seeder.SeedAsync(CancellationToken.None);

        var morning = (await store.ListRoutinesAsync(CancellationToken.None))
            .Single(r => r.Name == "Morning Launch");

        // User renames and shortens the routine.
        var userEdited = await store.UpdateRoutineAsync(
            morning.Id,
            name: "My Morning",
            description: "Personal version",
            checklistItems: new[]
            {
                new Thaddeus.SharedTypes.RoutineChecklistItem("", "skim goals", 0),
            },
            promptTemplate: null,
            enabled: null,
            CancellationToken.None);
        Assert.NotNull(userEdited);

        // Re-boot + re-seed: user's changes must survive.
        var reloaded = NewStore();
        var seeder2 = new RoutineSeeder(reloaded, NullLogger<RoutineSeeder>.Instance);
        await seeder2.SeedAsync(CancellationToken.None);

        var after = await reloaded.GetRoutineAsync(morning.Id, CancellationToken.None);
        Assert.NotNull(after);
        Assert.Equal("My Morning", after!.Name);
        Assert.Single(after.ChecklistItems);
        Assert.Equal("skim goals", after.ChecklistItems[0].Text);
    }

    [Fact]
    public async Task Seeder_is_the_only_background_service_it_registers_no_run_scheduler()
    {
        // Meta-test: IRoutineStore has NO equivalent of AutomationScheduler —
        // there is no type in the Routines namespace that advances runs on a
        // cadence. If someone reintroduces one without a product decision,
        // this test will fail so the review catches it.
        var runtimeAssembly = typeof(RoutineSeeder).Assembly;
        var backgroundServices = runtimeAssembly.GetTypes()
            .Where(t => !t.IsAbstract)
            .Where(t => typeof(Microsoft.Extensions.Hosting.IHostedService).IsAssignableFrom(t))
            .Where(t => t.Namespace == "Thaddeus.Runtime.Routines")
            .ToArray();

        Assert.Single(backgroundServices);
        Assert.Equal(typeof(RoutineSeeder), backgroundServices[0]);
    }

    private JsonFileRoutineStore NewStore() =>
        new(_tempDir, NullLogger<JsonFileRoutineStore>.Instance);
}
