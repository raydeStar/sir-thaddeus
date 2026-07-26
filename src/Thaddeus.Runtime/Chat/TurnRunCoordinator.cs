using System.Collections.Concurrent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.AuditLog;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

public enum TurnRunState
{
    AwaitingApproval,
    Running,
    Pausing,
    Paused,
    TakingOver,
    Cancelling,
    Cancelled,
    Completed,
    Failed,
}

public sealed record TurnRunSnapshot(
    string RunId,
    string ThreadId,
    string UserMessageId,
    string? AssistantMessageId,
    TurnRunState State,
    string? Checkpoint,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    string? Detail,
    long Version,
    WorkPlan? Plan)
{
    public bool IsTerminal => State is TurnRunState.Cancelled or TurnRunState.Completed or TurnRunState.Failed;
}

/// <summary>
/// Owns the lifecycle and cancellation boundary for live assistant turns.
/// Pause is cooperative: a request becomes Paused at the next declared safe
/// pipeline/tool checkpoint. Cancellation propagates immediately through the
/// same token used by model, permission, MCP, streaming, and persistence work.
/// </summary>
public sealed class TurnRunCoordinator : ITurnExecutionControl, IDisposable
{
    private const int RetainedRuns = 500;
    private readonly ConcurrentDictionary<string, RunEntry> _runs = new(StringComparer.Ordinal);
    private readonly AsyncLocal<string?> _currentRunId = new();
    private readonly ChatTurnPublisher _publisher;
    private readonly IAuditLogger _audit;
    private readonly ILogger<TurnRunCoordinator> _logger;
    private long _sequence;

    public TurnRunCoordinator(
        ChatTurnPublisher publisher,
        IAuditLogger audit,
        ILogger<TurnRunCoordinator> logger)
    {
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public TurnRunSnapshot Create(
        string threadId,
        string userMessageId,
        CancellationToken applicationStopping,
        WorkPlan? plan = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessageId);

        var runId = $"run_{Interlocked.Increment(ref _sequence):x}_{Guid.NewGuid():N}"[..29];
        var now = DateTimeOffset.UtcNow;
        var entry = new RunEntry(
            new TurnRunSnapshot(
                runId,
                threadId,
                userMessageId,
                AssistantMessageId: null,
                plan is null ? TurnRunState.Running : TurnRunState.AwaitingApproval,
                Checkpoint: plan is null ? "queued" : "plan-review",
                now,
                now,
                Detail: plan is null ? null : "Review and approve the plan before work begins",
                Version: 1,
                Plan: plan),
            CancellationTokenSource.CreateLinkedTokenSource(applicationStopping));

        if (!_runs.TryAdd(runId, entry))
            throw new InvalidOperationException("Unable to allocate a unique run id.");

        Publish(entry.Snapshot);
        PruneTerminalRuns();
        return entry.Snapshot;
    }

    public async Task<WorkPlan?> WaitForApprovalAsync(string runId, CancellationToken cancellationToken)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            throw new KeyNotFoundException($"Run '{runId}' was not found.");

