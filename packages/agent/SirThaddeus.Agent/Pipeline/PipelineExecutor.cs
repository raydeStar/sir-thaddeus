using System.Diagnostics;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Dispatches built queries to the appropriate execution path.
/// This is a thin adapter: the real execution logic lives in the existing
/// SearchOrchestrator, ToolLoopExecutor, and deterministic chat handlers.
/// The executor's value is recording what path was taken and how long it took,
/// enabling stage-level measurement.
/// </summary>
public sealed class PipelineExecutor : IRequestExecutor
{
    private readonly Func<BuiltQuery, CancellationToken, Task<(string Text, bool Success, string Error)>>? _executeFunc;

    /// <summary>
    /// Creates an executor with a custom execution delegate. In production,
    /// this delegate dispatches to the real orchestrator subsystems.
    /// For stage testing, provide a stub.
    /// </summary>
    public PipelineExecutor(
        Func<BuiltQuery, CancellationToken, Task<(string Text, bool Success, string Error)>>? executeFunc = null)
    {
        _executeFunc = executeFunc;
    }

    public async Task<ExecutorResult> ExecuteAsync(
        QueryBuilderResult queries,
        CancellationToken cancellationToken = default)
    {
        var segments = new List<ExecutionSegmentResult>(queries.Queries.Count);

        foreach (var query in queries.Queries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            if (!query.RequiresExecution)
            {
                // Chat/inline answers skip execution
                segments.Add(new ExecutionSegmentResult
                {
                    Source = query,
                    ResponseText = query.InlineAnswer,
                    Success = true,
                    DurationMs = sw.ElapsedMilliseconds
                });
                continue;
            }

            if (_executeFunc is not null)
            {
                try
                {
                    var (text, success, error) = await _executeFunc(query, cancellationToken);
                    sw.Stop();
                    segments.Add(new ExecutionSegmentResult
                    {
                        Source = query,
                        ResponseText = text,
                        Success = success,
                        Error = error,
                        DurationMs = sw.ElapsedMilliseconds
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sw.Stop();
                    segments.Add(new ExecutionSegmentResult
                    {
                        Source = query,
                        ResponseText = "",
                        Success = false,
                        Error = ex.Message,
                        DurationMs = sw.ElapsedMilliseconds
                    });
                }
            }
            else
            {
                // No executor configured — record as deferred
                sw.Stop();
                segments.Add(new ExecutionSegmentResult
                {
                    Source = query,
                    ResponseText = "",
                    Success = false,
                    Error = "No executor configured for this query type.",
                    DurationMs = sw.ElapsedMilliseconds
                });
            }
        }

        return new ExecutorResult { Segments = segments };
    }
}
