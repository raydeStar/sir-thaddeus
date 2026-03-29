using SirThaddeus.Contracts;
using Xunit;

namespace SirThaddeus.Tests;

public class BriefingSessionStoreTests
{
    [Fact]
    public void Record_SetsActiveBriefing()
    {
        var store = new BriefingSessionStore();
        var b = MakeBriefing("Coffee Shop", "high");

        store.Record(b);

        Assert.NotNull(store.ActiveBriefing);
        Assert.Equal("Coffee Shop", store.ActiveBriefing!.Hero.Title);
    }

    [Fact]
    public void Record_InsertsAtFront()
    {
        var store = new BriefingSessionStore();
        store.Record(MakeBriefing("First", "high"));
        store.Record(MakeBriefing("Second", "medium"));

        Assert.Equal(2, store.History.Count);
        Assert.Equal("Second", store.History[0].Title);
        Assert.Equal("First", store.History[1].Title);
    }

    [Fact]
    public void Record_DeduplicatesSameBriefingAtFront()
    {
        var store = new BriefingSessionStore();
        store.Record(MakeBriefing("Pizza", "high"));
        store.Record(MakeBriefing("Pizza", "medium")); // Same identity, different confidence

        Assert.Single(store.History);
        Assert.Equal("Partial", store.History[0].ConfidenceLabel); // Updated to medium
    }

    [Fact]
    public void Record_DoesNotDeduplicateDifferentBriefings()
    {
        var store = new BriefingSessionStore();
        store.Record(MakeBriefing("Pizza", "high"));
        store.Record(MakeBriefing("Burger", "high"));

        Assert.Equal(2, store.History.Count);
    }

    [Fact]
    public void Record_CapsAtMaxHistory()
    {
        var store = new BriefingSessionStore();
        for (int i = 0; i < 30; i++)
            store.Record(MakeBriefing($"Place{i}", "high"));

        Assert.Equal(BriefingSessionStore.MaxHistory, store.History.Count);
    }

    [Fact]
    public void History_EntriesHaveFormattedFields()
    {
        var store = new BriefingSessionStore();
        store.Record(MakeBriefing("Nice Cafe", "high"));

        var entry = store.History[0];
        Assert.Equal("Nice Cafe", entry.Title);
        Assert.Equal("Verified", entry.ConfidenceLabel);
        Assert.Contains("Nice Cafe", entry.StatusLine);
    }

    // ─── Helper ───────────────────────────────────────────────────────

    private static DeepDiveBriefingDto MakeBriefing(string title, string confidence) =>
        new(
            Version: 1,
            Topic: new BriefingTopicDto("place", title.ToLowerInvariant(), "UTC", "en-US", null),
            Hero: new BriefingHeroDto(title, confidence, "2025-01-01T00:00:00Z",
                "Open", "", "", "", "", ""),
            Cards: [],
            Map: null,
            Audit: []);
}
