namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// Borderless splash screen shown during application startup.
/// Displays a spinner and live status text so the user knows
/// the application is initializing rather than silently failing.
/// </summary>
public partial class SplashWindow : System.Windows.Window
{
    public SplashWindow()
    {
        InitializeComponent();

        // Icon paths must be resolved at runtime — relative XAML paths
        // don't work in single-file publish. BrandIcon handles this.
        Icon = Services.BrandIcon.WindowIcon;
        AppIcon.Source = Services.BrandIcon.WindowIcon;
    }

    /// <summary>
    /// Updates the status text shown below the spinner.
    /// Safe to call from the UI thread only.
    /// </summary>
    public void SetStatus(string status)
    {
        StatusText.Text = status;
    }

    /// <summary>
    /// Fades out and closes the splash window.
    /// </summary>
    public void FadeOutAndClose()
    {
        var fadeOut = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 1,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(250)
        };
        fadeOut.Completed += (_, _) =>
        {
            Close();
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
