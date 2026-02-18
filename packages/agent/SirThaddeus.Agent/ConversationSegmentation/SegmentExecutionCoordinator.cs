namespace SirThaddeus.Agent.ConversationSegmentation;

public sealed record SegmentExecutionRequest
{
    public required IReadOnlyList<ConversationSegment> ActionableSegments { get; init; }
    public required int MaxToolUsingActionables { get; init; }
    public required Func<ConversationSegment, CancellationToken, Task<SegmentExecutionResult>> ExecuteActionableAsync { get; init; }
}

/// <summary>
/// Executes actionable segments in-order and applies tool-using caps.
/// </summary>
public sealed class SegmentExecutionCoordinator
{
    public async Task<SegmentExecutionPlan> ExecuteAsync(
        SegmentExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExecuteActionableAsync);

        var executed = new List<SegmentExecutionResult>(request.ActionableSegments.Count);
        var deferred = new List<ConversationSegment>();
        var toolUsingExecuted = 0;

        foreach (var segment in request.ActionableSegments.OrderBy(s => s.Order))
        {
            if (toolUsingExecuted >= request.MaxToolUsingActionables)
            {
                deferred.Add(segment);
                continue;
            }

            var result = await request.ExecuteActionableAsync(segment, cancellationToken);
            executed.Add(result);

            if (result.UsedTools)
                toolUsingExecuted++;
        }

        return new SegmentExecutionPlan
        {
            Executed = executed,
            Deferred = deferred
        };
    }
}

