using SirThaddeus.AuditLog;

namespace SirThaddeus.DesktopRuntime.ViewModels;

/// <summary>
/// ViewModel wrapper for displaying an audit event in the UI.
/// </summary>
public sealed class AuditEventViewModel
{
    private readonly AuditEvent _event;

    public AuditEventViewModel(AuditEvent auditEvent)
    {
        _event = auditEvent ?? throw new ArgumentNullException(nameof(auditEvent));
    }

    /// <summary>
    /// Time formatted for display.
    /// </summary>
    public string TimeDisplay => _event.Timestamp.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>
    /// The action that was performed.
    /// </summary>
    public string Action => _event.Action;

    /// <summary>
    /// The actor that performed the action.
    /// </summary>
    public string Actor => _event.Actor;

    /// <summary>
    /// The result of the action.
    /// </summary>
    public string Result => _event.Result;

    /// <summary>
    /// A brief summary for the list display.
    /// </summary>
    public string Summary
    {
        get
        {
            var target = !string.IsNullOrEmpty(_event.Target) ? $" → {_event.Target}" : "";
            return $"{_event.Action}{target}";
        }
    }

    /// <summary>
    /// Whether this was a successful action.
    /// </summary>
    public bool IsSuccess => _event.Result == "ok";
}
