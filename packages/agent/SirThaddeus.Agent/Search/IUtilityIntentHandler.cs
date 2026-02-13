using SirThaddeus.Agent.Dialogue;

namespace SirThaddeus.Agent.Search;

public sealed record UtilityIntentExecutionRequest
{
    public string UserMessage { get; init; } = "";
    public RouterOutput Route { get; init; } = new() { Intent = Intents.ChatOnly };
    public ToolPlanDecision ToolPlan { get; init; } = new();
    public ValidatedSlots? ValidatedSlots { get; init; }
    public IList<ToolCallRecord> ToolCallsMade { get; init; } = [];
    public int RoundTrips { get; init; }

    public Func<string, DeterministicUtilityMatch?>? TryDeterministicMatch { get; init; }
    public Func<DeterministicUtilityMatch, UtilityRouter.UtilityResult>? ToUtilityResult { get; init; }
    public Func<ToolPlanDecision, string, UtilityRouter.UtilityResult?>? BuildFromToolPlan { get; init; }
    public Func<string, UtilityRouter.UtilityResult?>? TryContextFollowUp { get; init; }
    public Func<string, CancellationToken, Task<UtilityRouter.UtilityResult?>>? TryInferWithLlmAsync { get; init; }
    public Action<UtilityRouter.UtilityResult>? RememberUtilityContext { get; init; }

    public Func<string, UtilityRouter.UtilityResult, IList<ToolCallRecord>, int, CancellationToken, ValidatedSlots?, Task<AgentResponse>>? ExecuteWeatherAsync { get; init; }
    public Func<string, UtilityRouter.UtilityResult, IList<ToolCallRecord>, int, CancellationToken, ValidatedSlots?, Task<AgentResponse>>? ExecuteTimeAsync { get; init; }
    public Func<UtilityRouter.UtilityResult, IList<ToolCallRecord>, int, CancellationToken, Task<AgentResponse>>? ExecuteHolidayAsync { get; init; }
    public Func<UtilityRouter.UtilityResult, IList<ToolCallRecord>, int, CancellationToken, Task<AgentResponse>>? ExecuteFeedAsync { get; init; }
    public Func<UtilityRouter.UtilityResult, IList<ToolCallRecord>, int, CancellationToken, Task<AgentResponse>>? ExecuteStatusAsync { get; init; }
    public Func<UtilityRouter.UtilityResult, IList<ToolCallRecord>, CancellationToken, Task>? ExecuteGenericToolCallAsync { get; init; }

    public Func<UtilityRouter.UtilityResult, string>? BuildInlineResponse { get; init; }
    public Func<string, bool>? ShouldSuppressUiArtifacts { get; init; }
    public Action<string, string>? LogEvent { get; init; }
}

public interface IUtilityIntentHandler
{
    Task<AgentResponse?> TryHandleAsync(
        UtilityIntentExecutionRequest request,
        CancellationToken cancellationToken = default);
}
