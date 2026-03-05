using Avalonia.Controls;
using Avalonia.Interactivity;
using SirThaddeus.Contracts;
using System.Net.Http;
using System.Text;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow : Window
{
    private RuntimeApiClient? _runtimeApiClient;
    private string? _activeRunId;
    private readonly StringBuilder _transcript = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ConnectButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var baseUrl = RuntimeUrlBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                ConnectionStatusText.Text = "Invalid URL";
                return;
            }

            var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _runtimeApiClient = new RuntimeApiClient(httpClient);
            var health = await _runtimeApiClient.GetHealthAsync(CancellationToken.None);
            ConnectionStatusText.Text = health is null
                ? "Connected (no health payload)"
                : $"Connected: {health.Version}";
            SettingsRuntimeText.Text = $"Runtime: {baseUrl}";
        }
        catch (Exception ex)
        {
            ConnectionStatusText.Text = $"Connect failed: {ex.Message}";
        }
    }

    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null)
        {
            AppendTranscript("[system] Connect to runtime first.");
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        try
        {
            AppendTranscript($"[user] {prompt}");
            var run = await _runtimeApiClient.StartRunAsync(prompt, CancellationToken.None);
            _activeRunId = run.RunId;
            AppendTranscript($"[system] Run started: {run.RunId}");
            PromptBox.Text = string.Empty;
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] {ex.Message}");
        }
    }

    private async void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null || string.IsNullOrWhiteSpace(_activeRunId))
        {
            AppendTranscript("[system] No active run to cancel.");
            return;
        }

        try
        {
            var accepted = await _runtimeApiClient.CancelRunAsync(_activeRunId, CancellationToken.None);
            AppendTranscript(accepted
                ? $"[system] STOP accepted for {_activeRunId}"
                : $"[system] STOP rejected for {_activeRunId}");
        }
        catch (Exception ex)
        {
            AppendTranscript($"[error] Cancel failed: {ex.Message}");
        }
    }

    private async void RefreshAuditButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            var entries = await _runtimeApiClient.GetAuditAsync(CancellationToken.None);
            AuditList.ItemsSource = entries.Select(ToAuditLine).ToArray();
        }
        catch (Exception ex)
        {
            AuditList.ItemsSource = new[] { $"Audit load failed: {ex.Message}" };
        }
    }

    private void AppendTranscript(string line)
    {
        _transcript.AppendLine(line);
        TranscriptBox.Text = _transcript.ToString();
    }

    private static string ToAuditLine(AuditEntryDto dto)
    {
        return $"{dto.TimestampUtc:O} [{dto.Category}] {dto.Message}";
    }
}
