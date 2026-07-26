using SirThaddeus.Memory;
using SirThaddeus.Memory.Sqlite;

namespace Thaddeus.Runtime.Tests;

public sealed class MemoryResetStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "thaddeus-memory-reset-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Reset_permanently_removes_every_durable_memory_record_kind()
    {
        Directory.CreateDirectory(_root);
        using var store = new SqliteMemoryStore(Path.Combine(_root, "memory.db"));
        await store.EnsureSchemaAsync();
        await store.StoreFactAsync(new MemoryFact
        {
            MemoryId = "fact-1",
            Subject = "user",
            Predicate = "prefers",
            Object = "tea",
        });
        await store.StoreEventAsync(new MemoryEvent
        {
            EventId = "event-1",
            Type = "meeting",
            Title = "Review",
        });
        await store.StoreChunkAsync(new MemoryChunk
        {
            ChunkId = "chunk-1",
            SourceType = "conversation",
            Text = "private conversation fragment",
        });
        await store.StoreProfileAsync(new ProfileCard
        {
            ProfileId = "profile-1",
            DisplayName = "Ayric",
        });
        await store.StoreNuggetAsync(new MemoryNugget
        {
            NuggetId = "nugget-1",
            Text = "Use concise answers",
        });

        var removed = await store.ResetAllAsync();

        Assert.Equal(5, removed);
        Assert.Equal(0, (await store.ListFactsAsync(null, 0, 10)).TotalCount);
        Assert.Equal(0, (await store.ListEventsAsync(null, 0, 10)).TotalCount);
        Assert.Equal(0, (await store.ListChunksAsync(null, 0, 10)).TotalCount);
        Assert.Empty(await store.ListProfilesAsync());
        Assert.Equal(0, (await store.ListNuggetsAsync(null, 0, 10)).TotalCount);
        Assert.Equal(0, await store.ResetAllAsync());
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
