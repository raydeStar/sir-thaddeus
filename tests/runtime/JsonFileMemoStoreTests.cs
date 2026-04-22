using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Memory;

namespace Thaddeus.Runtime.Tests;

public sealed class JsonFileMemoStoreTests : IDisposable
{
    private readonly string _tempDir;

    public JsonFileMemoStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "thaddeus-memo-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Create_then_get_round_trips_fields()
    {
        var store = NewStore();
        var memo = await store.CreateAsync(
            "Project Goals",
            "## focus\n- ship phase 8",
            new[] { "Project", "PROJECT", "  goals  " },
            pinned: true,
            CancellationToken.None);

        Assert.StartsWith("mem_", memo.Id);
        Assert.Equal("Project Goals", memo.Title);
        Assert.True(memo.Pinned);
        Assert.Equal(new[] { "project", "goals" }, memo.Tags);

        var fetched = await store.GetAsync(memo.Id, CancellationToken.None);
        Assert.Equal(memo, fetched);
    }

    [Fact]
    public async Task List_orders_pinned_first_then_recent()
    {
        var store = NewStore();
        var a = await store.CreateAsync("A", "", null, false, CancellationToken.None);
        await Task.Delay(15); // separate UpdatedAt
        var b = await store.CreateAsync("B", "", null, false, CancellationToken.None);
        await Task.Delay(15);
        var c = await store.CreateAsync("C", "", null, true, CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);

        Assert.Equal(new[] { c.Id, b.Id, a.Id }, list.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task Update_is_partial_and_bumps_timestamp()
    {
        var store = NewStore();
        var memo = await store.CreateAsync("orig", "body1", new[] { "x" }, false, CancellationToken.None);
        await Task.Delay(10);

        var updated = await store.UpdateAsync(memo.Id, title: "renamed", body: null, tags: null, pinned: true, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("renamed", updated!.Title);
        Assert.Equal("body1", updated.Body);   // unchanged
        Assert.Equal(new[] { "x" }, updated.Tags); // unchanged
        Assert.True(updated.Pinned);
        Assert.True(updated.UpdatedAt > memo.UpdatedAt);
    }

    [Fact]
    public async Task Delete_removes_from_list_and_returns_false_second_time()
    {
        var store = NewStore();
        var memo = await store.CreateAsync("temp", "", null, false, CancellationToken.None);

        Assert.True(await store.DeleteAsync(memo.Id, CancellationToken.None));
        Assert.False(await store.DeleteAsync(memo.Id, CancellationToken.None));
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Persists_across_store_instances()
    {
        var store1 = NewStore();
        var memo = await store1.CreateAsync("survive restart", "body", new[] { "k" }, true, CancellationToken.None);
        store1.Dispose();

        var store2 = NewStore();
        var fetched = await store2.GetAsync(memo.Id, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(memo.Id, fetched!.Id);
        Assert.Equal(memo.Title, fetched.Title);
        Assert.Equal(memo.Body, fetched.Body);
        Assert.Equal(memo.Tags, fetched.Tags);
        Assert.Equal(memo.Pinned, fetched.Pinned);
        Assert.Equal(memo.CreatedAt, fetched.CreatedAt);
        Assert.Equal(memo.UpdatedAt, fetched.UpdatedAt);
    }

    private JsonFileMemoStore NewStore() =>
        new(_tempDir, NullLogger<JsonFileMemoStore>.Instance);
}
