using SirThaddeus.Memory;

namespace SirThaddeus.Tests;

/// <summary>
/// Tests for the shallow memory personalization layer:
/// - Profile Card and Memory Nugget domain models
/// - Shared parsing utilities (<see cref="MemoryParsing"/>)
/// - Nugget retrieval capping and sensitivity filtering
/// - Cold greeting detection heuristic
///
/// FakeMemoryStore is defined in MemoryRetrievalTests.cs and shared
/// across both files via the same namespace — no redefinition needed.
/// </summary>
public sealed class ShallowMemoryTests
{
    // ─────────────────────────────────────────────────────────────────
    // Domain Model Defaults
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ProfileCard_Defaults_KindIsUserAndJsonEmpty()
    {
        var card = new ProfileCard
        {
            ProfileId   = "test-1",
            DisplayName = "Sample User"
        };

        Assert.Equal("user", card.Kind);
        Assert.Equal("{}", card.ProfileJson);
        Assert.Null(card.Relationship);
        Assert.Null(card.Aliases);
    }

    [Fact]
    public void MemoryNugget_Defaults_WeightAndSensitivityCorrect()
    {
        var nugget = new MemoryNugget
        {
            NuggetId = "nug-1",
            Text     = "Prefers dark mode."
        };

        Assert.Equal(0.65, nugget.Weight);
        Assert.Equal(0, nugget.PinLevel);
        Assert.Equal("low", nugget.Sensitivity);
        Assert.Equal(0, nugget.UseCount);
        Assert.Null(nugget.LastUsedAt);
        Assert.Null(nugget.Tags);
    }

    [Fact]
    public void NuggetSensitivity_Constants_MatchExpectedValues()
    {
        Assert.Equal("low", NuggetSensitivity.Low);
        Assert.Equal("med", NuggetSensitivity.Medium);
        Assert.Equal("high", NuggetSensitivity.High);
    }

    // ─────────────────────────────────────────────────────────────────
    // Shared Parsing — MemoryParsing.ParseSensitivity
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("public",   Sensitivity.Public)]
    [InlineData("Public",   Sensitivity.Public)]
    [InlineData("personal", Sensitivity.Personal)]
    [InlineData("Personal", Sensitivity.Personal)]
    [InlineData("secret",   Sensitivity.Secret)]
    [InlineData("SECRET",   Sensitivity.Secret)]
    [InlineData("unknown",  Sensitivity.Public)]
    [InlineData("",         Sensitivity.Public)]
    [InlineData(null,       Sensitivity.Public)]
    public void ParseSensitivity_ResolvesCorrectly(string? input, Sensitivity expected)
    {
        Assert.Equal(expected, MemoryParsing.ParseSensitivity(input));
    }

    // ─────────────────────────────────────────────────────────────────
    // FakeMemoryStore — Profile + Nugget Operations
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreProfile_RoundTrip_LoadsBackCorrectly()
    {
        var store = new FakeMemoryStore();
        Assert.Null(await store.GetUserProfileAsync());

        var profile = new ProfileCard
        {
            ProfileId   = "prof-1",
            Kind        = "user",
            DisplayName = "Sample User"
        };

        await store.StoreProfileAsync(profile);
        var loaded = await store.GetUserProfileAsync();
        Assert.NotNull(loaded);
        Assert.Equal("Sample User", loaded!.DisplayName);
    }

    [Fact]
    public async Task SearchNuggets_ByKeyword_ReturnsOnlyMatches()
    {
        var store = new FakeMemoryStore();
        store.Nuggets.Add(new MemoryNugget
        {
            NuggetId    = "n1",
            Text        = "User prefers dark mode.",
            Tags        = ";preference;",
            Sensitivity = "low"
        });
        store.Nuggets.Add(new MemoryNugget
        {
            NuggetId    = "n2",
            Text        = "User likes pizza.",
            Tags        = ";preference;",
            Sensitivity = "low"
        });

        var results = await store.SearchNuggetsAsync("dark mode", 5);
        Assert.Single(results);
        Assert.Equal("n1", results[0].NuggetId);
    }

    [Fact]
    public async Task SearchPersonProfiles_MatchesNameAndAliases()
    {
        var store = new FakeMemoryStore();
        store.PersonProfiles.Add(new ProfileCard
        {
            ProfileId    = "p1",
            Kind         = "person",
            DisplayName  = "Dante",
            Relationship = "son",
            Aliases      = ";my son;dante;"
        });

        var hits = await store.SearchPersonProfilesAsync("dante", 1);
        Assert.Single(hits);
        Assert.Equal("Dante", hits[0].DisplayName);

        var miss = await store.SearchPersonProfilesAsync("wife", 1);
        Assert.Empty(miss);
    }

    // ─────────────────────────────────────────────────────────────────
    // Cold Greeting Detection
    // Tested via reflection — the heuristic is private static.
    // ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("hi",          true)]
    [InlineData("hello",       true)]
    [InlineData("hey",         true)]
    [InlineData("yo",          true)]
    [InlineData("good morning", true)]
    [InlineData("Hey there!",  true)]
    [InlineData("hi buddy",   true)]
    [InlineData("What is the meaning of life?", false)]
    [InlineData("hey can you search for the latest news about AI", false)]
    [InlineData("Look at my screen right now", false)]
    public void LooksLikeGreeting_ClassifiesCorrectly(string input, bool expected)
    {
        var method = typeof(Agent.AgentOrchestrator)
            .GetMethod("LooksLikeGreeting",
                System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        var result = (bool)method!.Invoke(null, [input.ToLowerInvariant().Trim()])!;
        Assert.Equal(expected, result);
    }

    // ─────────────────────────────────────────────────────────────────
    // Nugget Retrieval Bounds
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetGreetingNuggets_CapsAtMaxResults()
    {
        var store = new FakeMemoryStore();
        for (var i = 0; i < 10; i++)
        {
            store.Nuggets.Add(new MemoryNugget
            {
                NuggetId    = $"n{i}",
                Text        = $"Fact number {i}",
                Tags        = ";identity;",
                Sensitivity = "low"
            });
        }

        var results = await store.GetGreetingNuggetsAsync(2);
        Assert.True(results.Count <= 2,
            $"Expected at most 2 greeting nuggets, got {results.Count}");
    }

    [Fact]
    public async Task SearchNuggets_CapsAtMaxResults()
    {
        var store = new FakeMemoryStore();
        for (var i = 0; i < 20; i++)
        {
            store.Nuggets.Add(new MemoryNugget
            {
                NuggetId    = $"n{i}",
                Text        = $"Keyword fact {i}",
                Tags        = ";preference;",
                Sensitivity = "low"
            });
        }

        var results = await store.SearchNuggetsAsync("keyword", 5);
        Assert.True(results.Count <= 5,
            $"Expected at most 5 search nuggets, got {results.Count}");
    }

    [Fact]
    public async Task GetGreetingNuggets_ExcludesHighSensitivity()
    {
        var store = new FakeMemoryStore();
        store.Nuggets.Add(new MemoryNugget
        {
            NuggetId    = "safe",
            Text        = "A safe fact",
            Tags        = ";identity;",
            Sensitivity = "low"
        });
        store.Nuggets.Add(new MemoryNugget
        {
            NuggetId    = "secret",
            Text        = "A secret fact",
            Tags        = ";identity;",
            Sensitivity = "high"
        });

        var results = await store.GetGreetingNuggetsAsync(5);
        Assert.All(results, n => Assert.Equal("low", n.Sensitivity));
        Assert.DoesNotContain(results, n => n.NuggetId == "secret");
    }
}
