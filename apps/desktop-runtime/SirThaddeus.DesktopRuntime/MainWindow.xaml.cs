using System.Windows;
using System.Windows.Input;
using SirThaddeus.DesktopRuntime.ViewModels;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// The overlay window - a small pill that anchors to the corner of the screen.
/// </summary>
public partial class MainWindow : Window
{
    private OverlayViewModel? _viewModel;
    private System.Windows.Point? _pillMouseDownPoint;
    private bool _pillDragStarted;
    private const double DragThresholdPx = 4.0;

    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Sets the ViewModel for this window.
    /// </summary>
    public void SetViewModel(OverlayViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Record a baseline; we'll only start dragging if the mouse moves far enough.
        // This keeps "click to toggle" responsive while still allowing repositioning.
        _pillMouseDownPoint = e.GetPosition(this);
        _pillDragStarted = false;

        if (sender is UIElement element)
        {
            element.CaptureMouse();
        }

        e.Handled = true;
    }

    private void Pill_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_pillMouseDownPoint == null)
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _pillMouseDownPoint = null;
            _pillDragStarted = false;
            if (sender is UIElement element)
                element.ReleaseMouseCapture();
            return;
        }

        if (_pillDragStarted)
            return;

        var current = e.GetPosition(this);
        var delta = current - _pillMouseDownPoint.Value;

        if (Math.Abs(delta.X) < DragThresholdPx && Math.Abs(delta.Y) < DragThresholdPx)
            return;

        _pillDragStarted = true;

        if (sender is UIElement element2)
            element2.ReleaseMouseCapture();

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove can throw if called at an awkward time; ignore and keep UI responsive.
        }
        finally
        {
            _pillMouseDownPoint = null;
        }

        e.Handled = true;
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is UIElement element)
            element.ReleaseMouseCapture();

        try
        {
            // If we didn't start a drag, treat it as a click.
            if (_pillMouseDownPoint != null && !_pillDragStarted)
            {
                _viewModel?.ToggleDrawerCommand.Execute(null);
            }
        }
        finally
        {
            _pillMouseDownPoint = null;
            _pillDragStarted = false;
        }

        e.Handled = true;
    }

    private void PositionWindow()
    {
        // Position in the bottom-right corner of the primary screen
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Bottom - ActualHeight - 20;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.Dispose();
        base.OnClosed(e);
    }
}
