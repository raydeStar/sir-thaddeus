using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SirThaddeus.Core;
using SirThaddeus.DesktopRuntime.Services;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace SirThaddeus.DesktopRuntime;

/// <summary>
/// First-run onboarding wizard. Guides the user through:
///   1. Connecting to a local LLM provider
///   2. Entering their name / about
///   3. Picking a personality
///   4. (auto) Downloading voice backend assets if needed
/// </summary>
public partial class OnboardingWindow : Window
{
    private int _currentStep = 1;
    private string _selectedBaseUrl = "http://localhost:1234";
    private string _selectedProviderName = "LM Studio";
    private string? _detectedModelName;
    private readonly List<RadioButton> _providerRadios = [];

    // Background asset download state
    private Task? _assetDownloadTask;
    private CancellationTokenSource? _assetCts;
    private volatile bool _assetsReady;
    private volatile bool _assetDownloadFailed;

    // ── Results (read by App.xaml.cs after dialog closes) ──────────
    public string SelectedBaseUrl => _selectedBaseUrl;
    public string SelectedModel => _detectedModelName ?? "";
    public string UserDisplayName => DisplayNameInput.Text.Trim();
    public string UserAboutMe => AboutMeInput.Text.Trim();
    public string SelectedPersonalityId
    {
        get
        {
            if (PersonalitySirThaddeus.IsChecked == true) return "sir_thaddeus";
            if (PersonalityProfessional.IsChecked == true) return "professional";
            return "helpful_default";
        }
    }

    public OnboardingWindow()
    {
        InitializeComponent();
        Icon = Services.BrandIcon.WindowIcon;
        AppIcon.Source = Services.BrandIcon.WindowIcon;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Start background asset download immediately while user
        // goes through the preference steps.
        BeginBackgroundAssetDownload();

        await PopulateProviderListAsync();
        DisplayNameInput.Focus();
    }

    // ────────────────────────────────────────────────────────────────
    // Provider Detection
    // ────────────────────────────────────────────────────────────────

