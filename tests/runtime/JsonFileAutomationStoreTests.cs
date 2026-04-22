using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Automations;

namespace Thaddeus.Runtime.Tests;

public sealed class JsonFileAutomationStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileAutomationStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-auto-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Create_then_get_round_trips_fields()
    {
        var store = NewStore();
        var auto = await store.CreateAsync(
            "Morning brief",
            "Summarise yesterday's notes",
            new[] { "  list yesterday's memos  ", "", "summarise into 5 bullets" },
            enabled: true,
            allowedTools: null,
            schedule: null,
            CancellationToken.None);

        Assert.StartsWith("auto_", auto.Id);
        Assert.Equal("Morning brief", auto.Name);
        Assert.Equal("manual", auto.Trigger);
        Assert.True(auto.Enabled);
        Assert.Equal(2, auto.Steps.Count);
        Assert.Null(auto.LastRunAt);

        var fetched = await store.GetAsync(auto.Id, CancellationToken.None);
        Assert.Equal(auto, fetched);
    }

    [Fact]
    public async Task RecordRun_stamps_LastRunAt()
    {
        var store = NewStore();
        var auto = await store.CreateAsync("x", "", new[] { "go" }, true, allowedTools: null, schedule: null, CancellationToken.None);

        var stamped = await store.RecordRunAsync(auto.Id, CancellationToken.None);

        Assert.NotNull(stamped);
        Assert.NotNull(stamped!.LastRunAt);
        Assert.True(stamped.LastRunAt > auto.CreatedAt.AddSeconds(-1));
    }

    [Fact]
    public async Task Update_partial_keeps_unchanged_fields()
    {
        var store = NewStore();
        var auto = await store.CreateAsync("orig", "desc", new[] { "a", "b" }, true, allowedTools: null, schedule: null, CancellationToken.None);

        var updated = await store.UpdateAsync(auto.Id, name: "renamed", description: null, steps: null, enabled: false, allowedTools: null, schedule: null, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("renamed", updated!.Name);
        Assert.Equal("desc", updated.Description);
        Assert.Equal(new[] { "a", "b" }, updated.Steps);
        Assert.False(updated.Enabled);
    }

    [Fact]
    public async Task List_orders_recent_first()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A", "", new[] { "s" }, true, allowedTools: null, schedule: null, CancellationToken.None);
        await Task.Delay(15);
        var b = await store.CreateAsync("B", "", new[] { "s" }, true, allowedTools: null, schedule: null, CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Equal(new[] { b.Id, a.Id }, list.Select(x => x.Id).ToArray());
    }

    [Fact]
    public async Task Delete_removes_and_returns_false_second_time()
    {
        var store = NewStore();
        var auto = await store.CreateAsync("temp", "", new[] { "s" }, true, allowedTools: null, schedule: null, CancellationToken.None);

        Assert.True(await store.DeleteAsync(auto.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(auto.Id, CancellationToken.None));
    }

    private JsonFileAutomationStore NewStore() =>
        new(_tempDir, NullLogger<JsonFileAutomationStore>.Instance);
}
