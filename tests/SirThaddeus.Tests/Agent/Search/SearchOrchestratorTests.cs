using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class SearchOrchestratorTests
{
    [Fact]
    public void Constructor_NullDependencies_ThrowArgumentNullException()
    {
        var llm = new FakeLlmClient();
        var mcp = new FakeMcpClient();
        var audit = new FakeAuditLogger();

        Assert.Throws<ArgumentNullException>(() => new SearchOrchestrator(null!, mcp, audit, "sys"));
        Assert.Throws<ArgumentNullException>(() => new SearchOrchestrator(llm, null!, audit, "sys"));
        Assert.Throws<ArgumentNullException>(() => new SearchOrchestrator(llm, mcp, null!, "sys"));
        Assert.Throws<ArgumentNullException>(() => new SearchOrchestrator(llm, mcp, audit, null!));
    }

    [Fact]
    public void SystemPrompt_NullAssignment_NormalizesToEmptyString()
    {
        var sut = new SearchOrchestrator(new FakeLlmClient(), new FakeMcpClient(), new FakeAuditLogger(), "initial");

        sut.SystemPrompt = null!;

        Assert.Equal(string.Empty, sut.SystemPrompt);
    }

    [Fact]
    public async Task ExecuteAsync_WithForcedMode_ReturnsStructuredResponse()
    {
        var toolCalls = new List<ToolCallRecord>();
        var sut = new SearchOrchestrator(new FakeLlmClient(), new FakeMcpClient(), new FakeAuditLogger(), "system");

        var response = await sut.ExecuteAsync(
            userMessage: "bring me up more on moonrise bakery",
            memoryPackText: string.Empty,
            history: [],
            toolCallsMade: toolCalls,
            modeHint: LookupModeHint.DeepDive,
            ct: CancellationToken.None);

        Assert.NotNull(response);
        Assert.NotNull(response.Text);
        Assert.Same(toolCalls, response.ToolCallsMade);
    }

    [Fact]
    public void ProductRecommendationFilter_RejectsGoogleNewsEditorialSourcesForRetailerRequest()
    {
        var sources = new List<SourceItem>
        {
            new()
            {
                SourceId = "editorial",
                Url = "https://news.google.com/rss/articles/example?oc=5",
                Title = "The 6 Best Ashwagandha Supplements of 2026",
                Domain = "news.google.com",
                Snippet = "A review roundup that mentions Amazon."
            },
            new()
            {
                SourceId = "amazon",
                Url = "https://www.amazon.com/dp/B000000000",
                Title = "Ashwagandha Supplement 600mg",
                Domain = "amazon.com",
                Snippet = "Ashwagandha supplement listing with reviews."
            }
        };

        var filtered = SearchOrchestrator.TestHook_FilterProductSources(sources, ["amazon.com"], "Ashwagandha");

        var source = Assert.Single(filtered);
        Assert.Equal("amazon", source.SourceId);
    }

    [Fact]
    public async Task ExecuteAsync_MediaInstallmentCancellation_ReturnsGenericFallbackInsteadOfBareCancelled()
    {
        var llm = new FakeLlmClient((messages, _) =>
        {
            var system = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? string.Empty;
            if (system.Contains("search query builder", StringComparison.OrdinalIgnoreCase))
            {
                return new LlmResponse
                {
                    IsComplete = true,
                    Content = """{"query":"meridian drift season 7 episode 2 plot","recency":"any"}"""
                };
            }

            return new LlmResponse
            {
                IsComplete = true,
                Content = "Cancelled"
            };
        });

        var sut = new SearchOrchestrator(
            llm,
            new FakeMcpClient((toolName, _) => toolName switch
            {
                "web_search" or "WebSearch" => "Tool error: Cancelled",
                _ => "{}"
            }),
            new FakeAuditLogger(),
            "system");

        var response = await sut.ExecuteAsync(
            userMessage: "What's the plot of Episode 2 of Season 7 of Meridian Drift?",
            memoryPackText: string.Empty,
            history: [ChatMessage.System("system")],
            toolCallsMade: [],
            modeHint: LookupModeHint.Fact,
            ct: CancellationToken.None);

        Assert.True(response.Success);
        Assert.NotEqual("Cancelled", response.Text);
        Assert.Contains("Meridian Drift", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("official Season 7 Episode 2", response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("should not invent a plot", response.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, int?, LlmResponse>? _handler;

        public FakeLlmClient(Func<IReadOnlyList<ChatMessage>, int?, LlmResponse>? handler = null)
        {
            _handler = handler;
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_handler is not null)
            {
                return Task.FromResult(_handler(messages, null));
            }

            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = "ok"
            });
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_handler is not null)
            {
                return Task.FromResult(_handler(messages, maxTokensOverride));
            }

            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = "ok"
            });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");
    }

    private sealed class FakeMcpClient : IMcpToolClient
    {
        private readonly Func<string, string, string>? _handler;

        public FakeMcpClient(Func<string, string, string>? handler = null)
        {
            _handler = handler;
        }

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler?.Invoke(toolName, argumentsJson) ?? "{}");
        }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<McpToolInfo> tools = [];
            return Task.FromResult(tools);
        }
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        private readonly List<AuditEvent> _events = [];

        public void Append(AuditEvent auditEvent) => _events.Add(auditEvent);

        public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            _events.Add(auditEvent);
            return Task.CompletedTask;
        }

        public IReadOnlyList<AuditEvent> ReadTail(int maxEvents)
            => _events.TakeLast(Math.Max(0, maxEvents)).ToList();

        public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int maxEvents, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AuditEvent>>(ReadTail(maxEvents));
    }
}
