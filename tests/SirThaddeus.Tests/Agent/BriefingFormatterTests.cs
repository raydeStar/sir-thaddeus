using SirThaddeus.Contracts;
using Xunit;

namespace SirThaddeus.Tests;

public class BriefingFormatterTests
{
    // ─── Confidence Labels ────────────────────────────────────────────

    [Theory]
    [InlineData("high", "Verified")]
    [InlineData("High", "Verified")]
    [InlineData("medium", "Partial")]
    [InlineData("low", "Unverified")]
    [InlineData(null, "Unknown")]
    [InlineData("", "Unknown")]
    [InlineData("garbage", "Unknown")]
    public void FormatConfidenceLabel_MapsCorrectly(string? input, string expected)
    {
        Assert.Equal(expected, BriefingFormatter.FormatConfidenceLabel(input));
    }

    // ─── Status Message ───────────────────────────────────────────────

    [Fact]
    public void BuildBriefingStatusMessage_HighConfidence()
    {
        var b = MakeBriefing("Pizza Palace", "high");
        var msg = BriefingFormatter.BuildBriefingStatusMessage(b);
        Assert.Contains("Pizza Palace", msg);
        Assert.EndsWith("ready.", msg);
    }

    [Fact]
    public void BuildBriefingStatusMessage_MediumConfidence()
    {
        var b = MakeBriefing("Coffee Shop", "medium");
        var msg = BriefingFormatter.BuildBriefingStatusMessage(b);
        Assert.Contains("Double-check", msg);
    }

    [Fact]
    public void BuildBriefingStatusMessage_LowConfidence()
    {
        var b = MakeBriefing("Unknown Place", "low");
        var msg = BriefingFormatter.BuildBriefingStatusMessage(b);
        Assert.Contains("Verify key details", msg);
    }

    [Fact]
    public void BuildBriefingStatusMessage_FallsBackToQuery()
    {
        var b = MakeBriefing("", "high");
        var msg = BriefingFormatter.BuildBriefingStatusMessage(b);
        Assert.Contains("test query", msg); // from Topic.Query
    }

    // ─── Timestamp Formatting ─────────────────────────────────────────

    [Fact]
    public void FormatIsoTimestamp_Null_ReturnsUnknown()
    {
        Assert.Equal("Unknown", BriefingFormatter.FormatIsoTimestamp(null));
    }

    [Fact]
    public void FormatIsoTimestamp_ValidIso_ReturnsFormatted()
    {
        var result = BriefingFormatter.FormatIsoTimestamp("2025-01-15T14:30:00Z");
        Assert.NotEqual("Unknown", result);
        Assert.Contains("01/15/2025", result); // invariant culture short date
    }

    [Fact]
    public void FormatIsoTimestamp_Garbage_ReturnsOriginal()
    {
        Assert.Equal("not-a-date", BriefingFormatter.FormatIsoTimestamp("not-a-date"));
    }

    // ─── ValueOrFallback ──────────────────────────────────────────────

    [Theory]
    [InlineData(null, "-")]
    [InlineData("", "-")]
    [InlineData("  ", "-")]
    [InlineData("hello", "hello")]
    [InlineData("  spaced  ", "spaced")]
    public void ValueOrFallback_HandlesEdgeCases(string? input, string expected)
    {
        Assert.Equal(expected, BriefingFormatter.ValueOrFallback(input));
    }

    // ─── Source Collection ────────────────────────────────────────────