        await entry.ApprovalSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        lock (entry.Sync)
            return entry.Snapshot.Plan;
    }

    public TurnRunSnapshot? ApprovePlan(string runId, long expectedPlanVersion)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            var plan = entry.Snapshot.Plan
                ?? throw new TurnRunConflictException("This run does not have a plan to approve.");
            if (entry.Snapshot.State != TurnRunState.AwaitingApproval)
                throw new TurnRunConflictException("This plan is no longer awaiting approval.");
            if (plan.Version != expectedPlanVersion)
                throw new TurnRunConflictException(
                    $"Plan version {expectedPlanVersion} is stale; current version is {plan.Version}.");

            snapshot = Update(entry, TurnRunState.Running, "approved", "Plan approved by user");
        }

        entry.ApprovalSignal.TrySetResult(true);
        Audit("CHAT_PLAN_APPROVE", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? EditPlan(
        string runId,
        long expectedPlanVersion,
        IReadOnlyList<WorkPlanStep> steps)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;
        if (!WorkPlanBuilder.TryValidateEditedSteps(steps, out var validationError))
            throw new ArgumentException(validationError, nameof(steps));

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            var plan = entry.Snapshot.Plan
                ?? throw new TurnRunConflictException("This run does not have a plan to edit.");
            if (entry.Snapshot.State != TurnRunState.AwaitingApproval)
                throw new TurnRunConflictException("A plan can only be edited before approval.");
            if (plan.Version != expectedPlanVersion)
                throw new TurnRunConflictException(
                    $"Plan version {expectedPlanVersion} is stale; current version is {plan.Version}.");

            var updatedPlan = plan with
            {
                Version = plan.Version + 1,
                Steps = steps.Select(step => step with
                {
                    Label = step.Label.Trim(),
                    Status = WorkPlanStepStatus.Pending,
                }).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            entry.Snapshot = entry.Snapshot with
            {
                Plan = updatedPlan,
                Detail = "Plan revised; approval required",
                UpdatedAt = updatedPlan.UpdatedAt,
                Version = entry.Snapshot.Version + 1,
            };
            snapshot = entry.Snapshot;
        }

        Audit("CHAT_PLAN_EDIT", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public IDisposable Activate(string runId)
    {
        if (!_runs.ContainsKey(runId))
            throw new KeyNotFoundException($"Run '{runId}' was not found.");

        var prior = _currentRunId.Value;
        _currentRunId.Value = runId;
        return new Activation(this, prior);
    }

    public CancellationToken GetCancellationToken(string runId)
    {
        return _runs.TryGetValue(runId, out var entry)
            ? entry.Cancellation.Token
            : throw new KeyNotFoundException($"Run '{runId}' was not found.");
    }

    public TurnRunSnapshot? Get(string runId) =>
        _runs.TryGetValue(runId, out var entry) ? Read(entry) : null;

    public IReadOnlyList<TurnRunSnapshot> List(string? threadId = null)
    {
        return _runs.Values
            .Select(Read)
            .Where(run => threadId is null || string.Equals(run.ThreadId, threadId, StringComparison.Ordinal))
            .OrderByDescending(run => run.StartedAt)
            .ToArray();
    }

    public TurnRunSnapshot? Pause(string runId)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            if (entry.Snapshot.IsTerminal)
                return entry.Snapshot;
            if (entry.Snapshot.State == TurnRunState.AwaitingApproval)
                return entry.Snapshot;
            if (entry.Snapshot.State is TurnRunState.Pausing or TurnRunState.Paused or TurnRunState.TakingOver)
                return entry.Snapshot;

            entry.ResumeSignal = NewSignal();
            snapshot = Update(entry, TurnRunState.Pausing, entry.Snapshot.Checkpoint, "Pause requested");
        }

        Audit("CHAT_RUN_PAUSE", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? Resume(string runId)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        TaskCompletionSource<bool>? signal;
        lock (entry.Sync)
        {
            if (entry.Snapshot.IsTerminal)
                return entry.Snapshot;
            if (entry.Snapshot.State is not (TurnRunState.Pausing or TurnRunState.Paused or TurnRunState.TakingOver))
                return entry.Snapshot;

            signal = entry.ResumeSignal;
            entry.ResumeSignal = CompletedSignal();
            snapshot = Update(entry, TurnRunState.Running, entry.Snapshot.Checkpoint, "Resumed by user");
        }

        signal.TrySetResult(true);
        Audit("CHAT_RUN_RESUME", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? TakeOver(string runId)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            if (entry.Snapshot.IsTerminal)
                return entry.Snapshot;
            if (entry.Snapshot.State == TurnRunState.AwaitingApproval)
                throw new TurnRunConflictException("Approve or cancel the plan before taking over.");
            if (entry.Snapshot.State == TurnRunState.TakingOver)
                return entry.Snapshot;

            entry.ResumeSignal = NewSignal();
            snapshot = Update(
                entry,
                TurnRunState.TakingOver,
                entry.Snapshot.Checkpoint,
                "Handing the next safe step to the user");
        }

        Audit("CHAT_RUN_TAKE_OVER", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? Redirect(string runId, string instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction))
            throw new ArgumentException("A redirect instruction is required.", nameof(instruction));
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        TaskCompletionSource<bool>? signal = null;
        lock (entry.Sync)
        {
            if (entry.Snapshot.IsTerminal)
                return entry.Snapshot;
            if (entry.Snapshot.State == TurnRunState.AwaitingApproval)
                throw new TurnRunConflictException("Edit or approve the plan instead of redirecting it.");

            entry.SteeringInstructions.Enqueue(instruction.Trim());
            if (entry.Snapshot.State is TurnRunState.Pausing or TurnRunState.Paused or TurnRunState.TakingOver)
            {
                signal = entry.ResumeSignal;
                entry.ResumeSignal = CompletedSignal();
            }

            snapshot = Update(
                entry,
                TurnRunState.Running,
                entry.Snapshot.Checkpoint,
                "Remaining work redirected by user");
        }

        signal?.TrySetResult(true);
        Audit("CHAT_RUN_REDIRECT", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? Cancel(string runId, string detail = "Stopped by user")
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        TaskCompletionSource<bool> signal;
        lock (entry.Sync)
        {
            if (entry.Snapshot.IsTerminal)
                return entry.Snapshot;

            snapshot = Update(entry, TurnRunState.Cancelling, entry.Snapshot.Checkpoint, detail);
            signal = entry.ResumeSignal;
        }

        signal.TrySetResult(true);
        entry.Cancellation.Cancel();
        Audit("CHAT_RUN_CANCEL", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public int CancelAll(string detail = "Stopped by Stop All")
    {
        var cancelled = 0;
        foreach (var run in List().Where(item => !item.IsTerminal))
        {
            var before = run.State;
            var after = Cancel(run.RunId, detail);
            if (after is not null && before != TurnRunState.Cancelling)
                cancelled++;
        }

        return cancelled;
    }

    public TurnRunSnapshot? Complete(string runId, bool cancelled, string? detail = null)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            var state = cancelled || entry.Cancellation.IsCancellationRequested
                ? TurnRunState.Cancelled
                : TurnRunState.Completed;
            SetTerminalPlanStatuses(entry, cancelled ? WorkPlanStepStatus.Skipped : WorkPlanStepStatus.Done);
            snapshot = Update(entry, state, "complete", detail);
        }

        Audit(cancelled ? "CHAT_RUN_CANCELLED" : "CHAT_RUN_COMPLETED", snapshot, "ok");
        Publish(snapshot);
        return snapshot;
    }

    public TurnRunSnapshot? Fail(string runId, string detail)
    {
        if (!_runs.TryGetValue(runId, out var entry))
            return null;

        TurnRunSnapshot snapshot;
        lock (entry.Sync)
        {
            var state = entry.Cancellation.IsCancellationRequested
                ? TurnRunState.Cancelled
                : TurnRunState.Failed;
            SetTerminalPlanStatuses(entry, WorkPlanStepStatus.Blocked);
            snapshot = Update(entry, state, "complete", detail);
        }

        Audit(
            snapshot.State == TurnRunState.Cancelled ? "CHAT_RUN_CANCELLED" : "CHAT_RUN_FAILED",
            snapshot,
            snapshot.State == TurnRunState.Cancelled ? "cancelled" : "error");
        Publish(snapshot);
        return snapshot;
    }

    public async Task<string?> ReachCheckpointAsync(
        TurnContext context,
        string checkpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var runId = _currentRunId.Value;
        if (string.IsNullOrWhiteSpace(runId) || !_runs.TryGetValue(runId, out var entry))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        Task waitTask;
        TurnRunSnapshot? changed = null;
        lock (entry.Sync)
        {
            if (entry.Snapshot.AssistantMessageId is null ||
                !string.Equals(entry.Snapshot.Checkpoint, checkpoint, StringComparison.Ordinal))
            {
                AdvancePlan(entry, checkpoint);
                entry.Snapshot = entry.Snapshot with
                {
                    AssistantMessageId = context.MessageId,
                    Checkpoint = checkpoint,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Version = entry.Snapshot.Version + 1,
                };
                changed = entry.Snapshot;
            }

            if (entry.Snapshot.State == TurnRunState.Pausing)
            {
                changed = Update(entry, TurnRunState.Paused, checkpoint, "Paused at safe checkpoint");
            }
            else if (entry.Snapshot.State == TurnRunState.TakingOver)
            {
                changed = Update(entry, TurnRunState.TakingOver, checkpoint, "Waiting for user input");
            }

            waitTask = entry.ResumeSignal.Task;
        }

        if (changed is not null)
            Publish(changed);

        await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (entry.Sync)
        {
            return entry.SteeringInstructions.Count > 0
                ? entry.SteeringInstructions.Dequeue()
                : null;
        }
    }

    public void Dispose()
    {
        foreach (var entry in _runs.Values)
            entry.Cancellation.Dispose();
        _runs.Clear();
    }

    private static TurnRunSnapshot Read(RunEntry entry)
    {
        lock (entry.Sync)
            return entry.Snapshot;
    }

    private static TurnRunSnapshot Update(
        RunEntry entry,
        TurnRunState state,
        string? checkpoint,
        string? detail)
    {
        entry.Snapshot = entry.Snapshot with
        {
            State = state,
            Checkpoint = checkpoint,
            Detail = detail,
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = entry.Snapshot.Version + 1,
        };
        return entry.Snapshot;
    }

    private static void AdvancePlan(RunEntry entry, string checkpoint)
    {
        var plan = entry.Snapshot.Plan;
        var capability = MapCheckpoint(checkpoint);
        if (plan is null || capability is null)
            return;

        var steps = plan.Steps.ToArray();
        var target = Array.FindIndex(
            steps,
            step => step.Capability == capability &&
                    step.Status is WorkPlanStepStatus.Pending or WorkPlanStepStatus.Active);
        if (target < 0 || steps[target].Status == WorkPlanStepStatus.Active)
            return;

        for (var index = 0; index < steps.Length; index++)
        {
            if (steps[index].Status == WorkPlanStepStatus.Active)
                steps[index] = steps[index] with { Status = WorkPlanStepStatus.Done };
        }
        steps[target] = steps[target] with { Status = WorkPlanStepStatus.Active };
        entry.Snapshot = entry.Snapshot with
        {
            Plan = plan with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private static WorkPlanCapability? MapCheckpoint(string checkpoint)
    {
        var normalized = checkpoint.ToLowerInvariant();
        if (normalized.StartsWith("tool-loop:tool:", StringComparison.Ordinal))
        {
            var tool = normalized["tool-loop:tool:".Length..];
            if (tool.Contains("wiki", StringComparison.Ordinal) &&
                (tool.Contains("create", StringComparison.Ordinal) ||
                 tool.Contains("update", StringComparison.Ordinal) ||
                 tool.Contains("rewrite", StringComparison.Ordinal) ||
                 tool.Contains("delete", StringComparison.Ordinal) ||
                 tool.Contains("move", StringComparison.Ordinal)))
            {
                return WorkPlanCapability.DurableOutput;
            }
            if (tool.Contains("write", StringComparison.Ordinal) ||
                tool.Contains("delete", StringComparison.Ordinal) ||
                tool.Contains("send", StringComparison.Ordinal) ||
                tool.Contains("execute", StringComparison.Ordinal))
            {
                return WorkPlanCapability.DurableOutput;
            }
            if (tool.Contains("web", StringComparison.Ordinal) ||
                tool.Contains("search", StringComparison.Ordinal) ||
                tool.Contains("browser", StringComparison.Ordinal) ||
                tool.Contains("weather", StringComparison.Ordinal) ||
                tool.Contains("places", StringComparison.Ordinal))
            {
                return WorkPlanCapability.Research;
            }
            if (tool.Contains("read", StringComparison.Ordinal) ||
                tool.Contains("screen", StringComparison.Ordinal) ||
                tool.Contains("memory", StringComparison.Ordinal) ||
                tool.Contains("wiki", StringComparison.Ordinal))
            {
                return WorkPlanCapability.Context;
            }
        }

        if (normalized.Contains("memorycontext", StringComparison.Ordinal) ||
            normalized.Contains("corememory", StringComparison.Ordinal) ||
            normalized.Contains("dialoguestate", StringComparison.Ordinal))
        {
            return WorkPlanCapability.Context;
        }
        if (normalized.Contains("postprocess", StringComparison.Ordinal))
            return WorkPlanCapability.Compose;
        if (normalized.Contains("completionvalidation", StringComparison.Ordinal) ||
            normalized.Contains("responsecomposer", StringComparison.Ordinal))
        {
            return WorkPlanCapability.Verify;
        }
        return null;
    }

    private static void SetTerminalPlanStatuses(RunEntry entry, WorkPlanStepStatus activeStatus)
    {
        var plan = entry.Snapshot.Plan;
        if (plan is null)
            return;

        var steps = plan.Steps.Select(step => step.Status switch
        {
            WorkPlanStepStatus.Active => step with { Status = activeStatus },
            WorkPlanStepStatus.Pending => step with { Status = WorkPlanStepStatus.Skipped },
            _ => step,
        }).ToArray();
        entry.Snapshot = entry.Snapshot with
        {
            Plan = plan with { Steps = steps, UpdatedAt = DateTimeOffset.UtcNow },
        };
    }

    private void Publish(TurnRunSnapshot snapshot)
    {
        var task = _publisher.PublishRunStateAsync(
            new ChatRunStateChanged(
                snapshot.RunId,
                snapshot.ThreadId,
                snapshot.UserMessageId,
                snapshot.AssistantMessageId,
                snapshot.State.ToString().ToLowerInvariant(),
                snapshot.Checkpoint,
                snapshot.StartedAt,
                snapshot.UpdatedAt,
                snapshot.Detail,
                snapshot.Version,
                snapshot.Plan),
            CancellationToken.None);

        _ = task.ContinueWith(
            completed => _logger.LogWarning(
                completed.Exception,
                "chat_run.publish_failed run={RunId}",
                snapshot.RunId),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void Audit(string action, TurnRunSnapshot snapshot, string result)
    {
        _audit.Append(new AuditEvent
        {
            Actor = "user",
            Action = action,
            Target = snapshot.RunId,
            Result = result,
            Details = new Dictionary<string, object>
            {
                ["runId"] = snapshot.RunId,
                ["threadId"] = snapshot.ThreadId,
                ["userMessageId"] = snapshot.UserMessageId,
                ["assistantMessageId"] = snapshot.AssistantMessageId ?? string.Empty,
                ["state"] = snapshot.State.ToString(),
                ["checkpoint"] = snapshot.Checkpoint ?? string.Empty,
                ["version"] = snapshot.Version,
            },
        });
    }

    private void PruneTerminalRuns()
    {
        var overflow = _runs.Count - RetainedRuns;
        if (overflow <= 0)
            return;

        foreach (var snapshot in List()
                     .Where(run => run.IsTerminal)
                     .OrderBy(run => run.UpdatedAt)
                     .Take(overflow))
        {
            if (_runs.TryRemove(snapshot.RunId, out var removed))
                removed.Cancellation.Dispose();
        }
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult(true);
        return signal;
    }

    private sealed class RunEntry
    {
        public RunEntry(TurnRunSnapshot snapshot, CancellationTokenSource cancellation)
        {
            Snapshot = snapshot;
            Cancellation = cancellation;
            ResumeSignal = CompletedSignal();
            ApprovalSignal = snapshot.Plan is null ? CompletedSignal() : NewSignal();
        }

        public object Sync { get; } = new();
        public TurnRunSnapshot Snapshot { get; set; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource<bool> ResumeSignal { get; set; }
        public TaskCompletionSource<bool> ApprovalSignal { get; }
        public Queue<string> SteeringInstructions { get; } = new();
    }

    private sealed class Activation : IDisposable
    {
        private readonly TurnRunCoordinator _owner;
        private readonly string? _prior;
        private bool _disposed;

        public Activation(TurnRunCoordinator owner, string? prior)
        {
            _owner = owner;
            _prior = prior;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _owner._currentRunId.Value = _prior;
        }
    }
}

public sealed class TurnRunConflictException : InvalidOperationException
{
    public TurnRunConflictException(string message) : base(message)
    {
    }
}
