using SirThaddeus.Invocation;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.DesktopRuntime.Services;

// ─────────────────────────────────────────────────────────────────────────
// WPF Permission Prompter
//
// Bridges IPermissionPrompter to the real WPF dialog via the
// Application's Dispatcher. Delegates to PermissionPromptWindow
// which is already implemented in the desktop runtime.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// IPermissionPrompter implementation that shows a WPF dialog on the
/// UI thread. Created by the composition root and passed into the
/// <see cref="WpfPermissionGate"/>.
/// </summary>
public sealed class WpfPermissionPrompter : IPermissionPrompter
{
    private readonly App _app;

    public WpfPermissionPrompter(App app)
        => _app = app ?? throw new ArgumentNullException(nameof(app));

    public Task<PermissionDecision> PromptAsync(
        PermissionRequest request, CancellationToken cancellationToken = default)
    {
        // Marshal to the WPF UI thread to show the modal dialog
        return _app.Dispatcher.Invoke(() =>
        {
            var promptWindow = new PermissionPromptWindow();
            promptWindow.SetRequest(request);

            // Try to set owner to the main window if available
            if (_app.MainWindow is { IsLoaded: true })
                promptWindow.Owner = _app.MainWindow;

            promptWindow.ShowDialog();
            return Task.FromResult(promptWindow.GetDecision());
        });
    }
}