    private async Task PopulateProviderListAsync()
    {
        ConnectionStatus.Text = "Scanning for local LLM providers…";
        ConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xC2));

        var detected = await LlmProviderDetector.DetectAsync();

        ProviderList.Children.Clear();
        _providerRadios.Clear();

        bool anyOnline = false;
        bool firstOnlineSelected = false;

        foreach (var provider in detected)
        {
            var radio = CreateProviderRadio(provider);
            ProviderList.Children.Add(radio);
            _providerRadios.Add(radio);

            if (provider.IsOnline && !firstOnlineSelected)
            {
                radio.IsChecked = true;
                _selectedBaseUrl = provider.BaseUrl;
                _selectedProviderName = provider.Name;
                _detectedModelName = provider.ModelName;
                firstOnlineSelected = true;
                anyOnline = true;
            }
        }

        // Add custom URL option
        var customRadio = CreateCustomUrlRadio();
        ProviderList.Children.Add(customRadio);
        _providerRadios.Add(customRadio);

        if (!firstOnlineSelected && _providerRadios.Count > 0)
            _providerRadios[0].IsChecked = true;

        ConnectionStatus.Text = anyOnline
            ? "✓ Found a running LLM — select one above and click Next."
            : "No LLM detected. Please start your LLM backend and select it above, or enter a custom URL.";
        ConnectionStatus.Foreground = anyOnline
            ? new SolidColorBrush(Color.FromRgb(0x3E, 0xA8, 0x76))
            : new SolidColorBrush(Color.FromRgb(0xC6, 0x9E, 0x58));
    }

    private RadioButton CreateProviderRadio(LlmProviderDetector.DetectedProvider provider)
    {
        var statusDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = provider.IsOnline
                ? new SolidColorBrush(Color.FromRgb(0x3E, 0xA8, 0x76))
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x6B)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var nameText = new TextBlock
        {
            Text = provider.Name,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xF2)),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif")
        };

        var detailText = new TextBlock
        {
            Text = provider.IsOnline
                ? $" — {provider.ModelName ?? "model loaded"}"
                : $" — {provider.BaseUrl}",
            FontSize = 12,
            Foreground = provider.IsOnline
                ? new SolidColorBrush(Color.FromRgb(0x9A, 0xA8, 0xC2))
                : new SolidColorBrush(Color.FromRgb(0x4A, 0x55, 0x6B)),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif")
        };

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal };
        namePanel.Children.Add(statusDot);
        namePanel.Children.Add(nameText);
        namePanel.Children.Add(detailText);

        var radio = new RadioButton
        {
            Content = namePanel,
            GroupName = "LlmProvider",
            Style = (Style)FindResource("ProviderRadioStyle"),
            Tag = provider
        };

        radio.Checked += (_, _) =>
        {
            _selectedBaseUrl = provider.BaseUrl;
            _selectedProviderName = provider.Name;
            _detectedModelName = provider.ModelName;
        };

        return radio;
    }

    private RadioButton CreateCustomUrlRadio()
    {
        var nameText = new TextBlock
        {
            Text = "Custom URL",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xF2)),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif")
        };

        var urlInput = new TextBox
        {
            Width = 280,
            Text = "http://localhost:",
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDF, 0xE5, 0xF2)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x28, 0x2F, 0x3E)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, sans-serif")
        };

        var contentPanel = new StackPanel { Orientation = Orientation.Horizontal };
        contentPanel.Children.Add(nameText);
        contentPanel.Children.Add(urlInput);

        var radio = new RadioButton
        {
            Content = contentPanel,
            GroupName = "LlmProvider",
            Style = (Style)FindResource("ProviderRadioStyle"),
            Tag = "custom"
        };

        radio.Checked += (_, _) =>
        {
            _selectedBaseUrl = urlInput.Text.Trim();
            _selectedProviderName = "Custom";
            _detectedModelName = null;
        };

        urlInput.TextChanged += (_, _) =>
        {
            if (radio.IsChecked == true)
            {
                _selectedBaseUrl = urlInput.Text.Trim();
            }
        };

        return radio;
    }

    // ────────────────────────────────────────────────────────────────
    // Background Asset Download
    // ────────────────────────────────────────────────────────────────

    private void BeginBackgroundAssetDownload()
    {
        var repoRoot = ResolveRepoRoot();
        if (repoRoot == null)
        {
            _assetsReady = true;
            return;
        }

        var manifestPath = System.IO.Path.Combine(repoRoot, "assets", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            _assetsReady = true;
            return;
        }

        try
        {
            var mgr = new AssetManager(repoRoot);
            if (mgr.AllAssetsInstalled())
            {
                _assetsReady = true;
                return;
            }

            _assetCts = new CancellationTokenSource();
            var progress = new Progress<AssetProgress>(OnAssetProgress);
            _assetDownloadTask = Task.Run(async () =>
            {
                try
                {
                    await mgr.EnsureAllAssetsAsync(progress, _assetCts.Token);
                    _assetsReady = true;
                }
                catch (OperationCanceledException) { }
                catch
                {
                    _assetDownloadFailed = true;
                    _assetsReady = true;
                }
            });
        }
        catch
        {
            _assetsReady = true;
        }
    }

    private void OnAssetProgress(AssetProgress p)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => OnAssetProgress(p));
            return;
        }

        if (_currentStep != 4) return;

        var assetNum = p.AssetIndex + 1;
        var totalWidth = 340.0;

        switch (p.Phase)
        {
            case AssetProgressPhase.Checking:
                DownloadAssetLabel.Text = $"Checking {p.Description}...";
                DownloadStatusText.Text = $"Component {assetNum} of {p.TotalAssets}";
                break;

            case AssetProgressPhase.Downloading:
                DownloadAssetLabel.Text = p.Description;
                DownloadStatusText.Text = $"Downloading... {p.DownloadPercent}%  ({assetNum} of {p.TotalAssets})";
                var overallPct = ((p.AssetIndex * 100.0) + p.DownloadPercent) / p.TotalAssets;
                DownloadProgressFill.Width = Math.Max(0, Math.Min(totalWidth, totalWidth * overallPct / 100.0));
                break;

            case AssetProgressPhase.Verifying:
                DownloadAssetLabel.Text = $"Verifying {p.Description}...";
                DownloadStatusText.Text = $"Component {assetNum} of {p.TotalAssets}";
                break;

            case AssetProgressPhase.Extracting:
                DownloadAssetLabel.Text = $"Extracting {p.Description}...";
                DownloadStatusText.Text = $"Component {assetNum} of {p.TotalAssets}";
                break;

            case AssetProgressPhase.Installed:
            case AssetProgressPhase.AlreadyInstalled:
                DownloadProgressFill.Width = totalWidth * (assetNum / (double)p.TotalAssets);
                if (assetNum >= p.TotalAssets)
                {
                    DownloadAssetLabel.Text = "All set!";
                    DownloadStatusText.Text = "Voice components are ready.";
                    DownloadProgressFill.Width = totalWidth;
                    _ = FinishAfterDelayAsync();
                }
                break;
        }

        // If the background task failed, let the user proceed anyway.
        if (_assetDownloadFailed)
        {
            DownloadAssetLabel.Text = "Download issue -- you can continue.";
            DownloadStatusText.Text = "Voice assets can be fetched later from Settings.";
            _ = FinishAfterDelayAsync();
        }
    }

    private async Task FinishAfterDelayAsync()
    {
        await Task.Delay(800);
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => { DialogResult = true; Close(); });
            return;
        }
        DialogResult = true;
        Close();
    }

    private static string? ResolveRepoRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 10 && dir?.Parent is not null; i++)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "assets", "manifest.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // ────────────────────────────────────────────────────────────────
    // Step Navigation
    // ────────────────────────────────────────────────────────────────

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep < 3)
        {
            _currentStep++;
            UpdateStepVisibility();
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            UpdateStepVisibility();
        }
    }

    private System.Windows.Threading.DispatcherTimer? _step4PollTimer;

    private void OnLetsGoClick(object sender, RoutedEventArgs e)
    {
        // If assets are already downloaded (finished during steps 1-3), close now.
        if (_assetsReady)
        {
            DialogResult = true;
            Close();
            return;
        }

        // Otherwise, show the download step with a friendly spinner.
        _currentStep = 4;
        UpdateStepVisibility();
        StartDownloadSpinner();

        // Safety net: poll _assetsReady in case progress callbacks don't fire.
        _step4PollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _step4PollTimer.Tick += (_, _) =>
        {
            if (_assetsReady)
            {
                _step4PollTimer.Stop();
                DownloadAssetLabel.Text = _assetDownloadFailed
                    ? "Download issue -- you can continue."
                    : "All set!";
                DownloadStatusText.Text = _assetDownloadFailed
                    ? "Voice assets can be fetched later from Settings."
                    : "Voice components are ready.";
                DownloadProgressFill.Width = 340;
                _ = FinishAfterDelayAsync();
            }
        };
        _step4PollTimer.Start();
    }

    private void StartDownloadSpinner()
    {
        var rotateAnim = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(1)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        DownloadSpinnerRotation.BeginAnimation(
            System.Windows.Media.RotateTransform.AngleProperty, rotateAnim);
    }

    private void UpdateStepVisibility()
    {
        Step1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _currentStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _currentStep > 1 && _currentStep < 4 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _currentStep < 3 ? Visibility.Visible : Visibility.Collapsed;
        LetsGoButton.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Hide all nav buttons on step 4 (download in progress)
        if (_currentStep == 4)
        {
            BackButton.Visibility = Visibility.Collapsed;
            NextButton.Visibility = Visibility.Collapsed;
            LetsGoButton.Visibility = Visibility.Collapsed;
        }

        // Update step dots
        UpdateDot(Dot1, _currentStep >= 1);
        UpdateDot(Dot2, _currentStep >= 2);
        UpdateDot(Dot3, _currentStep >= 3);
        UpdateDot(Dot4, _currentStep >= 4);

        // Focus the first input on step 2
        if (_currentStep == 2 && string.IsNullOrWhiteSpace(DisplayNameInput.Text))
            DisplayNameInput.Focus();
    }

    private void UpdateDot(Ellipse dot, bool active)
    {
        dot.Style = (Style)FindResource(active ? "StepDotActive" : "StepDotInactive");
    }
}
