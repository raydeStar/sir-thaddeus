using SirThaddeus.Agent.Orchestration.Correlation;
using SirThaddeus.Agent.Validation.Completion;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.ToolLoop;

/// <summary>
/// Decorator around <see cref="IToolLoopExecutor"/> that adds post-execution
/// completion checking and bounded repair. After the inner executor finishes,
/// this wrapper:
///   1. Looks up the <see cref="CompletionContract"/> for the intent
///   2. Runs <see cref="CompletionChecker"/> against tool results
///   3. If incomplete and repair budget remains, injects a repair prompt
///      and re-enters the tool loop
///   4. Stamps <see cref="AgentResponse.IsPartial"/> and
///      <see cref="AgentResponse.MissingFields"/> on the final response
///
/// Design rules:
///   • The inner executor is never modified — this is pure decoration.
///   • Repair is bounded by <see cref="RunContext.MaxRepairs"/>.
///   • If no <see cref="RunContext"/> is provided on the request,
///     completion checking is skipped (backward-compatible).
/// </summary>
public sealed class CompletionAwareToolLoopExecutor : IToolLoopExecutor
{
    private readonly IToolLoopExecutor _inner;
    private readonly CompletionChecker _checker;
    private readonly RepairPlanner _repairPlanner;

    public CompletionAwareToolLoopExecutor(
        IToolLoopExecutor inner,
        CompletionChecker? checker = null,
        RepairPlanner? repairPlanner = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _checker = checker ?? new CompletionChecker();
        _repairPlanner = repairPlanner ?? new RepairPlanner();
    }

    public async Task<AgentResponse> ExecuteAsync(
        ToolLoopExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // First execution — delegate to inner
        var response = await _inner.ExecuteAsync(request, cancellationToken);

        // If no RunContext, skip completion checking (backward-compatible)
        if (request.RunContext is null)
            return response;

        var ctx = request.RunContext;
        var intent = request.Decision?.Intent ?? ctx.Intent;

        // Look up the completion contract for this intent
        var contract = CompletionContractRegistry.For(intent);
        if (ReferenceEquals(contract, CompletionContract.AlwaysSatisfied))
            return StampCorrelation(response, ctx);

        // Check completion
        var report = _checker.Check(contract, response.ToolCallsMade, response.Text);

        if (report.IsComplete)
            return StampCompletion(StampCorrelation(response, ctx), report);

        // Attempt repair loop
        var repairAttempt = 1;
        string? stopReasonOverride = null;
        while (!report.IsComplete && repairAttempt <= ctx.MaxRepairs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!ctx.RecordRepair())
            {
                stopReasonOverride = "repair_budget_exhausted";
                break;
            }

            var directive = _repairPlanner.Plan(report, repairAttempt, ctx.MaxRepairs);
            if (directive is null)
            {
                stopReasonOverride = repairAttempt > ctx.MaxRepairs
                    ? "repair_budget_exhausted"
                    : "no_actionable_repair";
                break;
            }

            request.LogEvent?.Invoke("COMPLETION_REPAIR",
                $"attempt={repairAttempt}/{ctx.MaxRepairs}, missing=[{string.Join(",", report.MissingFields)}]");

            // Inject repair prompt into history
            request.History.Add(ChatMessage.User(directive.RepairPrompt));

            // Re-execute the tool loop
            response = await _inner.ExecuteAsync(request, cancellationToken);

            // Re-check completion with ALL tool results (accumulated)
            report = _checker.Check(contract, response.ToolCallsMade, response.Text);
            repairAttempt++;
        }

        if (!report.IsComplete &&
            stopReasonOverride is null &&
            ctx.RepairCount >= ctx.MaxRepairs)
        {
            stopReasonOverride = "repair_budget_exhausted";
        }

        var finalStopReason = report.IsComplete
            ? report.StopReason
            : stopReasonOverride ?? report.StopReason;

        // Stamp final response with completion info
        return response with
        {
            IsPartial = !report.IsComplete,
            MissingFields = report.MissingFields,
            CorrelationId = ctx.CorrelationId.Value,
            CompletionConfidence = report.Confidence,
            CompletionStopReason = finalStopReason
        };
    }

    private static AgentResponse StampCorrelation(AgentResponse response, RunContext ctx) =>
        response with { CorrelationId = ctx.CorrelationId.Value };

    private static AgentResponse StampCompletion(AgentResponse response, CompletionReport report) =>
        response with
        {
            CompletionConfidence = report.Confidence,
            CompletionStopReason = report.StopReason
        };
}
