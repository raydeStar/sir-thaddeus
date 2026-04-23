// ─────────────────────────────────────────────────────────────────────────
// Shared test helpers
//
// FakeLlmClient / FakeMcpClient / StubMemoryContextProvider /
// StubGuardrailsCoordinator were defined at the bottom of the old
// AgentOrchestratorTests.cs. When that legacy test file was retired they
// got extracted here so pipeline and component tests (which never
// referenced AgentOrchestrator but DID use these fakes) still build.
//
// Keep these `internal sealed` so individual test files can still define
// their own if they want a narrower fake for a single suite.
// ─────────────────────────────────────────────────────────────────────────

using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

internal sealed class FakeLlmClient : ILlmClient
{
    private readonly Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> _respond;

    // â”€â”€ Text-only constructors (backwards compatible) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public FakeLlmClient(Func<IReadOnlyList<ChatMessage>, string> respond)
        : this((msgs, _) => new LlmResponse
        {
            IsComplete = true,
            Content = respond(msgs),
            FinishReason = "stop"
        })
    { }

    public FakeLlmClient(string fixedResponse)
        : this(_ => fixedResponse) { }

    // â”€â”€ Full-control constructor (can return tool calls) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public FakeLlmClient(
        Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>?, LlmResponse> respond)
        => _respond = respond;

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_respond(messages, tools));
    }

    public Task<LlmResponse> ChatAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        int maxTokensOverride,
        CancellationToken cancellationToken = default)
    {
        return ChatAsync(messages, tools, cancellationToken);
    }

    public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<string?>("fake-test-model");
}

internal sealed class StubMemoryContextProvider : IMemoryContextProvider
{
    public Task<MemoryContextResult> GetContextAsync(
        MemoryContextRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new MemoryContextResult());
}

internal sealed class StubGuardrailsCoordinator : IGuardrailsCoordinator
{
    public GuardrailsCoordinatorResult? TryRunDeterministicSpecialCase(string message, string mode) => null;

    public Task<GuardrailsCoordinatorResult?> TryRunAsync(
        RouterOutput route,
        string message,
        string mode,
        string? extraContext = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<GuardrailsCoordinatorResult?>(null);
}

    internal sealed class StubRouter : IRouter
    {
        private readonly RouterOutput _route;

        public StubRouter(RouterOutput route) => _route = route;

        public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(_route);
    }

/// <summary>
/// Fake MCP tool client with per-tool response routing and realistic
/// tool discovery. Tracks all calls for assertion.
/// </summary>
internal sealed class FakeMcpClient : IMcpToolClient
{
    private readonly Func<string, string, string> _toolHandler;
    private readonly IReadOnlyList<McpToolInfo> _availableTools;

    public List<(string Tool, string Args)> Calls { get; } = [];

    // â”€â”€ Simple constructor: fixed return for all tools â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public FakeMcpClient(string returnValue)
        : this((_, _) => returnValue, []) { }

    // â”€â”€ Routing constructor: per-tool response logic â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public FakeMcpClient(
        Func<string, string, string> toolHandler,
        IReadOnlyList<McpToolInfo>? availableTools = null)
    {
        _toolHandler = toolHandler;
        _availableTools = availableTools ?? [];
    }

