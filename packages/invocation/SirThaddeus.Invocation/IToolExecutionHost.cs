using SirThaddeus.Core;

namespace SirThaddeus.Invocation;

/// <summary>
/// Abstraction for managing runtime state during tool execution.
/// Allows the orchestration layer to transition states without WPF dependencies.
/// </summary>
public interface IToolExecutionHost
{
    /// <summary>
    /// Gets the current assistant state.
    /// </summary>
    AssistantState CurrentState { get; }

    /// <summary>
    /// Gets a cancellation token that is cancelled on STOP ALL.
    /// </summary>
    CancellationToken RuntimeToken { get; }

    /// <summary>
    /// Transitions to a new state.
    /// </summary>
    /// <param name="state">The target state.</param>
    /// <param name="reason">Optional reason for the transition.</param>
    /// <returns>True if the transition was successful.</returns>
    bool SetState(AssistantState state, string? reason = null);

    /// <summary>
    /// Refreshes the audit feed display (if applicable).
    /// </summary>
    void RefreshAuditFeed();
}

/// <summary>
/// Implementation that wraps a RuntimeController.
/// </summary>
public sealed class RuntimeControllerHost : IToolExecutionHost
{
    private readonly RuntimeController _controller;
    private readonly Action? _refreshCallback;

    public RuntimeControllerHost(RuntimeController controller, Action? refreshCallback = null)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _refreshCallback = refreshCallback;
    }

    public AssistantState CurrentState => _controller.CurrentState;

    public CancellationToken RuntimeToken => _controller.RuntimeToken;

    public bool SetState(AssistantState state, string? reason = null)
        => _controller.SetState(state, reason);

    public void RefreshAuditFeed() => _refreshCallback?.Invoke();
}
