namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Cooperative execution boundary for a live assistant turn.
/// Runtimes may pause a turn at these safe checkpoints or cancel it through
/// the supplied token. The default implementation is a no-op so headless and
/// embedded consumers retain the production pipeline without UI dependencies.
/// </summary>
public interface ITurnExecutionControl
{
    Task<string?> ReachCheckpointAsync(
        TurnContext context,
        string checkpoint,
        CancellationToken cancellationToken);
}

public sealed class NullTurnExecutionControl : ITurnExecutionControl
{
    public static NullTurnExecutionControl Instance { get; } = new();

    private NullTurnExecutionControl()
    {
    }

    public Task<string?> ReachCheckpointAsync(
        TurnContext context,
        string checkpoint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(null);
    }
}