    public Task<string> CallToolAsync(
        string toolName, string argumentsJson, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, argumentsJson));
        return Task.FromResult(_toolHandler(toolName, argumentsJson));
    }

    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_availableTools);
    }

    // â”€â”€ Helpers for building realistic tool lists â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// A representative set of MCP tools matching what the real server
    /// exposes. Used by tests that need to verify tool filtering, the
    /// tool loop, or multi-tool scenarios.
    /// </summary>
    public static IReadOnlyList<McpToolInfo> StandardToolSet =>
    [
        MakeTool("screen_capture",     "Captures the user's screen",
                 """{"type":"object","properties":{"monitor":{"type":"integer","description":"Monitor index"}},"required":[]}"""),
        MakeTool("get_active_window",  "Gets active window metadata",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("browser_navigate",   "Fetches URL content",
                 """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        MakeTool("memory_store_facts", "Stores structured facts about the user",
                 """{"type":"object","properties":{"factsJson":{"type":"string","description":"JSON array of fact objects"},"sourceRef":{"type":"string","description":"Source reference"}},"required":["factsJson"]}"""),
        MakeTool("memory_update_fact", "Updates an existing memory fact",
                 """{"type":"object","properties":{"memoryId":{"type":"string"},"newObject":{"type":"string"}},"required":["memoryId","newObject"]}"""),
        MakeTool("memory_retrieve",    "Retrieves relevant memories",
                 """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        MakeTool("memory_list_facts",  "Lists memory facts",
                 """{"type":"object","properties":{"filter":{"type":"string"}},"required":[]}"""),
        MakeTool("memory_delete_fact", "Deletes a memory fact",
                 """{"type":"object","properties":{"memoryId":{"type":"string"}},"required":["memoryId"]}"""),
        MakeTool("web_search",         "Searches the web for information",
                 """{"type":"object","properties":{"query":{"type":"string"},"maxResults":{"type":"integer"},"recency":{"type":"string"}},"required":["query"]}"""),
        MakeTool("places_discover",    "Discovers nearby places using open data",
             """{"type":"object","properties":{"query":{"type":"string"},"userLocationHint":{"type":"string"},"maxResults":{"type":"integer"},"radiusMeters":{"type":"integer"},"locale":{"type":"string"}},"required":["query"]}"""),
        MakeTool("places_lookup",      "Looks up place details for deep-dive briefings",
                 """{"type":"object","properties":{"query":{"type":"string"},"timezone":{"type":"string"},"locale":{"type":"string"},"userLocationHint":{"type":"string"},"maxReviewSnippets":{"type":"integer"}},"required":["query"]}"""),
        MakeTool("weather_geocode",    "Geocodes a place for weather lookup",
                 """{"type":"object","properties":{"place":{"type":"string"},"maxResults":{"type":"integer"}},"required":["place"]}"""),
        MakeTool("weather_forecast",   "Fetches weather from coordinates",
                 """{"type":"object","properties":{"latitude":{"type":"number"},"longitude":{"type":"number"},"placeHint":{"type":"string"},"countryCode":{"type":"string"},"days":{"type":"integer"}},"required":["latitude","longitude"]}"""),
        MakeTool("resolve_timezone",   "Resolves timezone from coordinates",
                 """{"type":"object","properties":{"latitude":{"type":"number"},"longitude":{"type":"number"},"countryCode":{"type":"string"}},"required":["latitude","longitude"]}"""),
        MakeTool("holidays_get",       "Returns public holidays by country/year",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"year":{"type":"integer"},"regionCode":{"type":"string"},"maxItems":{"type":"integer"}},"required":["countryCode"]}"""),
        MakeTool("holidays_next",      "Returns upcoming public holidays",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"regionCode":{"type":"string"},"maxItems":{"type":"integer"}},"required":["countryCode"]}"""),
        MakeTool("holidays_is_today",  "Checks if today is a public holiday",
                 """{"type":"object","properties":{"countryCode":{"type":"string"},"regionCode":{"type":"string"}},"required":["countryCode"]}"""),
        MakeTool("feed_fetch",         "Fetches and parses RSS/Atom feed URL",
                 """{"type":"object","properties":{"url":{"type":"string"},"maxItems":{"type":"integer"}},"required":["url"]}"""),
        MakeTool("status_check_url",   "Checks URL reachability",
                 """{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""),
        MakeTool("MemoryRetrieve",     "Retrieves relevant memories",
                 """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        MakeTool("file_read",          "Reads a file from disk",
                 """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        MakeTool("file_list",          "Lists files in a directory",
                 """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}"""),
        MakeTool("system_execute",     "Executes an allowlisted command",
                 """{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""),
        MakeTool("tool_ping",          "Reports MCP health",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("tool_list_capabilities", "Lists available tool capabilities",
                 """{"type":"object","properties":{},"required":[]}"""),
        MakeTool("time_now",           "Returns local time metadata",
                 """{"type":"object","properties":{},"required":[]}"""),
    ];

    private static McpToolInfo MakeTool(string name, string desc, string schemaJson)
    {
        var schema = System.Text.Json.JsonSerializer.Deserialize<object>(schemaJson)!;
        return new McpToolInfo { Name = name, Description = desc, InputSchema = schema };
    }
}

