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

        var brandIcon = Services.BrandIcon.WindowIcon;
        if (brandIcon is not null)
            Icon = brandIcon;
    }

    /// <summary>
    /// Configures the dialog with the permission request details.
    /// </summary>
    public void SetRequest(PermissionRequest request)
    {
        ToolNameText.Text = request.ToolName ?? "(unknown tool)";
        CapabilityText.Text = request.Capability.ToDisplayName();
        DescriptionText.Text = request.Capability.ToDescription();
        PurposeText.Text = FormatPurposeDetails(request.ToolName, request.Purpose);
        ScopeText.Text = request.Scope?.ToSummary() ?? "No restrictions";

        WarningText.Text = request.Capability switch
        {
            Capability.SystemExecute => "This tool can run commands on your system. Review the details carefully before allowing.",
            Capability.FileAccess => "This tool can read or write files on your computer. Review the path before allowing.",
            Capability.ScreenRead => "This tool will capture what is currently visible on your screen.",
            Capability.WebAccess => "This tool will make an outbound internet request on your behalf.",
            Capability.MemoryWrite => "This tool will store or modify data in your local memory database.",
            Capability.MemoryRead => "This tool will read from your local memory database.",
            _ => "Sir Thaddeus is requesting access to a tool on your behalf. Choose how to proceed."
        };
    }

    private static string FormatPurposeDetails(string? toolName, string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
            return "(no additional details)";

        // Strip the "Use tool 'xxx'." or "Use 'xxx': " prefix so we only show the args
        var cleaned = purpose;
        if (toolName is not null)
        {
            var prefixFull = $"Use tool '{toolName}'.";
            var prefixArgs = $"Use '{toolName}': ";
            if (cleaned.Equals(prefixFull, StringComparison.Ordinal))
                return "(no additional details)";
            if (cleaned.StartsWith(prefixArgs, StringComparison.Ordinal))
                cleaned = cleaned[prefixArgs.Length..];
        }

        return cleaned;
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

}
