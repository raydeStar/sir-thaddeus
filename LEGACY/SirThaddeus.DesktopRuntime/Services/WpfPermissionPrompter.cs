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
    private readonly bool _isHeadless;

    public WpfPermissionPrompter(App app, bool isHeadless = false)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _isHeadless = isHeadless;
    }

    public Task<PermissionDecision> PromptAsync(
        PermissionRequest request, CancellationToken cancellationToken = default)
    {
        if (_isHeadless)
        {
            // In headless mode, we cannot show a UI prompt, so auto-deny
            return Task.FromResult(PermissionDecision.Deny("Auto-denied (headless mode)"));
        }

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
