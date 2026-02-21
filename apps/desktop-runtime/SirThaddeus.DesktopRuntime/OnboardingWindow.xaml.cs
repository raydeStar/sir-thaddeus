using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
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
/// </summary>
public partial class OnboardingWindow : Window
{
    private int _currentStep = 1;
    private string _selectedBaseUrl = "http://localhost:1234";
    private string _selectedProviderName = "LM Studio";
    private string? _detectedModelName;
    private readonly List<RadioButton> _providerRadios = [];

    // ── Results (read by App.xaml.cs after dialog closes) ──────────
    public string SelectedBaseUrl => _selectedBaseUrl;
    public string SelectedModel => _detectedModelName ?? "local-model";
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
            Width = 8, Height = 8,
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

    private void OnLetsGoClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void UpdateStepVisibility()
    {
        Step1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.Visibility = _currentStep > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _currentStep < 3 ? Visibility.Visible : Visibility.Collapsed;
        LetsGoButton.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Update step dots
        UpdateDot(Dot1, _currentStep >= 1);
        UpdateDot(Dot2, _currentStep >= 2);
        UpdateDot(Dot3, _currentStep >= 3);

        // Focus the first input on step 2
        if (_currentStep == 2 && string.IsNullOrWhiteSpace(DisplayNameInput.Text))
            DisplayNameInput.Focus();
    }

    private void UpdateDot(Ellipse dot, bool active)
    {
        dot.Style = (Style)FindResource(active ? "StepDotActive" : "StepDotInactive");
    }
}
