using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Search.DeepDive;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for individual pipeline stages of the deep-dive place briefing.
/// Each test exercises one stage in isolation so failures are localizable.
/// </summary>
public class DeepDivePipelineStageTests
{
    // ── Stage 2: Search term construction ─────────────────────────

    [Theory]
    [InlineData("Deep dive Seattle Flowers with hours + reviews and what to expect.", "Seattle Flowers")]
    [InlineData("What are the operating hours of Starbucks in Olympia, WA?", "Starbucks in Olympia, WA")]
    [InlineData("When does Trader Joe's in Portland OR open?", "Trader Joe's in Portland OR")]
    [InlineData("Is Walmart in Portland OR open right now?", "Walmart in Portland OR")]
    [InlineData("When does Target in Seattle close today?", "Target in Seattle")]
    public void CleanQueryForWebFallback_ExtractsBusinessName(string input, string expected)
    {
        var method = typeof(DeepDiveCoordinator).GetMethod(
            "CleanQueryForWebFallback",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = (string)method!.Invoke(null, [input])!;
        Assert.Equal(expected, result);
    }

    // ── Stage 5: Open/closed status computation ───────────────────

    [Fact]
    public void TryParseTime_Parses12HourFormats()
    {
        Assert.Equal(new TimeSpan(7, 0, 0), DeepDiveCoordinator.TryParseTime("7 AM"));
        Assert.Equal(new TimeSpan(9, 30, 0), DeepDiveCoordinator.TryParseTime("9:30 AM"));
        Assert.Equal(new TimeSpan(21, 0, 0), DeepDiveCoordinator.TryParseTime("9 PM"));
        Assert.Equal(new TimeSpan(17, 30, 0), DeepDiveCoordinator.TryParseTime("5:30 pm"));
        Assert.Equal(new TimeSpan(0, 0, 0), DeepDiveCoordinator.TryParseTime("12 AM"));
        Assert.Equal(new TimeSpan(12, 0, 0), DeepDiveCoordinator.TryParseTime("12 PM"));
    }

    [Fact]
    public void TryParseTime_Parses24HourFormats()
    {
        Assert.Equal(new TimeSpan(8, 0, 0), DeepDiveCoordinator.TryParseTime("08:00"));
        Assert.Equal(new TimeSpan(21, 0, 0), DeepDiveCoordinator.TryParseTime("21:00"));
        Assert.Equal(new TimeSpan(0, 0, 0), DeepDiveCoordinator.TryParseTime("00:00"));
    }

    [Fact]
    public void TryParseTime_ReturnsNullForGarbage()
    {
        Assert.Null(DeepDiveCoordinator.TryParseTime(""));
        Assert.Null(DeepDiveCoordinator.TryParseTime("abc"));
        Assert.Null(DeepDiveCoordinator.TryParseTime("25:00"));
    }

    [Fact]
    public void TryComputeOpenStatus_BusinessIsOpen_ReturnsTrue()
    {
        var bullets = new List<string>
        {
            "Monday: 7 AM - 9 PM",
            "Tuesday: 7 AM - 9 PM",
            "Wednesday: 7 AM - 9 PM"
        };

        // Monday at 2 PM → should be open
        var monday2pm = new DateTimeOffset(2026, 3, 30, 14, 0, 0, TimeSpan.Zero); // Monday
        var (isOpen, todayHours) = DeepDiveCoordinator.TryComputeOpenStatus(
            bullets, "UTC", monday2pm);

        Assert.True(isOpen);
        Assert.Equal("7 AM - 9 PM", todayHours);
    }

    [Fact]
    public void TryComputeOpenStatus_BusinessIsClosed_ReturnsFalse()
    {
        var bullets = new List<string>
        {
            "Monday: 7 AM - 9 PM",
            "Tuesday: 7 AM - 9 PM"
        };

        // Monday at 10 PM → should be closed
        var monday10pm = new DateTimeOffset(2026, 3, 30, 22, 0, 0, TimeSpan.Zero); // Monday
        var (isOpen, todayHours) = DeepDiveCoordinator.TryComputeOpenStatus(
            bullets, "UTC", monday10pm);

        Assert.False(isOpen);
        Assert.Equal("7 AM - 9 PM", todayHours);
    }

    [Fact]
    public void TryComputeOpenStatus_ClosedDay_ReturnsFalse()
    {
        var bullets = new List<string>
        {
            "Monday: 8:00 AM - 5:00 PM",
            "Sunday: Closed"
        };

        // Sunday at noon → should be closed
        var sundayNoon = new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero); // Sunday
        var (isOpen, _) = DeepDiveCoordinator.TryComputeOpenStatus(
            bullets, "UTC", sundayNoon);

        Assert.False(isOpen);
    }

    [Fact]
    public void TryComputeOpenStatus_Open24Hours_ReturnsTrue()
    {
        var bullets = new List<string> { "Monday: Open 24 hours" };

        var monday3am = new DateTimeOffset(2026, 3, 30, 3, 0, 0, TimeSpan.Zero);
        var (isOpen, _) = DeepDiveCoordinator.TryComputeOpenStatus(
            bullets, "UTC", monday3am);

        Assert.True(isOpen);
    }

    [Fact]
    public void TryComputeOpenStatus_NoBullets_ReturnsNull()
    {
        var (isOpen, todayHours) = DeepDiveCoordinator.TryComputeOpenStatus(
            [], "UTC", DateTimeOffset.UtcNow);

        Assert.Null(isOpen);
        Assert.Null(todayHours);
    }

