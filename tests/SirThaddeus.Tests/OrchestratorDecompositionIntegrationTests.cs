using SirThaddeus.Agent;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class OrchestratorDecompositionIntegrationTests
{
    [Fact]
    public async Task ProcessAsync_DelegatesToInjectedCoordinatorModules()
    {
        var router = new StubRouter(new RouterOutput
        {
            Intent = Intents.GeneralTool,
            Confidence = 0.8,
            RequiredCapabilities = [ToolCapability.Meta]
        });
        var memory = new StubMemoryContextProvider();
        var toolLoop = new CapturingToolLoopExecutor();
        var guardrails = new StubGuardrailsCoordinator();
        var deterministic = new StubDeterministicUtilityEngine();

        var llm = new FakeLlmClient("unused");
        var mcp = new FakeMcpClient((_, _) => "{}", FakeMcpClient.StandardToolSet);
        var audit = new TestAuditLogger();

        var agent = new AgentOrchestrator(
            llm,
            mcp,
            audit,
            "Test assistant.",
            router: router,
            memoryContextProvider: memory,
            toolLoopExecutor: toolLoop,
            deterministicUtilityEngine: deterministic,
            guardrailsCoordinator: guardrails);

        var result = await agent.ProcessAsync("please help with tools");

        Assert.True(result.Success);
        Assert.Equal("delegated tool loop response", result.Text);
        Assert.True(router.Called);
        Assert.True(memory.Called);
        Assert.True(toolLoop.Called);
        Assert.Contains("tool_list_capabilities", toolLoop.ObservedToolNames, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(2, guardrails.MessagesChecked.Count);
    }

    private sealed class StubRouter : IRouter
    {
        private readonly RouterOutput _route;
        public bool Called { get; private set; }

        public StubRouter(RouterOutput route)
        {
            _route = route;
        }

        public Task<RouterOutput> RouteAsync(RouterRequest request, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(_route);
        }
    }

    private sealed class StubMemoryContextProvider : IMemoryContextProvider
    {
        public bool Called { get; private set; }

        public Task<MemoryContextResult> GetContextAsync(
            MemoryContextRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new MemoryContextResult
            {
                PackText = "",
                OnboardingNeeded = false,
                Provenance = new MemoryContextProvenance
                {
                    SourceTool = "MemoryRetrieve",
                    Success = true,
                    Summary = "No relevant memories found."
                }
            });
        }
    }

    private sealed class CapturingToolLoopExecutor : IToolLoopExecutor
    {
        public bool Called { get; private set; }
        public List<string> ObservedToolNames { get; } = [];

        public Task<AgentResponse> ExecuteAsync(
            ToolLoopExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Called = true;
            ObservedToolNames.Clear();
            ObservedToolNames.AddRange(request.Tools.Select(t => t.Function.Name));
            return Task.FromResult(new AgentResponse
            {
                Text = "delegated tool loop response",
                Success = true,
                ToolCallsMade = request.ToolCallsMade,
                LlmRoundTrips = request.InitialRoundTrips
            });
        }
    }

    private sealed class StubGuardrailsCoordinator : IGuardrailsCoordinator
    {
        public List<string> MessagesChecked { get; } = [];

        public GuardrailsCoordinatorResult? TryRunDeterministicSpecialCase(string message, string mode)
        {
            MessagesChecked.Add(message);
            return null;
        }

        public Task<GuardrailsCoordinatorResult?> TryRunAsync(
            RouterOutput route,
            string message,
            string mode,
            CancellationToken cancellationToken = default)
        {
            MessagesChecked.Add(message);
            return Task.FromResult<GuardrailsCoordinatorResult?>(null);
        }
    }

    private sealed class StubDeterministicUtilityEngine : IDeterministicUtilityEngine
    {
        public DeterministicUtilityMatch? TryMatch(string userMessage) => null;
    }
}