    [Fact]
    public void CollectBriefingSources_DeduplicatesByUrl()
    {
        var src1 = new BriefingSourceRefDto("Yelp", "https://yelp.com/biz/1", "2025-01-01T00:00:00Z");
        var src2 = new BriefingSourceRefDto("Yelp Duplicate", "https://yelp.com/biz/1", "2025-01-01T00:00:00Z");
        var src3 = new BriefingSourceRefDto("Google", "https://google.com/maps/1", "2025-01-01T00:00:00Z");

        var b = MakeBriefingWithSources(
            [new BriefingCardDto("hours", "Hours", [src1, src2], ["Open Mon-Fri"])],
            [new BriefingAuditStepDto("step1", "detail", "2025-01-01T00:00:00Z", [src3])]);

        var sources = BriefingFormatter.CollectBriefingSources(b);
        Assert.Equal(2, sources.Count);
        Assert.Equal("Yelp", sources[0].Name);
        Assert.Equal("Google", sources[1].Name);
    }

    [Fact]
    public void CollectBriefingSources_IncludesHeroWebsite()
    {
        var b = MakeBriefing("Place", "high", "https://place.com");
        var sources = BriefingFormatter.CollectBriefingSources(b);
        Assert.Single(sources);
        Assert.Equal("Website", sources[0].Name);
    }

    [Fact]
    public void CollectBriefingSources_EmptyBriefing_ReturnsEmpty()
    {
        var b = MakeBriefing("Place", "high");
        var sources = BriefingFormatter.CollectBriefingSources(b);
        Assert.Empty(sources);
    }

    // ─── Source Summary ───────────────────────────────────────────────

    [Fact]
    public void BuildSourceSummary_FormatsNicelyWithLimit()
    {
        var sources = Enumerable.Range(1, 6)
            .Select(i => new BriefingSourceRefDto($"Source{i}", $"https://s{i}.com", "2025-01-01T00:00:00Z"))
            .ToList();

        var summary = BriefingFormatter.BuildSourceSummary(sources);
        Assert.StartsWith("Sources: ", summary);
        // Only 4 shown
        Assert.Contains("Source1", summary);
        Assert.Contains("Source4", summary);
        Assert.DoesNotContain("Source5", summary);
    }

    [Fact]
    public void BuildSourceSummary_Empty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, BriefingFormatter.BuildSourceSummary([]));
    }

    // ─── Source Meta ──────────────────────────────────────────────────

    [Fact]
    public void BuildSourceMeta_ExtractsHostname()
    {
        var src = new BriefingSourceRefDto("Yelp", "https://www.yelp.com/biz/pizza", "2025-01-15T10:00:00Z");
        var meta = BriefingFormatter.BuildSourceMeta(src);
        Assert.Contains("www.yelp.com", meta);
        Assert.Contains("checked", meta);
    }

    // ─── Briefing Equality ────────────────────────────────────────────

    [Fact]
    public void SameBriefing_SameQueryAndTitle_True()
    {
        var a = MakeBriefing("Pizza Place", "high");
        var b = MakeBriefing("pizza place", "low"); // different confidence, same identity
        Assert.True(BriefingFormatter.SameBriefing(a, b));
    }

    [Fact]
    public void SameBriefing_DifferentTitle_False()
    {
        var a = MakeBriefing("Pizza Place", "high");
        var b = MakeBriefing("Burger Joint", "high");
        Assert.False(BriefingFormatter.SameBriefing(a, b));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static DeepDiveBriefingDto MakeBriefing(string title, string confidence, string? website = null) =>
        new(
            Version: 1,
            Topic: new BriefingTopicDto("place", "test query", "UTC", "en-US", null),
            Hero: new BriefingHeroDto(title, confidence, "2025-01-01T00:00:00Z",
                "Open", "", "", "", website ?? "", ""),
            Cards: [],
            Map: null,
            Audit: []);

    private static DeepDiveBriefingDto MakeBriefingWithSources(
        IReadOnlyList<BriefingCardDto> cards,
        IReadOnlyList<BriefingAuditStepDto> audit) =>
        new(
            Version: 1,
            Topic: new BriefingTopicDto("place", "test query", "UTC", "en-US", null),
            Hero: new BriefingHeroDto("Test", "high", "2025-01-01T00:00:00Z",
                "Open", "", "", "", "", ""),
            Cards: cards,
            Map: null,
            Audit: audit);
}
