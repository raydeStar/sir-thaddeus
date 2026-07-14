using System.Net;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.WebSearch.Providers;

namespace SirThaddeus.Tests;

public sealed class ToolBackedResponseQualityGuardsTests
{
    [Fact]
    public void FailedMemoryProvenance_DoesNotEnableToolBackedRewrites()
    {
        const string draft = "I may need a more direct source before answering.";
        var response = ToolBackedResponseQualityGuards.Apply(
            draft,
            "Put the final answer on its own line. Example: cars at a dealership near a park.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.MemoryRetrieve,
                    Arguments = "{}",
                    Success = false,
                    Result = "Memory retrieval suppressed by the active tool contract."
                }
            ]);

        Assert.Equal(draft, response);
    }

    [Fact]
    public void CapabilityManifestSummary_ReplacesUngroundedDraftWithToolDerivedGroups()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "Today's date is Sunday.",
            "Call tool_list_capabilities and summarize the capability groups.",
            [
                new ToolCallRecord
                {
                    ToolName = "tool_list_capabilities",
                    Arguments = "{}",
                    Success = true,
                    Result = "[tool_list_capabilities: 4 tool(s); capability groups: file, memory, meta, web; " +
                             "tools: file_read, memory_retrieve, tool_ping, web_search]"
                }
            ]);

        Assert.Contains("Available capability groups (4 tools): file, memory, meta, web", response, StringComparison.Ordinal);
        Assert.Contains("web_search", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Today's date", response, StringComparison.Ordinal);
    }

    [Fact]
    public void CapabilityManifestSummary_ParsesRawManifestWithoutHardCodedGroups()
    {
        const string manifest = """
            [
              {"name":"alpha_read","category":"alpha"},
              {"name":"beta_write","category":"beta"}
            ]
            """;
        var response = ToolBackedResponseQualityGuards.Apply(
            "unrelated draft",
            "Summarize the available capability groups.",
            [
                new ToolCallRecord
                {
                    ToolName = "tool_list_capabilities",
                    Arguments = "{}",
                    Success = true,
                    Result = manifest
                }
            ]);

        Assert.Equal(
            "Available capability groups (2 tools): alpha, beta. Representative tools include: alpha_read, beta_write.",
            response);
    }

    [Fact]
    public void RawTransportFailureWithoutSourceDetails_ReturnsSafeToolSynthesisFallback()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "LLM returned 400 (Bad Request): n_keep >= n_ctx",
            "Search for a recent technology headline.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"recent technology headline\"}",
                    Success = true,
                    Result = "[search: 1 result(s) returned]"
                }
            ]);

        Assert.Contains("ran web_search", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("could not safely synthesize", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LLM returned", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("n_keep", response, StringComparison.OrdinalIgnoreCase);
    }

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
    public void ExistenceSearchArgsRewriter_WhenReleasedProductExistenceRequest_UsesAllTimeOfficialQuery()
    {
        var rewriter = new ExistenceSearchArgsRewriter();
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "Does Vendor Z1 exist as a released product?"
        };

        var rewritten = rewriter.Rewrite(
            context,
            ToolNames.WebSearch,
            "{\"query\":\"Vendor Z1 latest rumors\",\"recency\":\"month\"}");

        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal("Vendor Z1 official release date specifications model list", document.RootElement.GetProperty("query").GetString());
        Assert.Equal("any", document.RootElement.GetProperty("recency").GetString());
    }

    [Fact]
    public void ExistenceSearchArgsRewriter_WhenNotExistenceRequest_LeavesSearchUntouched()
    {
        var rewriter = new ExistenceSearchArgsRewriter();
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "What is the weather today?"
        };

        var original = "{\"query\":\"weather today\",\"recency\":\"day\"}";
        var rewritten = rewriter.Rewrite(context, ToolNames.WebSearch, original);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void FactSearchArgsRewriter_WhenLatestStableVersionRequest_UsesAllTimeOfficialVersionQuery()
    {
        var rewriter = new FactSearchArgsRewriter();
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "What is the latest stable version of QuantaScript as of 2025?"
        };

        var rewritten = rewriter.Rewrite(
            context,
            ToolNames.WebSearch,
            "{\"query\":\"latest stable version of QuantaScript 2025\",\"recency\":\"week\"}");

        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal("latest stable version of QuantaScript official documentation release notes", document.RootElement.GetProperty("query").GetString());
        Assert.Equal("any", document.RootElement.GetProperty("recency").GetString());
    }

    [Fact]
    public void FactSearchArgsRewriter_WhenNotVersionFactRequest_LeavesSearchUntouched()
    {
        var rewriter = new FactSearchArgsRewriter();
        var context = new TurnContext
        {
            ThreadId = "thread",
            MessageId = "message",
            UserText = "What are the top technology news stories today?"
        };

        var original = "{\"query\":\"top technology news\",\"recency\":\"day\"}";
        var rewritten = rewriter.Rewrite(context, ToolNames.WebSearch, original);

        Assert.Equal(original, rewritten);
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
    public void Apply_WhenRetryInstructionEchoedForOpenStatus_StripsInternalEnvelope()
    {
        const string retryEnvelope = """
            User request: Is McDonalds in Portland OR open right now?
            Retry strategy: official_source_search
            Guidance: Prioritize official/first-party documentation and policy pages.
            Previous answer for verification:
            I cannot access real-time restaurant information.
            Return concise, evidence-grounded output and call out uncertainty when unresolved.
            """;
        const string echoedResponse = """
            **User request: Is McDonalds in Portland OR open right now?
            Retry strategy: official_source_search
            Guidance: Prioritize official/first-party documentation and policy pages.
            Previous answer for verification:
            I cannot access real-time restaurant information.
            Return concise, evidence-grounded output and call out uncertainty when unresolved**
            Verification recommended
            The live search fallback did not surface a trustworthy hours page in this run.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            echoedResponse,
            retryEnvelope,
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"McDonalds Portland OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = false
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"McDonalds Portland OR open right now\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("I could not confirm whether", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("McDonalds", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Portland", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Retry strategy:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Previous answer for verification", response, StringComparison.OrdinalIgnoreCase);
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
            "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Meridian Drift Season 7 Episode 2 plot\"}",
                    Result = "[search: 2 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Meridian Drift", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official Season 7 Episode 2", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenLatestVersionNoResultsGenericFallback_PreservesSubject()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            ExplicitWebNoResultsContractNormalizer.UnavailableMessage,
            "What is the latest stable version of Python?",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"latest stable version of Python\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Python", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot verify the latest stable version", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStrictTwoLineLatestVersionPromptDrifts_RebuildsContract()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "Sir Thaddeus here. I have consulted the web, but the search results are not providing a definitive answer regarding the latest stable version of .NET as of 2025 with sufficient detail to meet your strict two-line format requirement.",
            "What is the latest stable version of .NET as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"latest stable version of .NET\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.StartsWith("Answer:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\nCommentary:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStrictTwoLineLatestVersionSearchIsInconclusive_RebuildsContract()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "Sir Thaddeus here. It appears my initial query did not yield a precise, definitive answer regarding the latest stable .NET version for 2025. Since the search results were inconclusive, I will attempt a more targeted web search. May I proceed with that second query?",
            "What is the latest stable version of .NET as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"latest stable version of .NET\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.StartsWith("Answer:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\nCommentary:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".NET", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("May I proceed", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenMediaInstallmentAnswerSummarizesReboot_ReplacesWithNonexistenceAnswer()
    {
        const string weakResponse = "I dont have information regarding the specific plot details for Episode 2 of Season 7 of Meridian Drift. The search results only confirm a new Meridian Drift reboot ordered by a streaming service.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What would be the plot of Episode 2 of Season 7 of Meridian Drift about?",
            [
                new ToolCallRecord
                {
                    ToolName = "web_search",
                    Arguments = "{\"query\":\"Meridian Drift Season 7 Episode 2 plot\"}",
                    Result = "[search: 4 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Meridian Drift", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official Season 7 Episode 2", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("streaming service", response, StringComparison.OrdinalIgnoreCase);
    }

    // Parallel knowledge-listing fallback test removed after the wiki became
    // the only assistant-shared knowledge surface; see DESIGN_NOTES_2026-05.md.

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
    public void Apply_WhenStrictLatestVersionAnswerIsNoisy_DoesNotSynthesizeHardCodedAnswer()
    {
        const string weakResponse = """
            Answer: I do not have a definitive answer for the latest stable version of QuantaScript as of 2025 based on the search results provided.
            Commentary: The recent search query returned an unrelated article, indicating that no specific version information was found in the immediate results.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "What is the latest stable version of QuantaScript as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"latest stable version of QuantaScript 2025\"}",
                    Result = "[search: 1 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\bQuantaScript\s+\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase), response);
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
    public void Apply_WhenMovieComparisonMentionsAnimatedAndLiveActionAdaptations_InsertsOriginal()
    {
        const string weakResponse = "My previous search indicated that it is not word for word identical; rather, it shares the core story while featuring changes. The sources suggested differences exist between the animated and live-action adaptations.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"How to Train Your Dragon live action word for word original\"}",
                    Result = "[search: 3 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("original animated and live-action adaptations", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStructuredSearchGetsNoResults_DoesNotAskPermission()
    {
        const string weakResponse = "Since I must gather live evidence before synthesizing this information, I shall broaden my approach. Would you permit me to execute a broader search? Once we have gathered sufficient material, I shall structure it precisely as requested.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Search for recent updates and developments in Orion Mesh from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"recent updates and developments in Orion Mesh last year\"}",
                    Result = "[search: 0 result(s) returned]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Orion Mesh recent updates developments 2024 2025 official\"}",
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
            "Search for recent updates and developments in Orion Mesh from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Orion Mesh recent updates and developments last year\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/orion-mesh-release","title":"Orion Mesh 13.2 Released with Expanded CLI, TypeScript Host Preview, and Dashboard Improvements","domain":"example.com","snippet":"The release expands CLI functionality, previews TypeScript host support, and improves the dashboard."}]}
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
    public void Apply_WhenStructuredSearchFindsIrrelevantResults_ReplacesWithDeterministicStructure()
    {
        const string weakResponse = """
            Overview: Zero relevant information was retrieved regarding .NET Aspire developments.
            Common Points, Differences, Practical Takeaway: These sections cannot be populated as no factual basis for them exists.
            Should I attempt another web search using different keywords?
            """;

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
                        {"sources":[{"url":"https://example.com/aspire-release","title":".NET Aspire 9.4 release notes","domain":"example.com","snippet":"The release improves dashboard diagnostics, app-host workflow, and integrations for distributed application development."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("Overview:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Common Points:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Differences:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Practical Takeaway:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Should I attempt", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenStructuredSearchEchoesPageChrome_RebuildsCleanSourceFocus()
    {
        const string chromeLeakingResponse = """
            Overview: Orion Mesh has continued to evolve over the last year.

            Common Points:
            - Recent sources overlap on developer tooling.

            Differences:
            - One emphasis is: What's New in Aspire 13.3 | Aspire Blog Skip to main content Aspire 13.3 has arrived! Get the latest release that brings Kubernetes support, agent-assisted Aspirification, and more. Get Aspire 13.3 Maddy Montaquila Principal Product Manager Aspire.

            Practical Takeaway: Use the official release notes first.
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            chromeLeakingResponse,
            "Search for recent updates and developments in Orion Mesh from the last year. Synthesize information from multiple sources, compare what overlaps and what differs. Provide a structured response with: Overview, Common Points, Differences, Practical Takeaway.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Orion Mesh recent updates and developments last year\"}",
                    Result = """
                        [search: 2 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/orion-mesh-13-3","title":"What's New in Orion Mesh 13.3 | Orion Blog","domain":"example.com","snippet":"What's New in Orion Mesh 13.3 | Orion Blog Skip to main content Orion Mesh 13.3 has arrived! Get the latest release that brings Kubernetes support, agent-assisted mesh planning, and more. Get Orion Mesh 13.3 Product Manager Orion."},{"url":"https://example.com/orion-mesh-13-2","title":"Orion Mesh 13.2 release notes","domain":"example.com","snippet":"A list of new features, updates, and breaking changes in Orion Mesh 13.2."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("Overview:", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kubernetes support", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Skip to main content", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get the latest release", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Principal Product Manager", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenNewsDigestContainsLandingPageAndClippedSnippet_RebuildsFromStorySources()
    {
        const string weakResponse = """
            Here are the main stories I found:
            1. Tech News, Latest Technology News Today, New Gadgets, Phones, Laptops ... -- Tech News: Get the latest technology today news, reviews, and updates on smartphones, laptops, gaming, wearables, and more. Stay updated on Apple, Samsung, Google, Microsoft, and other tech giants with The Indian Express
            2. Six years after 'public breakup', Apple goes back to Intel due to what ... -- Tech News News: Apple has reached a preliminary agreement with Intel to manufacture some chips for its devices -- a deal that would end years of estrangement between the
            3. White House Considers AI Vetting, Sparks Tech Industry Panic -- The White House is scrambling to find its footing on AI policy, as the development of new, more powerful models forces the administration to rethink its strategy on&n
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Give me the top technology news stories today.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"top technology news\",\"categories\":[\"news\"],\"recency\":\"day\"}",
                    Result = """
                        [search: 4 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://indianexpress.com/section/technology/","title":"Tech News, Latest Technology News Today, New Gadgets, Phones, Laptops ...","domain":"indianexpress.com","snippet":"Tech News: Get the latest technology today news, reviews, and updates on smartphones, laptops, gaming, wearables, and more. Stay updated on Apple, Samsung, Google, Microsoft, and other tech giants with The Indian Express"},{"url":"https://timesofindia.example/apple-intel","title":"Six years after 'public breakup', Apple goes back to Intel due to what ...","domain":"timesofindia.example","snippet":"Tech News News: Apple has reached a preliminary agreement with Intel to manufacture some chips for its devices - a deal that would end years of estrangement between the companies."},{"url":"https://wired.example/ai-vetting","title":"White House Considers AI Vetting, Sparks Tech Industry Panic","domain":"wired.example","snippet":"The White House is scrambling to find its footing on AI policy, as the development of new, more powerful models forces officials to rethink their strategy on oversight."},{"url":"https://cnbc.example/nvidia-ai-investor","title":"Nvidia embraces AI investor, topping $40 billion in equity bets 2026 - CNBC","domain":"cnbc.example","snippet":"Nvidia is pouring billions of dollars at a time into companies across the AI infrastructure stack, while also signing commercial deals with them."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.StartsWith("Here are the main stories I found:", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Tech News, Latest Technology News Today", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Get the latest technology today news", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&n", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Apple goes back to Intel", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("White House Considers AI Vetting", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nvidia embraces AI investor", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenFactAnswerAddsSirThaddeusNote_StripsOrnamentalTail()
    {
        const string responseWithNote = """
            Answer: The latest stable version is QuantaScript 10.0.7.

            Commentary: Official version listings are the best source for this kind of changing fact.

            ***
            *Sir Thaddeus's Note: Always treat documentation with a healthy dose of skepticism.*
            """;

        var response = ToolBackedResponseQualityGuards.Apply(
            responseWithNote,
            "What is the latest stable version of QuantaScript?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"latest stable version of QuantaScript\"}",
                    Result = """
                        [search: 1 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[{"url":"https://example.com/quantascript-versions","title":"QuantaScript versions","domain":"example.com","snippet":"QuantaScript 10.0.7 is listed as active LTS."}]}
                        """,
                    Success = true
                }
            ]);

        Assert.Contains("Answer: The latest stable version is QuantaScript 10.0.7.", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sir Thaddeus", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("healthy dose of skepticism", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenToolBackedAnswerHasMojibakeDash_CleansReadablePunctuation()
    {
        var response = ToolBackedResponseQualityGuards.Apply(
            "Here are the main stories I found:\n1. Example headline \u00E2\u20AC\u201D 2026-05-09 10:00 UTC",
            "Give me the top technology news stories today.",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"top technology news\",\"recency\":\"day\"}",
                    Result = "[search: 1 result(s) returned]",
                    Success = true
                }
            ]);

        Assert.Contains("Example headline - 2026-05-09", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\u00E2\u20AC\u201D", response, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void Apply_WhenReleasedProductExistenceAnswerIsWeak_ReplacesFromSearchEvidence()
    {
        const string weakResponse = "It certainly does exist, based on what I gathered. The search results also mention future models, which gives context.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Does Vendor Z1 exist as a released product?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Vendor Z1 official release\"}",
                    Result = """
                        [search: 2 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[
                          {"url":"https://vendor.example/support/z1","title":"Vendor Z1 - Tech Specs","domain":"vendor.example","snippet":"Year introduced: 2024. Vendor Z1 tech specs and support."},
                          {"url":"https://vendor.example/compare","title":"Compare Vendor Z1 models","domain":"vendor.example","snippet":"Compare released Vendor Z1 models."}
                        ]}
                        """,
                    Success = true
                }
            ]);

        Assert.StartsWith("Yes", response);
        Assert.Contains("Vendor Z1 exists as a released product", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2024", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Evidence checked", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future models", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenReleasedProductMissingFromReleaseLists_ReplacesWithNegativeEvidenceSummary()
    {
        const string weakResponse = "It appears the Vendor Z99 does not exist. I recommend consulting official announcements.";

        var response = ToolBackedResponseQualityGuards.Apply(
            weakResponse,
            "Does Vendor Z99 exist as a released product?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"Vendor Z99 release history\"}",
                    Result = """
                        [search: 2 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[
                          {"url":"https://vendor.example/models","title":"List of Vendor Z models","domain":"vendor.example","snippet":"Vendor Z is a line of devices. Current released models include Vendor Z1 and Vendor Z2."},
                          {"url":"https://industry.example/vendor-z-history","title":"Every Vendor Z release in chronological order","domain":"industry.example","snippet":"Release history and model list for the Vendor Z family."}
                        ]}
                        """,
                    Success = true
                }
            ]);

        Assert.StartsWith("No", response);
        Assert.Contains("Vendor Z99", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("release/model-list evidence", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Evidence checked", response, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consulting official announcements", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Apply_WhenRawLlmContextErrorAfterLocalBusinessTools_UsesToolEvidenceFallback()
    {
        const string rawError = "LLM returned 400 (Bad Request): {\"error\":\"The number of tokens to keep from the initial prompt is greater than the context length (n_keep: 31669>= n_ctx: 4096).\"}";

        var response = ToolBackedResponseQualityGuards.Apply(
            rawError,
            "Can you find a good florist in Hillsboro, OR?",
            [
                new ToolCallRecord
                {
                    ToolName = ToolNames.PlacesLookup,
                    Arguments = "{\"query\":\"florist in Hillsboro, OR\"}",
                    Result = "[Places lookup error: Google Places API key is not configured.]",
                    Success = true
                },
                new ToolCallRecord
                {
                    ToolName = ToolNames.WebSearch,
                    Arguments = "{\"query\":\"best florist in Hillsboro, OR reviews\"}",
                    Result = """
                        [search: 2 result(s) returned]

                        <!-- SOURCES_JSON -->
                        {"sources":[
                          {"url":"https://example.test/hillsboro-florist","title":"Hillsboro Florist Reviews","domain":"example.test","snippet":"Local florist reviews in Hillsboro, Oregon."}
                        ]}
                        """,
                    Success = true
                }
            ]);

        Assert.DoesNotContain("LLM returned", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("florist", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hillsboro", response, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sources checked", response, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Unexpected HTTP call", null, HttpStatusCode.InternalServerError);
    }
}
