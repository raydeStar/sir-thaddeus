namespace Thaddeus.Shell.Windows;

/// <summary>
/// Delegate used when the workspace window receives a native close request.
/// Return <c>true</c> to cancel the close, <c>false</c> to allow it.
/// </summary>
public delegate bool WorkspaceWindowClosingHandler();

/// <summary>
/// Surface the shell can drive after the main Photino window exists.
/// This keeps tray and shutdown behavior testable without constructing GUI state.
/// </summary>
public interface IWorkspaceWindowSurface
{
    /// <summary>True when the workspace surface is currently visible.</summary>
    bool IsVisible { get; }

    /// <summary>Raised when the native host asks to close the workspace window.</summary>
    event WorkspaceWindowClosingHandler? ClosingRequested;

    /// <summary>Show or restore the workspace surface.</summary>
    void Show();

    /// <summary>Hide the workspace surface without terminating the process.</summary>
    void Hide();

    /// <summary>Request a real window close.</summary>
    void Close();
}