using System.Windows;
using SirThaddeus.Invocation;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Modal dialog for explicit permission prompting.
/// Displays capability, purpose, scope, and duration for user approval.
/// </summary>
public partial class PermissionPromptWindow : Window
{
    private PermissionDecision? _decision;

    public PermissionPromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Configures the dialog with the permission request details.
    /// </summary>
    public void SetRequest(PermissionRequest request)
    {
        CapabilityText.Text = request.Capability.ToString();
        PurposeText.Text = request.Purpose ?? "(No purpose specified)";
        ScopeText.Text = request.Scope?.ToSummary() ?? "No restrictions";
        DurationText.Text = FormatDuration(request.Duration);
    }

    /// <summary>
    /// Gets the user's decision after the dialog closes.
    /// </summary>
    public PermissionDecision GetDecision()
    {
        return _decision ?? PermissionDecision.Deny("Dialog closed without response");
    }

    private void AllowOnceButton_Click(object sender, RoutedEventArgs e)
    {
        _decision = PermissionDecision.AllowOnce();
        DialogResult = true;
        Close();
    }

    private void AllowSessionButton_Click(object sender, RoutedEventArgs e)
    {
        _decision = PermissionDecision.AllowSession();
        DialogResult = true;
        Close();
    }

    private void AllowAlwaysButton_Click(object sender, RoutedEventArgs e)
    {
        _decision = PermissionDecision.AllowAlways();
        DialogResult = true;
        Close();
    }

    private void DenyButton_Click(object sender, RoutedEventArgs e)
    {
        _decision = PermissionDecision.Deny("User denied permission");
        DialogResult = false;
        Close();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes < 1)
            return $"{(int)duration.TotalSeconds} seconds";
        if (duration.TotalHours < 1)
            return $"{(int)duration.TotalMinutes} minutes";
        if (duration.TotalDays < 1)
            return $"{duration.TotalHours:F1} hours";
        return $"{duration.TotalDays:F1} days";
    }
}
