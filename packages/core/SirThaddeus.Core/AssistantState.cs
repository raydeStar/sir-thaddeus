namespace SirThaddeus.Core;

/// <summary>
/// Represents the explicit runtime states of the assistant.
/// These states are always visible to the user via the overlay.
/// </summary>
public enum AssistantState
{
    /// <summary>
    /// The assistant is completely stopped. No background activity.
    /// </summary>
    Off,

    /// <summary>
    /// The assistant is running but not actively performing any task.
    /// </summary>
    Idle,

    /// <summary>
    /// Actively capturing audio input from the user.
    /// </summary>
    Listening,

    /// <summary>
    /// Processing a request or generating a response.
    /// </summary>
    Thinking,

    /// <summary>
    /// Reading screen content for context.
    /// </summary>
    ReadingScreen,

    /// <summary>
    /// Actively controlling the browser.
    /// </summary>
    BrowserControl,

    /// <summary>
    /// A remote service is performing work on behalf of the user.
    /// </summary>
    ServiceWorking
}

/// <summary>
/// Extension methods for <see cref="AssistantState"/>.
/// </summary>
public static class AssistantStateExtensions
{
    /// <summary>
    /// Gets the display label for the state.
    /// </summary>
    public static string ToDisplayLabel(this AssistantState state) => state switch
    {
        AssistantState.Off => "Off",
        AssistantState.Idle => "Idle",
        AssistantState.Listening => "Listening...",
        AssistantState.Thinking => "Thinking...",
        AssistantState.ReadingScreen => "Reading Screen",
        AssistantState.BrowserControl => "Browser Control",
        AssistantState.ServiceWorking => "Service Working",
        _ => state.ToString()
    };

    /// <summary>
    /// Gets the icon hint for the state (for UI binding).
    /// </summary>
    public static string ToIconHint(this AssistantState state) => state switch
    {
        AssistantState.Off => "power_off",
        AssistantState.Idle => "check_circle",
        AssistantState.Listening => "mic",
        AssistantState.Thinking => "hourglass",
        AssistantState.ReadingScreen => "visibility",
        AssistantState.BrowserControl => "mouse",
        AssistantState.ServiceWorking => "cloud",
        _ => "help"
    };
}
