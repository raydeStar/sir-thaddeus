namespace SirThaddeus.Agent.Workflow;

public interface ITaskClassifier
{
    Task<TaskEnvelope> ClassifyAsync(string userRequest, CancellationToken ct);
}

public interface IChecklistPlanner
{
    Task<UserVisibleChecklist> BuildChecklistAsync(TaskEnvelope envelope, CancellationToken ct);
}

public interface IExecutionPlanner
{
    Task<IReadOnlyList<PlannedAction>> BuildInitialPlanAsync(TaskEnvelope envelope, CancellationToken ct);
}

public interface ITaskOrchestrator
{
    Task<FinalTaskResult> RunAsync(TaskEnvelope envelope, CancellationToken ct);
}

public interface IToolExecutionService
{
    Task<ToolExecutionResult> ExecuteAsync(PlannedAction action, CancellationToken ct);
}

public interface IConfidenceEvaluator
{
    ConfidenceSnapshot Evaluate(TaskRunState state);
}

public interface IRetryPlanner
{
    Task<IReadOnlyList<PlannedAction>> BuildRetryPlanAsync(TaskRunState state, CancellationToken ct);
}

public interface IRetryGateEvaluator
{
    RetryGateDecision Evaluate(TaskRunState state, ConfidenceSnapshot confidence, TimeSpan elapsed);
}

public interface IProgressNarrator
{
    Task<string?> BuildUpdateAsync(TaskRunState state, ProgressTrigger trigger, CancellationToken ct);
}

public interface IProgressPublisher
{
    Task PublishChecklistAsync(UserVisibleChecklist checklist, CancellationToken ct);
    Task PublishEventAsync(ProgressEvent progressEvent, CancellationToken ct);
    Task PublishNarrationAsync(string message, CancellationToken ct);
}

public interface IAuditTrailWriter
{
    Task WriteAsync(TaskRunState state, CancellationToken ct);
}