using System.Net;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.Tests;

public sealed class ToolBackedResponseQualityGuardsTests
{
    [Fact]
    public void LocationAwarePlacesArgsRewriter_WhenPlacesDiscoverUsesNearMe_AddsConfiguredLocationHint()
    {
        var rewriter = new LocationAwarePlacesArgsRewriter(() => "Olympia, WA");
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "Is there a florist nearby?"
        };

        var rewritten = rewriter.Rewrite(
            context,
            ToolNames.PlacesDiscover,
            "{\"query\":\"florist near me\"}");

        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal("Olympia, WA", document.RootElement.GetProperty("userLocationHint").GetString());
    }

    [Fact]
    public void LocationAwarePlacesArgsRewriter_WhenConcreteHintExists_PreservesIt()
    {
        var rewriter = new LocationAwarePlacesArgsRewriter(() => "Olympia, WA");
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "Is there a florist nearby?"
        };

        var rewritten = rewriter.Rewrite(
            context,
            ToolNames.PlacesDiscover,
            "{\"query\":\"florist\",\"userLocationHint\":\"Portland, OR\"}");

        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal("Portland, OR", document.RootElement.GetProperty("userLocationHint").GetString());
    }

    [Fact]
    public async Task OsmPlacesDiscover_WhenQueryUsesNearMeWithoutLocation_ReturnsMissingLocation()
    {
        using var provider = new OsmPlacesDiscoveryProvider(new HttpClient(new ThrowingHandler()));

        var result = await provider.DiscoverAsync("florist near me", userLocationHint: null);

        Assert.Equal("florist near me", result.Query);
        Assert.Equal(string.Empty, result.UserLocationHint);
        Assert.Equal(string.Empty, result.ResolvedLocation);
        Assert.Contains(result.Errors, error => error.Contains("location hint is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_WhenPlacesDiscoverRetryHasLocation_ReplacesStaleNoLocationEvidence()
    {
        const string staleResponse = """
            I could not confirm a trustworthy florist from this live lookup.
            The places_discover lookup (osm_overpass) checked "florist nearby" without a resolved location, but it did not return a trustworthy florist match I can recommend.
            Sources checked: places_discover/Open Places.
            Best next step: verify in the official store locator, maps listing, or by phone before visiting.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            staleResponse,
            "Is there a florist nearby?",
            [
                new ToolCallRecord
                {
                    ToolName = "places_discover",
                    Arguments = "{\"query\":\"florist nearby\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"florist nearby\",\"userLocationHint\":\"\",\"resolvedLocation\":\"\",\"results\":[]}",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = "places_discover",
                    Arguments = "{\"query\":\"florist near Olympia, WA\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"florist near Olympia, WA\",\"userLocationHint\":\"Olympia, WA\",\"resolvedLocation\":\"Olympia, Washington, US\",\"results\":[]}",
                    Success = true
                }
            ]);

        Assert.Contains("florist near Olympia, WA", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Olympia, Washington, US", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("without a resolved location", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenPlacesDiscoverAnswerOmitsSourceDetails_AppendsLookupEvidence()
    {
        const string goodButUngroundedResponse = "I found four florist candidates near Olympia, WA: The Popinjay Flower and Gift Shop, Fleure Floral Design, Capitol Florist, and Artistry in Flowers.";

        var response = ToolBackedResponseQualityGuards.Apply(
            goodButUngroundedResponse,
            "Is there a florist nearby?",
            [
                new ToolCallRecord
                {
                    ToolName = "places_discover",
                    Arguments = "{\"query\":\"florist near me\",\"userLocationHint\":\"Olympia, WA\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"florist near me\",\"userLocationHint\":\"Olympia, WA\",\"resolvedLocation\":\"Olympia, Washington, US\",\"results\":[]}",
                    Success = true
                }
            ]);

        Assert.Contains("places_discover/osm_overpass", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("florist near me", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Olympia, Washington, US", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessSourceIsArticleTitle_DoesNotPresentItAsCandidate()
    {
        const string shellResponse = """
            Sources checked: web_search.
            Briefing summary: details from web sources. Verification recommended.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            shellResponse,
            "Can you recommend a good florist in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"a good florist in Hillsboro, OR\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/flower-subscription","title":"7 Flower Subscription Services for a Gift That Keeps on Giving","domain":"example.com","snippet":"A gift guide, not a local florist listing."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy florist in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plausible florist", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7 Flower Subscription Services", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessSourceMentionsDifferentCity_DoesNotPresentItAsCandidate()
    {
        const string shellResponse = """
            Sources checked: web_search.
            Briefing summary: details from web sources. Verification recommended.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            shellResponse,
            "Can you recommend a good florist in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"a good florist in Hillsboro, OR\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/perth-florists","title":"Perth's best florists (who deliver!)","domain":"example.com","snippet":"A guide to flower delivery in Perth, Australia."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy florist in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plausible florist", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Perth", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessPlacesOnlyCandidateListMakesQualityClaims_ReturnsConservativeFallback()
    {
        const string weakResponse = """
            It appears I have found a few options for delis in Hillsboro, Oregon. Since you asked for a "good" one, here is a comparison to help you decide:

            * **Carniceria Vendura Deli:** Located about 1300 meters away from the center point. (Reviews indicate high quality meats and authentic offerings.)
            * **Max's Deli:** This one is located at 460 Southeast 10th Avenue, Hillsboro, 97123, and is about 1382 meters away. (Generally well-rated for quick service and classic deli sandwiches.)
            * **Isabella's Deli:** Found roughly 1431 meters from the center point. (Often praised in local reviews for its unique selection of cheeses and cold cuts.)

            ***
            *Sir Thaddeus*

            Lookup details: places_discover/osm_overpass; query="deli in Hillsboro, OR"; resolved location=Hillsboro, Oregon, US.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you find me a good deli in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesDiscover,
                    Arguments = "{\"query\":\"deli in Hillsboro, OR\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"deli in Hillsboro, OR\",\"resolvedLocation\":\"Hillsboro, Oregon, US\",\"results\":[{\"name\":\"Max's Deli\",\"distanceMeters\":1400}]}",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy deli in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("places_discover lookup", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("highly rated", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("good customer service", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessListsInitialPlacesCandidatesAfterFailedDetails_ReturnsConservativeFallback()
    {
        const string weakResponse = """
            It appears there are several delis listed in Hillsboro, Oregon. Since you asked for a "good" one, I suggest we look up the details-like ratings or hours-for a few of these to help you decide.

            I see three candidates from this initial search: Carniceria Vendura Deli, Max's Deli, and Isabella's Deli.

            Lookup details: places_discover/osm_overpass; query="deli in Hillsboro, OR"; resolved location=Hillsboro, Oregon, US.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you find me a good deli in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesDiscover,
                    Arguments = "{\"query\":\"deli in Hillsboro, OR\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"deli in Hillsboro, OR\",\"resolvedLocation\":\"Hillsboro, Oregon, US\",\"results\":[{\"name\":\"Max's Deli\",\"distanceMeters\":1400}]}",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"Max's Deli Hillsboro OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best deli in Hillsboro OR\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy deli in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("places_discover lookup", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_search", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("candidates from this initial search", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessNamesOnlyResponseAsksToFetchMoreDetails_ReturnsConservativeFallback()
    {
        const string weakResponse = """
            Well now, I have located a few establishments categorized as delis within Hillsboro, Oregon. Since you asked for a "good" one, which implies quality or reputation, the initial list from my search gives me names-Carniceria Vendura Deli, Max's Deli, and Isabella's Deli-but it doesn't furnish me with reviews or operating hours to properly rank them for you.

            To give you better guidance, would you prefer I fetch more details on one of these specific delis, such as their current hours or customer ratings?

            Best regards,
            Sir Thaddeus

            Lookup details: places_discover/osm_overpass; query="deli in Hillsboro, OR"; resolved location=Hillsboro, Oregon, US.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you find me a good deli in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesDiscover,
                    Arguments = "{\"query\":\"deli in Hillsboro, OR\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"deli in Hillsboro, OR\",\"resolvedLocation\":\"Hillsboro, Oregon, US\",\"results\":[{\"name\":\"Max's Deli\",\"distanceMeters\":1400}]}",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best deli in Hillsboro OR\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy deli in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would you prefer", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Best regards", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessPlacesOnlyOptionsAdmitMissingReviews_ReturnsConservativeFallback()
    {
        const string weakResponse = """
            It seems you are looking for a good deli in Hillsboro, Oregon. I have found three candidates near that area based on the initial search results.

            The options presented are:
            1. **Carniceria Vendura Deli:** Located about 1.3 km away.
            2. **Max's Deli:** Located at 460 Southeast 10th Avenue, roughly 1.4 km away.
            3. **Isabella's Deli:** Located approximately 1.4 km away.

            Since I only have the general location and not reviews or operating hours yet, if you would like me to check ratings, specific menus, or opening times for any of these places, I can perform a more detailed lookup for you.
            Lookup details: places_discover/osm_overpass; resolved location=Hillsboro, Oregon, US.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you find me a good deli in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesDiscover,
                    Arguments = "{\"query\":\"deli in Hillsboro, OR\"}",
                    Result = "{\"provider\":\"osm_overpass\",\"query\":\"deli in Hillsboro, OR\",\"resolvedLocation\":\"Hillsboro, Oregon, US\",\"results\":[{\"name\":\"Max's Deli\",\"distanceMeters\":1400}]}",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy deli in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("options presented", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check ratings", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalBusinessClaimsLocalConsensusWithoutEvidence_ReturnsConservativeFallback()
    {
        const string weakResponse = "Based on local consensus and available listings, I can suggest **Pilsbry's Deli** as a well-regarded option in Hillsboro, OR, for your deli needs. If you are looking for something different or have specific preferences, let me know.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you find me a good deli in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"best deli in Hillsboro, OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best deli in Hillsboro, OR reviews\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a trustworthy deli in Hillsboro, OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local consensus", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("well-regarded", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenOpenStatusClaimHasNoTrustedHoursEvidence_ReplacesWithConservativeFallback()
    {
        const string weakResponse = "Walmart in Portland, OR is open until 10:00 PM today. Walmart in Portland, OR is currently open and its operating hours for today are until 10:00 PM.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Is Walmart in Portland OR open right now?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"Walmart Portland OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Walmart Portland OR hours today\",\"recency\":\"day\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://www.walmart.com/store/finder?location=Portland%2C%20OR\"}",
                    Result = "[browser: Title: \"Walmart Stores Near Me -  Locations, Hours & Services\", content returned]",
                    Success = true
                }
            ]);

        Assert.Contains("could not confirm whether Walmart in Portland OR is open right now", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("places_lookup/Google Places could not provide a usable current-hours result", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_search for \"Walmart Portland OR hours today\" returned 0 results", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Walmart Stores Near Me", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sources checked", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10:00 PM", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("currently open", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMcDonaldsOpenStatusFallbackIsNeeded_PreservesUserBrandSpelling()
    {
        const string weakResponse = "McDonald's in Portland OR is currently open until 10:00 PM today. Sources checked: time.is, 24timezones.com.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Is McDonalds in Portland OR open right now?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"McDonalds Portland OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"McDonald's opening hours Portland Oregon today\"}",
                    Result = "[search: 3 result(s) returned]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = "browser_navigate",
                    Arguments = "{\"url\":\"https://www.opentable.com/higgins-restaurant-and-bar\"}",
                    Result = "[browser: (no title), content returned]",
                    Success = true
                }
            ]);

        Assert.Contains("McDonalds in Portland OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sources checked: places_lookup/Google Places, web_search, browser_navigate", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Use the McDonalds store finder", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("McDonald's's", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("time.is", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("24timezones", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenOpenStatusAnswerIsUnconfirmed_PreservesUserBrandSpelling()
    {
        const string weakResponse = "None of the results provided a definitive open now status for any specific McDonald's location. I would need a more specific address.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Is McDonalds in Portland OR open right now?",
            [
                new ToolCallRecord
                {
                    ToolName = "places_lookup",
                    Arguments = "{\"query\":\"McDonalds in Portland OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"current operating hours for McDonald's in Portland Oregon\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("McDonalds in Portland OR", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not confirm", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("places_lookup", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMediaInstallmentFallbackReturnsCancelled_ReplacesWithNonexistenceAnswer()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "Cancelled",
            "What would be the plot of Episode 1 of Season 3 of Stargate Universe about?",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Stargate Universe Season 3 Episode 1 plot\"}",
                    Result = "[search: 2 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Stargate Universe", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled after Season 2", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no real episode plot", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMediaInstallmentAnswerSummarizesReboot_ReplacesWithNonexistenceAnswer()
    {
        const string weakResponse = "I dont have information regarding the specific plot details for Episode 1 of Season 3 of Stargate Universe. The search results only confirm a new Stargate TV series ordered by Amazon MGM Studios for Prime Video.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What would be the plot of Episode 1 of Season 3 of Stargate Universe about?",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Stargate Universe Season 3 Episode 1 plot\"}",
                    Result = "[search: 4 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Stargate Universe", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancelled after Season 2", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no real episode plot", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Amazon MGM", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenKnowledgeStoreListRootsLeaksToolCall_ReplacesWithRootSummary()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "<|tool_call>call:knowledge_store_list_roots{}<tool_call|>",
            "Call knowledge_store_list_roots and report the configured root id and display name.",
            [
                new ToolCallRecord
                {
                    ToolName = "knowledge_store_list_roots",
                    Arguments = "{}",
                    Result = "{\"ok\":true,\"enabled\":true,\"roots\":[{\"id\":\"harness\",\"display_name\":\"Harness Knowledge Store\",\"absolute_path\":\"C:\\\\Temp\\\\ks\"}]}",
                    Success = true
                }
            ]);

        Assert.Contains("root id is harness", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display name is Harness Knowledge Store", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_call", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenHoursLookupOffersAnotherSearch_ReturnsConservativeHoursFallback()
    {
        const string weakResponse = "The search results I found primarily discuss past closures or holiday hours for Trader Joe's in Portland, Oregon. I regret that these results do not provide a general, current opening time for a specific store right now; I would need a more direct source. Would you like me to try another search focusing specifically on Trader Joe's official hours Portland OR?";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What are the hours for Trader Joe's in Portland, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Trader Joe's Portland OR hours\"}",
                    Result = "[search: 3 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm operating hours", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Trader Joe", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Portland", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Would you like me", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("past closures", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStrictDotNetLatestVersionAnswerIsNoisy_ReturnsExactlyTwoLines()
    {
        const string weakResponse = """
            Answer: I do not have a definitive answer for the latest stable version of .NET as of 2025 based on the search results provided.
            Commentary: The recent search query returned an unrelated article regarding UGC NET applications, indicating that no specific information about .NET versions was found in the immediate results.

            ***
            Sir Thaddeus
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What is the latest stable version of .NET as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"latest stable version of .NET 2025\"}",
                    Result = "[search: 1 result(s) returned]",
                    Success = true
                }
            ]);

        var lines = response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, lines.Length);
        Assert.StartsWith("Answer:", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("Commentary:", lines[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET 10", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unrelated", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenProductRecommendationDeflectsAfterSearch_ReturnsEvidenceGroundedChecklist()
    {
        const string weakResponse = "It seems I encountered a bit of a snag trying to pull up specific recommendations for Ashwagandha on Amazon.com at this moment; the search returned no direct matches.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you recommend a good Ashwagandha on Amazon.com?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best ashwagandha supplement amazon\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best Ashwagandha supplement reviews\"}",
                    Result = "[search: 3 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("single best ashwagandha supplement on Amazon", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("web_search for \"best ashwagandha supplement amazon\" returned 0 results", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("third-party testing", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snag", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenProductRecommendationAsksForUserGoalAfterSearch_ReturnsEvidenceGroundedChecklist()
    {
        const string weakResponse = "To give you a truly helpful suggestion, I would need to know what you are hoping to achieve with Ashwagandha. Once you clarify your goal, I can try a more targeted search.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you recommend a good Ashwagandha on Amazon.com?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best ashwagandha supplement amazon\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a single best ashwagandha supplement on Amazon", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would need to know", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenProductRecommendationSaysSearchYieldedNoListings_ReturnsChecklistFallback()
    {
        const string weakResponse = """
            It appears my immediate search for a specific recommendation on Amazon.com did not yield any direct product listings. Finding the best supplement is rather subjective, as what works well for one individual may be less effective or appropriate for another; it truly depends on what you are hoping to achieve with it.

            If you'd like me to perform a broader search for reviews comparing various brands, please let me know.

            ***
            Sir Thaddeus
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you recommend a good Ashwagandha on Amazon.com?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best ashwagandha supplement amazon\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm a single best ashwagandha supplement on Amazon", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("buying checklist", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("please let me know", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMovieComparisonDeflectsAfterSearch_ReturnsConservativeAnswer()
    {
        const string weakResponse = "My current search did not immediately yield a direct comparison. Would you like me to try a broader search? Yours in clear assessment, Sir Thaddeus.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Is the new live-action How to Train Your Dragon word for word the same as the original?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"How to Train Your Dragon live action word for word same as original\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.StartsWith("No.", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("word-for-word identity is not established", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Would you like me", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMovieComparisonSaysNothingConcrete_ReturnsConservativeAnswer()
    {
        const string weakResponse = "It appears the search results did not bring back a direct comparison detailing whether the live-action How to Train Your Dragon is word for word like the original animated movies. I recommend we try a broader search or perhaps narrow down what aspect interests you most. I await your direction on how to proceed.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Is the new live-action How to Train Your Dragon word for word the same as the original?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"How to Train Your Dragon live action word for word same as original\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.StartsWith("No.", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("word-for-word identity is not established", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("broader search", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("await your direction", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStructuredSearchGetsNoResults_DoesNotAskPermission()
    {
        const string weakResponse = "Since I must gather live evidence before synthesizing this information, I shall broaden my approach. Would you permit me to execute a broader search? Once we have gathered sufficient material, I shall structure it precisely as requested.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Search for recent updates and developments in .NET Aspire from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"recent updates and developments in .NET Aspire last year\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\".NET Aspire recent updates developments 2024 2025 microsoft official\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Overview:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Common Points:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Differences:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Practical Takeaway:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Would you permit", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStructuredSearchHasSingleSnippetFollowup_ReplacesWithDeterministicStructure()
    {
        const string weakResponse = "It looks like youre looking for a comprehensive overview. Since I only have one snippet here, I will use that. Let me know if youd like me to check for more recent releases.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Search for recent updates and developments in .NET Aspire from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\".NET Aspire recent updates and developments last year\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/aspire-release","title":".NET Aspire 13.2 Released with Expanded CLI, TypeScript AppHost Preview, and Dashboard Improvements","domain":"example.com","snippet":"The release expands CLI functionality, previews TypeScript AppHost support, and improves the dashboard."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("Overview:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Common Points:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Differences:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Practical Takeaway:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("only have one snippet", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Let me know", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The available source points", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalNewsNoResultsDeflects_ReturnsEvidenceFallback()
    {
        const string weakResponse = "It seems my attempt to fetch live news for Boise, ID, yielded no immediate results. I will await further instruction. _Sir Thaddeus_";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "I am in a hurry; give me local news for Boise, ID.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"local news Boise ID\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Local news in Boise, ID", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("did not return a trustworthy local headline set", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("await further instruction", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLocalNewsNoResultsAddsFarewellChatter_ReturnsEvidenceFallback()
    {
        const string weakResponse = "Regarding the news in Boise, ID, my search did not yield any specific live results for that area at this moment. It seems the current information streams are quiet on that particular topic or location. Farewell for now.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "I am in a hurry; give me local news for Boise, ID.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"local news Boise ID\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Local news in Boise, ID", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Farewell", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("current information streams", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenCurrentTimeAnswerIgnoresTimeNow_ReplacesWithDeterministicTime()
    {
        const string weakResponse = "The current time in Tokyo, Japan is Tuesday, May 7, 2026, at 10:57 AM local time.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What time is it in Tokyo, Japan right now?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WeatherGeocode,
                    Arguments = "{\"location\":\"Tokyo, Japan\"}",
                    Result = "[Weather geocode: 3 result(s), source=photon]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.ResolveTimezone,
                    Arguments = "{\"countryCode\":\"JP\",\"latitude\":35.6768601,\"longitude\":139.7638947}",
                    Result = "[Timezone lookup: timezone=Asia/Tokyo, source=open-meteo]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.TimeNow,
                    Arguments = "{}",
                    Result = "{\"iso\":\"2026-05-07T20:35:48.9491441-06:00\",\"unix_ms\":1778207748949,\"timezone\":\"Mountain Standard Time\",\"offset\":\"-06:00\"}",
                    Success = true
                }
            ]);

        Assert.Contains("11:35 AM", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Friday, May 8, 2026", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tokyo, Japan", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time_now", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenCurrentTimeAnswerSkipsTimeNow_UsesResolvedTimezoneAndSystemClock()
    {
        const string weakResponse = "The current time in Tokyo, Japan is determined by the `Asia/Tokyo` timezone. To give you the precise moment, I would need to check a live clock for that zone.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What time is it in Tokyo, Japan right now?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WeatherGeocode,
                    Arguments = "{\"location\":\"Tokyo, Japan\"}",
                    Result = "[Weather geocode: 3 result(s), source=photon]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.ResolveTimezone,
                    Arguments = "{\"countryCode\":\"JP\",\"latitude\":35.6768601,\"longitude\":139.7638947}",
                    Result = "[Timezone lookup: timezone=Asia/Tokyo, source=open-meteo]",
                    Success = true
                }
            ]);

        Assert.Contains("Tokyo, Japan", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source=photon", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("clock=system UTC", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("would need to check", response, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Unexpected HTTP call", null, HttpStatusCode.InternalServerError);
    }
}