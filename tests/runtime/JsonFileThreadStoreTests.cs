using Microsoft.Extensions.Logging.Abstractions;
using Thaddeus.Runtime.Chat;
using Thaddeus.SharedTypes;
using Xunit;

namespace Thaddeus.Runtime.Tests;

public class JsonFileThreadStoreTests : IDisposable
{
    private readonly string _root;

    public JsonFileThreadStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "thaddeus-thread-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }
    }

    private JsonFileThreadStore NewStore() =>
        new(_root, NullLogger<JsonFileThreadStore>.Instance);

    [Fact]
    public async Task Create_persists_thread_to_disk()
    {
        using var store = NewStore();
        var thread = await store.CreateAsync("Hello world", CancellationToken.None);

        Assert.NotEmpty(thread.Id);
        Assert.Equal("Hello world", thread.Title);
        Assert.Empty(thread.Messages);

        var path = Path.Combine(_root, thread.Id + ".json");
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task List_orders_by_most_recently_updated()
    {
        using var store = NewStore();
        var t1 = await store.CreateAsync("first", CancellationToken.None);
        await Task.Delay(15);
        var t2 = await store.CreateAsync("second", CancellationToken.None);
        await Task.Delay(15);
        await store.AppendMessageAsync(t1.Id, NewUserMessage("ping"), CancellationToken.None);

        var threads = await store.ListAsync(CancellationToken.None);

        Assert.Equal(2, threads.Count);
        Assert.Equal(t1.Id, threads[0].Id); // touched most recently
        Assert.Equal(t2.Id, threads[1].Id);
    }

    [Fact]
    public async Task AppendMessage_persists_and_returns_updated_thread()
    {
        using var store = NewStore();
        var thread = await store.CreateAsync("conv", CancellationToken.None);
        var msg = NewUserMessage("hello there");

        var updated = await store.AppendMessageAsync(thread.Id, msg, CancellationToken.None);

        Assert.Single(updated.Messages);
        Assert.Equal("hello there", updated.Messages[0].Text);
        Assert.True(updated.UpdatedAt >= thread.CreatedAt);
    }

    [Fact]
    public async Task AppendMessage_unknown_thread_throws()
    {
        using var store = NewStore();
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            store.AppendMessageAsync("th_missing", NewUserMessage("x"), CancellationToken.None));
    }

    [Fact]
    public async Task Get_returns_null_for_unknown_thread()
    {
        using var store = NewStore();
        Assert.Null(await store.GetAsync("th_missing", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_thread_and_file()
    {
        using var store = NewStore();
        var t = await store.CreateAsync("doomed", CancellationToken.None);
        var path = Path.Combine(_root, t.Id + ".json");
        Assert.True(File.Exists(path));

        var ok = await store.DeleteAsync(t.Id, CancellationToken.None);

        Assert.True(ok);
        Assert.False(File.Exists(path));
        Assert.Null(await store.GetAsync(t.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Reload_after_dispose_restores_threads_and_messages()
    {
        string threadId;
        using (var store = NewStore())
        {
            var t = await store.CreateAsync("persisted", CancellationToken.None);
            await store.AppendMessageAsync(t.Id, NewUserMessage("first"), CancellationToken.None);
            await store.AppendMessageAsync(t.Id, NewAssistantMessage("reply"), CancellationToken.None);
            threadId = t.Id;
        }

        using var reopened = NewStore();
        var loaded = await reopened.GetAsync(threadId, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("persisted", loaded!.Title);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(ChatRole.User, loaded.Messages[0].Role);
        Assert.Equal(ChatRole.Assistant, loaded.Messages[1].Role);
        Assert.Equal("reply", loaded.Messages[1].Text);
    }

    [Fact]
    public async Task Concurrent_appends_to_same_thread_do_not_lose_messages()
    {
        using var store = NewStore();
        var t = await store.CreateAsync("race", CancellationToken.None);

        var tasks = Enumerable.Range(0, 25).Select(i =>
            store.AppendMessageAsync(t.Id, NewUserMessage("msg-" + i), CancellationToken.None));
        await Task.WhenAll(tasks);

        var final = await store.GetAsync(t.Id, CancellationToken.None);
        Assert.NotNull(final);
        Assert.Equal(25, final!.Messages.Count);
    }

    private static ChatMessage NewUserMessage(string text) =>
        new("msg_" + Guid.NewGuid().ToString("N")[..12], ChatRole.User, text, DateTimeOffset.UtcNow);

    private static ChatMessage NewAssistantMessage(string text) =>
        new("msg_" + Guid.NewGuid().ToString("N")[..12], ChatRole.Assistant, text, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Rename_updates_title_and_persists()
    {
        using var store = NewStore();
        var t = await store.CreateAsync("first", CancellationToken.None);

        var renamed = await store.RenameAsync(t.Id, "  My Project  ", CancellationToken.None);

        Assert.NotNull(renamed);
        Assert.Equal("My Project", renamed!.Title);

        // Round-trip through a fresh store proves the rename hit disk.
        using var store2 = NewStore();
        var loaded = await store2.GetAsync(t.Id, CancellationToken.None);
        Assert.Equal("My Project", loaded!.Title);
    }

    [Fact]
    public async Task Rename_falls_back_to_Untitled_when_blank()
    {
        using var store = NewStore();
        var t = await store.CreateAsync("first", CancellationToken.None);

        var renamed = await store.RenameAsync(t.Id, "   ", CancellationToken.None);

        Assert.Equal("Untitled", renamed!.Title);
    }

    [Fact]
    public async Task Rename_returns_null_for_unknown_thread()
    {
        using var store = NewStore();
        var renamed = await store.RenameAsync("th_missing", "x", CancellationToken.None);
        Assert.Null(renamed);
    }

    [Fact]
    public async Task SetPinned_toggles_pin_and_persists()
    {
        using var store = NewStore();
        var t = await store.CreateAsync("first", CancellationToken.None);
        Assert.False(t.Pinned);

        var pinned = await store.SetPinnedAsync(t.Id, true, CancellationToken.None);
        Assert.True(pinned!.Pinned);

        // Round-trip persistence.
        using var store2 = NewStore();
        var loaded = await store2.GetAsync(t.Id, CancellationToken.None);
        Assert.True(loaded!.Pinned);

        // Toggle off.
        var unpinned = await store.SetPinnedAsync(t.Id, false, CancellationToken.None);
        Assert.False(unpinned!.Pinned);
    }
}
