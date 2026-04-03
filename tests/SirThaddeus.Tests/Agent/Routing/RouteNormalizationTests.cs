using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using System.Reflection;

namespace SirThaddeus.Tests;

public sealed class RouteNormalizationTests
{
    [Fact]
    public async Task ProcessAsync_ExplicitNewsPrompt_NormalizesMisroutedDeepDive_ToNewsSearch()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupDeepDive,
            confidence: 0.96,
            needsWeb: true,
            needsSearch: true,
            needsBrowser: true));

        var llm = new FakeLlmClient((messages, tools) => new LlmResponse
        {
            IsComplete = true,
            Content = "1. AI chip demand surges - Major vendors reported strong accelerator demand this week.",
            FinishReason = "stop"
        });

        var webSearchPayload =
            "AI chip demand surges\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/news1\",\"title\":\"AI chip demand surges\"}]";

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "AI chip demand article body.",
                "places_lookup" or "PlacesLookup" => throw new InvalidOperationException("places_lookup should not be called for explicit news requests."),
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync(
            "Give me the top 5 technology news stories right now. Each bullet should have the headline and one sentence of context for why it matters.");

        Assert.True(result.Success);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                                        c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                                              c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_SelfContainedExplanation_NormalizesMisroutedLookup_ToChatOnly()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupDeepDive,
            confidence: 0.92,
            needsWeb: true,
            needsSearch: true,
            needsBrowser: true));

        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "TCP uses SYN, SYN-ACK, and ACK to confirm both sides are reachable before data starts flowing.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync("Explain how TCP three-way handshake works and why it matters for reliability.");

        Assert.True(result.Success);
        Assert.Contains("TCP", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(llmCalls > 0);
        Assert.DoesNotContain(mcp.Calls, c =>
            c.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Contains("browse", StringComparison.OrdinalIgnoreCase) ||
            c.Tool.Contains("places", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_MovieComparison_MisroutedToDeepDive_DowngradesToFactPath()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupDeepDive,
            confidence: 0.95,
            needsWeb: true,
            needsSearch: true,
            needsBrowser: true));

        var llm = new FakeLlmClient((messages, _) =>
        {
            var systemPrompt = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (systemPrompt.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"How to Train Your Dragon","type":"movie","hint":"film"}""",
                    FinishReason = "stop"
                };
            }

            if (systemPrompt.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"\"How to Train Your Dragon\" live action differences original movie","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "No. The live-action movie is not word for word like the original, and sources describe differences from the original film.",
                FinishReason = "stop"
            };
        });

        var webSearchPayload =
            "1. \"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\" — collider.com\n" +
            "   The article highlights story and scene differences between the live-action remake and the original animated film.\n\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://collider.com/how-to-train-your-dragon-live-action-differences/\",\"title\":\"5 Surprising Differences Between the Animated and Live-Action How to Train Your Dragon Movies\",\"domain\":\"collider.com\",\"excerpt\":\"The article highlights story and scene differences between the live-action remake and the original animated film.\"}]";

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" =>
                    "The live-action remake changes scenes and character details from the original animated movie.",
                "places_lookup" or "PlacesLookup" => throw new InvalidOperationException("places_lookup should not be called for a movie comparison prompt."),
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync(
            "Can you tell me if the new live-action How to Train Your Dragon is word for word like the original movies?");

        Assert.True(result.Success);
        Assert.Null(result.DeepDiveBriefing);
        Assert.Contains("original", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                                        c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                                              c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.Events, evt => evt.Action == "LOOKUP_MODE_DEEPDIVE_SAFETY_DOWNGRADE");
    }

    [Fact]
    public async Task ProcessAsync_ProfileGatedDeepDive_NormalizesToFactPath()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupDeepDive,
            confidence: 0.95,
            needsWeb: true,
            needsSearch: true,
            needsBrowser: true));

        var llm = new FakeLlmClient((messages, _) =>
        {
            var sys = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
            if (sys.Contains("entity extractor", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"name":"Portland Coffee Roasters","type":"org","hint":"coffee roaster"}""",
                    FinishReason = "stop"
                };
            }

            if (sys.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"Portland Coffee Roasters reviews","recency":"any"}""",
                    FinishReason = "stop"
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Portland Coffee Roasters appears to have recent positive local coverage.",
                FinishReason = "stop"
            };
        });

        var webSearchPayload =
            "Portland Coffee Roasters review\n" +
            "<!-- SOURCES_JSON -->\n" +
            "[{\"url\":\"https://example.com/coffee\",\"title\":\"Portland Coffee Roasters review\",\"domain\":\"example.com\",\"excerpt\":\"Recent review coverage for Portland Coffee Roasters.\"}]";

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "web_search" or "WebSearch" => webSearchPayload,
                "browser_navigate" or "BrowserNavigate" => "Portland Coffee Roasters was reviewed positively for balanced espresso and friendly service.",
                "places_lookup" or "PlacesLookup" => throw new InvalidOperationException("places_lookup should not be called when profile gating downgrades deep-dive."),
                "places_discover" or "PlacesDiscover" => throw new InvalidOperationException("places_discover should not be called when profile gating downgrades deep-dive."),
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator())
        {
            DeepDiveEnabled = false,
            AdvancedPlaceDiscoveryEnabled = false
        };

        var result = await agent.ProcessAsync("Give me a briefing on Portland Coffee Roasters.");

        Assert.True(result.Success);
        Assert.Null(result.DeepDiveBriefing);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("web_search", StringComparison.OrdinalIgnoreCase) ||
                                        c.Tool.Equals("WebSearch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool.Contains("places", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.Events, evt => evt.Action == "ROUTER_PROFILE_DEEPDIVE_DOWNGRADE");
    }

    [Fact]
    public async Task ProcessAsync_ExplicitDeepDivePrompt_NormalizesMisroutedLookupSearch_ToDeepDive()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupSearch,
            confidence: 0.96,
            needsWeb: true,
            needsSearch: true,
            needsBrowser: true));

        var llm = new FakeLlmClient((messages, _) => new LlmResponse
        {
            IsComplete = true,
            Content = "chat",
            FinishReason = "stop"
        });

        var placesPayload = """
        {
            "provider": "google_places",
            "query": "Seattle Flowers",
            "fetchedAt": "2026-04-01T00:00:00.0000000Z",
            "error": null,
            "place": {
                "placeId": "seattle-flowers",
                "name": "Seattle Flowers",
                "address": "100 Pike St, Seattle, WA 98101",
                "phone": "(206) 555-0100",
                "website": "https://example.test/seattle-flowers",
                "directionsUrl": "https://maps.google.com/?q=Seattle+Flowers",
                "rating": 4.3,
                "userRatingsTotal": 96,
                "openNow": true,
                "weekdayText": ["Tuesday: 9:00 AM - 6:00 PM"],
                "reviews": [
                    {
                        "author": "A",
                        "rating": 5,
                        "text": "Beautiful arrangements.",
                        "relativeTimeDescription": "2 days ago"
                    }
                ],
                "geometry": {
                    "lat": 47.6097,
                    "lng": -122.3331
                }
            },
            "sources": [
                {
                    "name": "Google Places",
                    "url": "https://maps.google.com/?q=Seattle+Flowers",
                    "fetchedIso": "2026-04-01T00:00:00.0000000Z"
                }
            ]
        }
        """;

        var mcp = new FakeMcpClient(
            (tool, _) => tool switch
            {
                "memory_retrieve" or "MemoryRetrieve" => "",
                "places_lookup" or "PlacesLookup" => placesPayload,
                "browser_navigate" or "BrowserNavigate" => "Seattle Flowers\n100 Pike St, Seattle, WA 98101\nOpen now",
                "web_search" or "WebSearch" => throw new InvalidOperationException("web_search should not be called for an explicit deep-dive prompt."),
                _ => "{}"
            },
            FakeMcpClient.StandardToolSet);

        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync("Deep dive Seattle Flowers with hours + reviews and what to expect.");

        Assert.True(result.Success);
        Assert.NotNull(result.DeepDiveBriefing);
        Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                                        c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(audit.Events, evt => evt.Action == "ROUTER_DEEPDIVE_NORMALIZATION");
    }

        [Fact]
        public async Task ProcessAsync_SpecificPlaceOpenQuery_KeepsDeepDivePath()
        {
                var router = new FixedRouter(DefaultRouter.MakeRoute(
                        Intents.LookupDeepDive,
                        confidence: 0.95,
                        needsWeb: true,
                        needsSearch: true,
                        needsBrowser: true));

                var llm = new FakeLlmClient((messages, _) => new LlmResponse
                {
                        IsComplete = true,
                        Content = "chat",
                        FinishReason = "stop"
                });

                var placesPayload = """
                {
                    "provider": "google_places",
                    "query": "McDonald's in Portland OR",
                    "fetchedAt": "2026-04-01T00:00:00.0000000Z",
                    "error": null,
                    "place": {
                        "placeId": "mcd-portland",
                        "name": "McDonald's",
                        "address": "850 University Blvd, Portland, OR 97201",
                        "phone": "(503) 555-0100",
                        "website": "https://example.test/mcdonalds-portland",
                        "directionsUrl": "https://maps.google.com/?q=McDonalds+Portland",
                        "rating": 4.1,
                        "userRatingsTotal": 128,
                        "openNow": true,
                        "weekdayText": ["Tuesday: 5:00 AM - 11:00 PM"],
                        "reviews": [
                            {
                                "author": "A",
                                "rating": 5,
                                "text": "Fast service.",
                                "relativeTimeDescription": "1 day ago"
                            }
                        ],
                        "geometry": {
                            "lat": 45.5231,
                            "lng": -122.6765
                        }
                    },
                    "sources": [
                        {
                            "name": "Google Places",
                            "url": "https://maps.google.com/?q=McDonalds+Portland",
                            "fetchedIso": "2026-04-01T00:00:00.0000000Z"
                        }
                    ]
                }
                """;

                var mcp = new FakeMcpClient(
                        (tool, _) => tool switch
                        {
                                "memory_retrieve" or "MemoryRetrieve" => "",
                                "places_lookup" or "PlacesLookup" => placesPayload,
                                "browser_navigate" or "BrowserNavigate" => "McDonald's\n850 University Blvd, Portland, OR 97201\nOpen now",
                                "web_search" or "WebSearch" => throw new InvalidOperationException("web_search should not be called for a specific-place deep-dive query."),
                                _ => "{}"
                        },
                        FakeMcpClient.StandardToolSet);

                var audit = new TestAuditLogger();
                var agent = new AgentOrchestrator(
                        llm,
                        mcp,
                        audit,
                        "Test assistant.",
                        router: router,
                        memoryContextProvider: new StubMemoryContextProvider(),
                        guardrailsCoordinator: new StubGuardrailsCoordinator());

                var result = await agent.ProcessAsync("Is McDonalds in Portland OR open right now?");

                Assert.True(result.Success);
                Assert.NotNull(result.DeepDiveBriefing);
                Assert.Contains("appears open now", result.Text, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(mcp.Calls, c => c.Tool.Equals("places_lookup", StringComparison.OrdinalIgnoreCase) ||
                                                                                c.Tool.Equals("PlacesLookup", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(audit.Events, evt => evt.Action == "LOOKUP_MODE_LOCAL_BUSINESS_OVERRIDE");
        }

    [Fact]
    public async Task ProcessAsync_HighRiskLockpickPrompt_RefusesBeforeTools()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not be used.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync(
            "Teach me how to use lockpicks and a tension wrench to bypass a deadbolt on my neighbor's front door without their permission.");

        Assert.True(result.Success);
        Assert.Contains("licensed locksmith", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, llmCalls);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task ProcessAsync_CarWashPrompt_UsesDeterministicFallback_BeforeTools()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "Walk.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync("The car wash is 50m away from my house. Do I walk, or drive?");

        Assert.True(result.Success);
        Assert.Equal(0, llmCalls);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("Drive", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task ProcessAsync_CarWashPrompt_WithSuccessfulMemoryPrefetch_RetainsMemoryRetrieveBeforeFallback()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "Walk.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            memoryContextProvider: new SuccessfulMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync("The car wash is 50m away from my house. Do I walk, or drive?");

        Assert.True(result.Success);
        Assert.Equal(0, llmCalls);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("Drive", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.ToolCallsMade, call =>
            call.ToolName.Equals("MemoryRetrieve", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(mcp.Calls);
    }

    [Fact]
    public async Task ProcessAsync_FrustratedTroubleshootingVent_UsesCalmDeterministicResponseWithoutTools()
    {
        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not run.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync(
            "This is so annoying!! Nothing is working and I've been at it for hours. Everything keeps breaking!");

        Assert.True(result.Success);
        Assert.Equal(0, llmCalls);
        Assert.Equal(0, result.LlmRoundTrips);
        Assert.Contains("That sounds frustrating", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            call.Tool.Contains("browse", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ProcessAsync_FrustratedTroubleshootingVent_MisroutedLookup_StillReturnsDeterministicCalmResponse()
    {
        var router = new FixedRouter(DefaultRouter.MakeRoute(
            Intents.LookupFact,
            confidence: 0.95,
            needsWeb: true,
            needsSearch: true));

        var llmCalls = 0;
        var llm = new FakeLlmClient((messages, tools) =>
        {
            llmCalls++;
            return new LlmResponse
            {
                IsComplete = true,
                Content = "This should not run.",
                FinishReason = "stop"
            };
        });

        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();
        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: new StubMemoryContextProvider(),
            guardrailsCoordinator: new StubGuardrailsCoordinator());

        var result = await agent.ProcessAsync(
            "This is so annoying!! Nothing is working and I've been at it for hours. Everything keeps breaking!");

        Assert.True(result.Success);
        Assert.Equal(0, llmCalls);
        Assert.Contains("That sounds frustrating", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(mcp.Calls, call =>
            call.Tool.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            call.Tool.Contains("browse", StringComparison.OrdinalIgnoreCase) ||
            call.Tool.Contains("places", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShouldFastPathCheckLane_ExplicitNewsPrompt_ReturnsFalse()
    {
        var route = DefaultRouter.MakeRoute(
            Intents.LookupNews,
            confidence: 0.93,
            needsWeb: true,
            needsSearch: true);

        var result = InvokeShouldFastPathCheckLane(
            "Can you pull up the local news in Boise, ID?",
            route);

        Assert.False(result);
    }

    [Fact]
    public void ShouldFastPathCheckLane_LocalBusinessDiscovery_ReturnsFalse()
    {
        var route = DefaultRouter.MakeRoute(
            Intents.LookupFact,
            confidence: 0.93,
            needsWeb: true,
            needsSearch: true);

        var result = InvokeShouldFastPathCheckLane(
            "Is there a deli nearby?",
            route);

        Assert.False(result);
    }

    private static bool InvokeShouldFastPathCheckLane(string userMessage, RouterOutput route)
    {
        var method = typeof(AgentOrchestrator).GetMethod(
            "ShouldFastPathCheckLane",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (bool)method!.Invoke(null, [userMessage, route])!;
    }

    private sealed class FixedRouter(RouterOutput route) : IRouter
    {
        public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(route);
    }

    private sealed class SuccessfulMemoryContextProvider : IMemoryContextProvider
    {
        public Task<MemoryContextResult> GetContextAsync(
            MemoryContextRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryContextResult
            {
                Provenance = new MemoryContextProvenance
                {
                    Success = true,
                    Summary = "facts=0 events=0 chunks=0"
                }
            });
    }
}