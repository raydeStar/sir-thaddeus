using SirThaddeus.Core;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;

namespace SirThaddeus.Invocation;

/// <summary>
/// Executes tool plans with permission prompting and state management.
/// </summary>
public sealed class ToolPlanExecutor
{
    private readonly IPermissionBroker _permissionBroker;
    private readonly IToolRunner _toolRunner;
    private readonly IPermissionPrompter _prompter;
    private readonly IToolExecutionHost _host;

    public ToolPlanExecutor(
        IPermissionBroker permissionBroker,
        IToolRunner toolRunner,
        IPermissionPrompter prompter,
        IToolExecutionHost host)
    {
        _permissionBroker = permissionBroker ?? throw new ArgumentNullException(nameof(permissionBroker));
        _toolRunner = toolRunner ?? throw new ArgumentNullException(nameof(toolRunner));
        _prompter = prompter ?? throw new ArgumentNullException(nameof(prompter));
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>
    /// Executes a tool plan.
    /// </summary>
    /// <param name="plan">The plan to execute.</param>
    /// <param name="cancellationToken">Additional cancellation token.</param>
    /// <returns>The execution result.</returns>
    public async Task<ExecutionResult> ExecuteAsync(
        ToolPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Link with runtime token for STOP ALL support
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _host.RuntimeToken,
            cancellationToken);
        var token = linkedCts.Token;

        var stepResults = new List<StepResult>();

        try
        {
            foreach (var (call, index) in plan.Steps.Select((c, i) => (c, i)))
            {
                token.ThrowIfCancellationRequested();

                var stepResult = await ExecuteStepAsync(call, plan.StepPreviews[index], token);
                stepResults.Add(stepResult);

                if (!stepResult.Success)
                {
                    // Stop on first failure
                    return ExecutionResult.PartialFailure(stepResults, stepResult.Error ?? "Step failed");
                }

                _host.RefreshAuditFeed();
            }

            return ExecutionResult.Ok(stepResults);
        }
        catch (OperationCanceledException)
        {
            return ExecutionResult.Cancelled(stepResults);
        }
        finally
        {
            // Return to idle after execution
            _host.SetState(AssistantState.Idle, "Plan execution completed");
            _host.RefreshAuditFeed();
        }
    }

    private async Task<StepResult> ExecuteStepAsync(
        ToolCall call,
        string preview,
        CancellationToken cancellationToken)
    {
        // Determine appropriate state based on capability
        var executionState = call.RequiredCapability switch
        {
            Capability.ScreenRead => AssistantState.ReadingScreen,
            Capability.BrowserControl => AssistantState.BrowserControl,
            _ => AssistantState.Thinking
        };

        // Request permission
        var permissionRequest = new PermissionRequest
        {
            Capability = call.RequiredCapability,
            Purpose = call.Purpose,
            Duration = TimeSpan.FromSeconds(30),
            Requester = "user"
        };

        var decision = await _prompter.PromptAsync(permissionRequest, cancellationToken);

        if (!decision.Approved)
        {
            _permissionBroker.LogDenial(permissionRequest, decision.DenialReason ?? "User denied");
            _host.RefreshAuditFeed();
            return StepResult.Denied(preview, decision.DenialReason ?? "Permission denied by user");
        }

        // Issue token
        var token = _permissionBroker.IssueToken(permissionRequest);

        // Transition to execution state
        _host.SetState(executionState, $"Executing: {preview}");

        // Execute the tool
        var result = await _toolRunner.ExecuteAsync(call, token.Id, cancellationToken);

        if (result.Success)
        {
            return StepResult.Ok(preview, result.Output, result.DurationMs);
        }
        else
        {
            return StepResult.Failed(preview, result.Error ?? "Unknown error");
        }
    }
}

/// <summary>
/// Result of executing a single step in a plan.
/// </summary>
public sealed record StepResult
{
    public required bool Success { get; init; }
    public required string StepDescription { get; init; }
    public object? Output { get; init; }
    public string? Error { get; init; }
    public long? DurationMs { get; init; }

    public static StepResult Ok(string description, object? output, long? durationMs = null) => new()
    {
        Success = true,
        StepDescription = description,
        Output = output,
        DurationMs = durationMs
    };

    public static StepResult Failed(string description, string error) => new()
    {
        Success = false,
        StepDescription = description,
        Error = error
    };

    public static StepResult Denied(string description, string reason) => new()
    {
        Success = false,
        StepDescription = description,
        Error = $"Permission denied: {reason}"
    };
}

/// <summary>
/// Result of executing an entire tool plan.
/// </summary>
public sealed record ExecutionResult
{
    public required bool Success { get; init; }
    public required IReadOnlyList<StepResult> StepResults { get; init; }
    public bool WasCancelled { get; init; }
    public string? Error { get; init; }

    public static ExecutionResult Ok(IReadOnlyList<StepResult> results) => new()
    {
        Success = true,
        StepResults = results
    };

    public static ExecutionResult PartialFailure(IReadOnlyList<StepResult> results, string error) => new()
    {
        Success = false,
        StepResults = results,
        Error = error
    };

    public static ExecutionResult Cancelled(IReadOnlyList<StepResult> results) => new()
    {
        Success = false,
        StepResults = results,
        WasCancelled = true,
        Error = "Execution was cancelled"
    };
}