    [Fact]
    public void TryComputeOpenStatus_RespectsTimezone()
    {
        var bullets = new List<string> { "Friday: 8 AM - 6 PM" };

        // Friday 5 PM UTC = Friday 10 AM Pacific
        var fridayUtc = new DateTimeOffset(2026, 3, 27, 17, 0, 0, TimeSpan.Zero);
        var (isOpen, _) = DeepDiveCoordinator.TryComputeOpenStatus(
            bullets, "Pacific Standard Time", fridayUtc);

        Assert.True(isOpen);
    }

    // ── Stage 4: Source filtering ─────────────────────────────────

    [Fact]
    public void IsNavigablePlaceFallbackSource_RejectsAppStores()
    {
        var method = typeof(DeepDiveCoordinator).GetMethod(
            "IsNavigablePlaceFallbackSource",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var appleSource = new SourceItem { Url = "https://apps.apple.com/us/app/target/id297430070", Title = "Target App" };
        var googlePlaySource = new SourceItem { Url = "https://play.google.com/store/apps/details?id=com.walmart.android", Title = "Walmart App" };
        var wikiSource = new SourceItem { Url = "https://en.wikipedia.org/wiki/Seattle", Title = "Seattle" };

        Assert.False((bool)method!.Invoke(null, [appleSource])!);
        Assert.False((bool)method!.Invoke(null, [googlePlaySource])!);
        Assert.False((bool)method!.Invoke(null, [wikiSource])!);
    }

    [Fact]
    public void IsNavigablePlaceFallbackSource_AllowsBusinessSites()
    {
        var method = typeof(DeepDiveCoordinator).GetMethod(
            "IsNavigablePlaceFallbackSource",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var yelpSource = new SourceItem { Url = "https://www.yelp.com/biz/seattle-flowers", Title = "Seattle Flowers" };
        var storeSource = new SourceItem { Url = "https://www.storeopeninghours.com/target-seattle", Title = "Target Seattle" };
        var officialSource = new SourceItem { Url = "https://www.target.com/sl/seattle-pike-plaza/2786", Title = "Target Seattle" };

        Assert.True((bool)method!.Invoke(null, [yelpSource])!);
        Assert.True((bool)method!.Invoke(null, [storeSource])!);
        Assert.True((bool)method!.Invoke(null, [officialSource])!);
    }

    // ── Stage 2: Hours parser ─────────────────────────────────────

    [Fact]
    public void HoursParser_ExtractsDayHours()
    {
        var chunks = new[] { "Monday: 8:00 AM - 5:00 PM\nTuesday: 9 AM - 6 PM\nSunday: Closed" };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.Contains(result.Bullets, b => b.StartsWith("Monday:", StringComparison.Ordinal));
        Assert.Contains(result.Bullets, b => b.StartsWith("Tuesday:", StringComparison.Ordinal));
        Assert.Contains(result.Bullets, b => b.Contains("Closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void HoursParser_HandlesDayRange()
    {
        var chunks = new[] { "Mon-Fri: 9 AM - 5 PM" };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.True(result.Bullets.Count >= 5); // Mon through Fri
    }

    [Fact]
    public void HoursParser_HandlesDayRangeWithThrough()
    {
        var chunks = new[] { "Monday through Friday: 9 AM - 5 PM" };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.True(result.Bullets.Count >= 5); // Mon through Fri
    }

    [Fact]
    public void HoursParser_NormalizesAmPmWithPeriods()
    {
        var chunks = new[] { "Monday: 10:30 a.m. - 6:00 p.m." };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.Contains(result.Bullets, b => b.StartsWith("Monday:", StringComparison.Ordinal));
    }

    [Fact]
    public void HoursParser_NaturalLanguageHours()
    {
        var chunks = new[] { "Our hours are from 10:30 am to 6:00 pm Monday through Saturday." };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.True(result.Bullets.Count >= 6); // Mon through Sat
    }

    [Fact]
    public void HoursParser_NaturalLanguageHoursWithPeriods()
    {
        var chunks = new[] { "We are OPEN for curbside pickup and contactless delivery. Our hours are from 10:30 a.m. to 6:00 p.m. Monday through Saturday." };
        var result = DeepDiveHoursParser.Parse(chunks);

        Assert.True(result.HasAnyHours);
        Assert.True(result.Bullets.Count >= 6); // Mon through Sat
    }

    // ── Stage 3: Source prioritization ────────────────────────────

    [Fact]
    public void HoursSourcePriority_RanksAggregatorsAboveGenericSites()
    {
        var yelp = new SourceItem { Title = "Seattle Flowers - Yelp", Url = "https://www.yelp.com/biz/seattle-flowers", Snippet = "Great flowers" };
        var storeHours = new SourceItem { Title = "Target Seattle Hours", Url = "https://www.storeopeninghours.com/target-seattle", Snippet = "" };
        var bizSite = new SourceItem { Title = "Seattle Flowers", Url = "https://www.seattleflowers.com/", Snippet = "buy flowers" };
        var bizSiteWithHours = new SourceItem { Title = "Seattle Flowers", Url = "https://www.seattleflowers.com/", Snippet = "open hours 9 AM" };

        Assert.True(DeepDiveCoordinator.HoursSourcePriority(storeHours) > DeepDiveCoordinator.HoursSourcePriority(yelp));
        Assert.True(DeepDiveCoordinator.HoursSourcePriority(yelp) > DeepDiveCoordinator.HoursSourcePriority(bizSite));
        Assert.True(DeepDiveCoordinator.HoursSourcePriority(bizSiteWithHours) > DeepDiveCoordinator.HoursSourcePriority(bizSite));
    }
}
