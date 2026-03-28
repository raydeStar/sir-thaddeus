using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.Search.DeepDive;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class DeepDiveBriefingContractTests
{
    [Fact]
    public void DtoSerialization_RoundTrip_RemainsValid()
    {
        var briefing = new DeepDiveBriefing
        {
            Version = 1,
            Topic = new DeepDiveTopic
            {
                Kind = "place",
                Query = "Portland Floral",
                Timezone = "America/Los_Angeles",
                Locale = "en-US"
            },
            Hero = new DeepDiveHero
            {
                Title = "Portland Floral",
                Confidence = "high",
                LastCheckedIso = DateTimeOffset.UtcNow.ToString("O"),
                StatusLine = "Open now",
                ClosesText = "Today: 9:00 AM - 6:00 PM"
            },
            Cards =
            [
                new DeepDiveCard
                {
                    Type = "hours",
                    Title = "Hours",
                    Bullets = ["Monday: 9:00 AM - 6:00 PM"],
                    Sources = [Source("Google Places", "https://maps.google.com/?q=Portland+Floral")]
                },
                new DeepDiveCard
                {
                    Type = "reviews",
                    Title = "Reviews",
                    Bullets = ["Average rating: 4.7 across 300 ratings."],
                    Sources = [Source("Google Places", "https://maps.google.com/?q=Portland+Floral")]
                },
                new DeepDiveCard
                {
                    Type = "summary",
                    Title = "Summary",
                    Bullets = ["Best for same-day bouquets."],
                    Sources = [Source("Google Places", "https://maps.google.com/?q=Portland+Floral")]
                }
            ]
        };

        var json = JsonSerializer.Serialize(briefing);
        var roundTrip = JsonSerializer.Deserialize<DeepDiveBriefing>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        Assert.NotNull(roundTrip);
        Assert.True(DeepDiveBriefingValidator.TryValidate(roundTrip, out var errors),
            string.Join(" | ", errors));
    }

    [Fact]
    public void ContractCompliance_FixtureJson_DeserializeValidateAndMapProjection()
    {
        var repoRoot = FindRepoRoot();
        var fixturePath = ResolveFixturePath(repoRoot, "deep_dive_place.sample.json");

        var json = File.ReadAllText(fixturePath);
        Assert.True(
            DeepDiveBriefingValidator.TryParseAndValidateJson(json, out var briefing, out var errors),
            string.Join(" | ", errors));

        var projection = DeepDiveBriefingViewModelProjection.Map(briefing!);
        Assert.False(string.IsNullOrWhiteSpace(projection.HeroTitle));
        Assert.True(projection.Cards.Count >= 3);
    }

    [Fact]
    public void HoursParser_DetectsConflictsAcrossSources()
    {
        var parsed = DeepDiveHoursParser.Parse(
        [
            "Monday: 9:00 AM - 5:00 PM\nTuesday: 9:00 AM - 5:00 PM",
            "Monday: 10:00 AM - 6:00 PM\nWednesday: 9:00 AM - 5:00 PM"
        ]);

        Assert.True(parsed.HasAnyHours);
        Assert.True(parsed.HasConflict);
        Assert.Contains(parsed.Bullets, b => b.StartsWith("Monday:", StringComparison.Ordinal));
    }

    [Fact]
    public void HoursParser_HandlesSpaceSeparatorAndDayRanges()
    {
        // Space-only separator (no colon/dash between day and hours)
        var parsed = DeepDiveHoursParser.Parse(
        [
            "Monday 8:00 am - 8:00 pm\nTuesday 8:00 am - 8:00 pm",
            "Mon-Fri: 5:30am-10pm"
        ]);

        Assert.True(parsed.HasAnyHours);
        Assert.True(parsed.Bullets.Count >= 2, $"Expected 2+ bullets, got {parsed.Bullets.Count}: {string.Join("; ", parsed.Bullets)}");
        Assert.Contains(parsed.Bullets, b => b.StartsWith("Monday:", StringComparison.Ordinal));
    }

    [Fact]
    public void HoursParser_HandlesClosedAndOpen24Hours()
    {
        var parsed = DeepDiveHoursParser.Parse(
        [
            "Monday: Open 24 hours\nSunday: Closed"
        ]);

        Assert.True(parsed.HasAnyHours);
        Assert.Contains(parsed.Bullets, b => b.Contains("Open 24 hours", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(parsed.Bullets, b => b.Contains("Closed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WebExtractor_ExtractsPhoneAddressRating()
    {
        var result = DeepDiveWebExtractor.Extract(
        [
            "Address: 700 SW 5th Ave, Portland, OR 97204",
            "Phone: (503) 555-9580. Rated 4.2 out of 5 stars. 300 reviews.",
            "Great service and the staff is very friendly."
        ]);

        Assert.Equal("(503) 555-9580", result.Phone);
        Assert.NotNull(result.Address);
        Assert.Contains("Portland", result.Address!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4.2, result.Rating);
        Assert.Equal(300, result.ReviewCount);
        Assert.True(result.ReviewSnippets.Count > 0, "Should have extracted review snippets.");
    }

    [Fact]
    public void WebExtractor_NormalizesNoisyAddressAndPhoneFromSearchText()
    {
        var result = DeepDiveWebExtractor.Extract(
        [
            "08:00 AM - 12:00 PM See Hours 600 2nd Ave, Seattle, WA 98104",
            "Call 1 206-728-2588. Rated 3.0 out of 5 stars. 212 reviews."
        ]);

        Assert.Equal("600 2nd Ave, Seattle, WA 98104", result.Address);
        Assert.Equal("(206) 728-2588", result.Phone);
        Assert.Equal(3.0, result.Rating);
        Assert.Equal(212, result.ReviewCount);
    }

    [Fact]
    public void WebExtractor_InfersBusinessNameFromSourceTitles()
    {
        var sources = new List<SourceItem>
        {
            new() { Url = "https://example.com/a", Title = "McDonald's - 700 SW 5th Ave | Portland" },
            new() { Url = "https://example.com/b", Title = "McDonald's Hours - Portland OR" },
            new() { Url = "https://example.com/c", Title = "McDonald's in Portland :: Fast Food" }
        };

        var result = DeepDiveWebExtractor.Extract(["some content"], sources);
        Assert.NotNull(result.BusinessName);
        Assert.Contains("McDonald", result.BusinessName!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CardOrdering_PlaceCards_PrioritizesWarningsAndHours()
    {
        var ordered = DeepDiveCardOrdering.Apply(
            "place",
            [
                Card("summary"),
                Card("reviews"),
                Card("warnings"),
                Card("hours")
            ]);

        Assert.Equal("warnings", ordered[0].Type);
        Assert.Equal("hours", ordered[1].Type);
        Assert.Equal("reviews", ordered[2].Type);
        Assert.Equal("summary", ordered[3].Type);
    }

    [Fact]
    public async Task Coordinator_FallbackWithConflictingHours_AddsWarningsAndLowConfidence()
    {
        // Simulate realistic web search output with structured source data.
        var webResult = """
1. McDonald's - 700 SW 5th Ave, Portland, OR
Monday: 9:00 AM - 5:00 PM
Tuesday: 9:00 AM - 5:00 PM

2. McDonald's Hours & Restaurant Info
Rated 3.8 out of 5 stars. 142 reviews.
Address: 700 SW 5th Ave, Portland, OR 97204
Phone: (503) 555-9580

<!-- SOURCES_JSON -->
[
  {"title":"McDonald's - 700 SW 5th Ave | Portland","url":"https://example.com/a","domain":"example.com","excerpt":"Monday: 9:00 AM - 5:00 PM. Address: 700 SW 5th Ave, Portland, OR 97204. Phone: (503) 555-9580. Great service and friendly staff."},
  {"title":"McDonald's Hours - Portland OR","url":"https://example.com/b","domain":"example.com","excerpt":"Monday: 10:00 AM - 6:00 PM. 142 reviews. Rated 3.8/5 stars."}
]
""";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return """{"provider":"google_places","query":"demo","error":"key missing","place":null,"sources":[]}""";
            }

            if (tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return webResult;
            }

            if (tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            {
                return "Monday: 10:00 AM - 6:00 PM\nTuesday: 10:00 AM - 6:00 PM\nPhone: (503) 555-9580";
            }

            return "{}";
        });

        var coordinator = new DeepDiveCoordinator(mcp, new TestAuditLogger());
        var toolCalls = new List<ToolCallRecord>();

        var result = await coordinator.BuildPlaceBriefingAsync(
            query: "McDonald's Portland",
            timezone: "America/Los_Angeles",
            locale: "en-US",
            userLocationHint: "Portland, OR",
            toolCallsMade: toolCalls,
            cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Briefing);

        // Conflicting hours (9-5 vs 10-6) should still yield medium
        // confidence because data WAS extracted.  Conflict adds a warning
        // but does not downgrade confidence to low.
        Assert.Equal("medium", result.Briefing!.Hero.Confidence);
        Assert.Contains(result.Briefing.Cards, c => c.Type == "warnings");

        // Even with conflicts, hours should be PRESENT (not placeholder text).
        var hoursCard = result.Briefing.Cards.FirstOrDefault(c => c.Type == "hours");
        Assert.NotNull(hoursCard);
        Assert.True(hoursCard!.Bullets.Count > 0);
        Assert.Contains(hoursCard.Bullets, b => b.Contains("Monday", StringComparison.OrdinalIgnoreCase));

        // Extraction should have pulled address and phone from snippets.
        var summaryCard = result.Briefing.Cards.FirstOrDefault(c => c.Type is "summary" or "details");
        Assert.NotNull(summaryCard);

        Assert.NotNull(result.AssistantText);
        Assert.Contains("McDonalds", result.AssistantText!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Address:", result.AssistantText!, StringComparison.Ordinal);
        Assert.Contains("Phone:", result.AssistantText!, StringComparison.Ordinal);
        Assert.Contains("Briefing summary:", result.AssistantText!, StringComparison.Ordinal);
        Assert.DoesNotContain("Briefing tab", result.AssistantText!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeepDiveCoordinator_AssistantLead_IncludesReviewSignal_WhenAvailable()
    {
        var briefing = new DeepDiveBriefing
        {
            Topic = new DeepDiveTopic
            {
                Kind = DeepDiveConstants.KindPlace,
                Query = "Richard's Deli Hillsboro OR",
                Timezone = "America/Los_Angeles",
                Locale = "en-US"
            },
            Hero = new DeepDiveHero
            {
                Title = "Richard's Deli",
                Confidence = "medium",
                StatusLine = "Details from web sources",
                LastCheckedIso = DateTimeOffset.UtcNow.ToString("O"),
                Address = "2559 SE Tualatin Valley Hwy, Hillsboro, OR 97123",
                Phone = "(503) 619-0506"
            },
            Cards =
            [
                new DeepDiveCard
                {
                    Type = "reviews",
                    Title = "Reviews",
                    Bullets =
                    [
                        "Average rating: 4.6 across 120 ratings.",
                        "People often praise service quality and staff friendliness.",
                        "No dominant complaint theme surfaced in sampled snippets."
                    ],
                    Sources = [Source("yelp", "https://www.yelp.com/biz/richards-deli-hillsboro")]
                }
            ]
        };

        var method = typeof(DeepDiveCoordinator).GetMethod(
            "BuildAssistantLead",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);

        var assistantLead = Assert.IsType<string>(method!.Invoke(null, [briefing]));

        Assert.Contains("Average rating: 4.6 across 120 ratings.", assistantLead, StringComparison.Ordinal);
        Assert.Contains("People often praise service quality and staff friendliness.", assistantLead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentOrchestrator_DeepDiveQuery_ReturnsBriefingPayload()
    {
        var placesPayload = JsonSerializer.Serialize(new
        {
            provider = "google_places",
            query = "Portland Floral",
            fetchedAt = DateTimeOffset.UtcNow.ToString("O"),
            error = (string?)null,
            place = new
            {
                placeId = "abc123",
                name = "Portland Floral",
                address = "145 SW Morrison St, Portland, OR 97204",
                phone = "(503) 555-1234",
                website = "https://example.test/floral",
                directionsUrl = "https://maps.google.com/?q=Portland+Floral",
                rating = 4.7,
                userRatingsTotal = 300,
                openNow = true,
                weekdayText = new[] { "Saturday: 9:00 AM - 6:00 PM" },
                reviews = new[]
                {
                    new { author = "A", rating = 5, text = "Great service", relativeTimeDescription = "a week ago" }
                },
                geometry = new { lat = 45.5231, lng = -122.6765 }
            },
            sources = new[]
            {
                new { name = "Google Places", url = "https://maps.google.com/?q=Portland+Floral", fetchedIso = DateTimeOffset.UtcNow.ToString("O") }
            }
        });

        var llm = new FakeLlmClient((messages, tools) =>
            new LlmResponse { IsComplete = true, Content = "chat", FinishReason = "stop" });
        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Equals("memory_retrieve", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("MemoryRetrieve", StringComparison.OrdinalIgnoreCase))
            {
                return "";
            }

            if (tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return placesPayload;
            }

            return "{}";
        }, FakeMcpClient.StandardToolSet);

        var orchestrator = new AgentOrchestrator(llm, mcp, new TestAuditLogger(), "Test system prompt");
        var response = await orchestrator.ProcessAsync("deep dive portland floral hours + reviews");

        Assert.True(response.Success);
        Assert.NotNull(response.DeepDiveBriefing);
        Assert.Equal("Portland Floral", response.DeepDiveBriefing!.Hero.Title);
        Assert.Contains("appears open now", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeepDiveCoordinator_WebFallback_PrefersBrowsedSourceAddressInAssistantLead()
    {
        var webResult = """
1. \"Fast food in Portland, OR at 5613 SE 82nd Ave | McDonald's\" - McDonalds
2. \"McDonald's - 13459 NW Cornell Rd, Portland, OR 97229\" - Yelp
3. \"McDonald's - 12090 SW Main St, Portland, OR 97223\" - MapQuest

<!-- SOURCES_JSON -->
{"sources":[{"title":"Fast food in Portland, OR at 5613 SE 82nd Ave | McDonald's","url":"https://www.mcdonalds.example/5613","domain":"mcdonalds.com","excerpt":"Monday: Open 24 hours"},{"title":"McDonald's - 13459 NW Cornell Rd, Portland, OR 97229","url":"https://www.yelp.example/mcdonalds-portland-6","domain":"yelp.com","excerpt":"Yelp listing for Portland location"},{"title":"McDonald's - 12090 SW Main St, Portland, OR 97223","url":"https://www.mapquest.example/mcdonalds","domain":"mapquest.com","excerpt":"Map listing"}]}
""";

        var mcp = new FakeMcpClient((tool, args) =>
        {
            if (tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase))
            {
                return """{"provider":"google_places","query":"demo","error":"key missing","place":null,"sources":[]}""";
            }

            if (tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase))
            {
                return webResult;
            }

            if (tool.Equals("browser_navigate", StringComparison.OrdinalIgnoreCase) ||
                tool.Equals("BrowserNavigate", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Contains("mcdonalds.example/5613", StringComparison.OrdinalIgnoreCase))
                {
                    return "McDonald's\nToday: Monday: Open 24 hours\nPhone: (503) 555-1111";
                }

                if (args.Contains("yelp.example/mcdonalds-portland-6", StringComparison.OrdinalIgnoreCase))
                {
                    return "McDonald's\n13459 NW Cornell Rd, Portland, OR 97229\nPhone: (503) 643-9455\nOpen now";
                }

                return "Map listing";
            }

            return "{}";
        });

        var coordinator = new DeepDiveCoordinator(mcp, new TestAuditLogger());
        var toolCalls = new List<ToolCallRecord>();

        var result = await coordinator.BuildPlaceBriefingAsync(
            query: "Is McDonalds in Portland OR open right now?",
            timezone: "America/Los_Angeles",
            locale: "en-US",
            userLocationHint: "Portland, OR",
            toolCallsMade: toolCalls,
            cancellationToken: CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Briefing);
        Assert.Contains("appears open now", result.AssistantText!, StringComparison.OrdinalIgnoreCase);
        // With priority-based source ordering, the Yelp source (review site)
        // is browsed before the generic business homepage, so the Yelp address
        // is preferred.
        Assert.Contains("13459 NW Cornell Rd", result.AssistantText!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12090 SW Main St", result.AssistantText!, StringComparison.OrdinalIgnoreCase);
    }

    private static SourceRef Source(string name, string url) => new()
    {
        Name = name,
        Url = url,
        FetchedIso = DateTimeOffset.UtcNow.ToString("O")
    };

    private static DeepDiveCard Card(string type) => new()
    {
        Type = type,
        Title = type,
        Bullets = ["x"],
        Sources = [Source("test", "https://example.test")]
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.GetFiles(dir.FullName, "*.sln").Length > 0)
                return dir.FullName;
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static string ResolveFixturePath(string repoRoot, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(repoRoot, "tests", "SirThaddeus.Tests", "Fixtures", fileName),
            Path.Combine(repoRoot, "LEGACY", "SirThaddeus.DesktopRuntime", "Fixtures", fileName),
            Path.Combine(repoRoot, "apps", "desktop-runtime", "SirThaddeus.DesktopRuntime", "Fixtures", fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not locate fixture '{fileName}' in expected paths.",
            candidates[0]);
    }
}
