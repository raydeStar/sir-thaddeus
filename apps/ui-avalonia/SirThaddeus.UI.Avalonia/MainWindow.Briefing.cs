using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using SirThaddeus.Contracts;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using SirThaddeus.UI.Avalonia.ViewModels;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private readonly ObservableCollection<BriefingHistoryListItem> _briefingHistoryItems = [];
    private readonly ObservableCollection<BriefingCardListItem> _briefingCardItems = [];
    private readonly ObservableCollection<BriefingAuditListItem> _briefingAuditItems = [];
    private readonly ObservableCollection<BriefingSourceListItem> _briefingSourceItems = [];
    private readonly Dictionary<ChatSessionItem, ChatSessionBriefingState> _briefingBySession = [];
    private bool _suppressBriefingHistorySelection;
    private DeepDiveBriefingDto? _currentBriefing;

    private void InitializeBriefingUi()
    {
        BriefingHistoryList.ItemsSource = _briefingHistoryItems;
        BriefingCardsList.ItemsSource = _briefingCardItems;
        BriefingAuditList.ItemsSource = _briefingAuditItems;
        BriefingSourcesList.ItemsSource = _briefingSourceItems;
        UpdateBriefingHistoryHint();
        ResetBriefingDetailUi();
    }

    private void LoadBriefingForSession(ChatSessionItem session)
    {
        var state = GetOrCreateBriefingState(session);
        RefreshBriefingHistory(session, state.ActiveBriefing);

        var briefing = state.ActiveBriefing ?? state.History.FirstOrDefault()?.Briefing;
        if (briefing is null)
        {
            ResetBriefingDetailUi();
            SelectBriefingHistoryItem(null);
            return;
        }

        state.ActiveBriefing = briefing;
        RenderBriefing(briefing);
        SelectBriefingHistoryItem(briefing);
    }

    private void DisplayBriefing(DeepDiveBriefingDto briefing, bool recordHistory, bool activateTab)
    {
        ArgumentNullException.ThrowIfNull(briefing);

        var state = GetOrCreateBriefingState(_currentSession);
        if (recordHistory)
        {
            state.Record(briefing);
        }
        else
        {
            state.ActiveBriefing = briefing;
        }

        RefreshBriefingHistory(_currentSession, briefing);
        RenderBriefing(briefing);

        if (activateTab)
        {
            SetActiveView(BriefingTabButton);
        }
    }

    private void RefreshBriefingHistory(ChatSessionItem session, DeepDiveBriefingDto? selectedBriefing)
    {
        var state = GetOrCreateBriefingState(session);

        _briefingHistoryItems.Clear();
        foreach (var snapshot in state.History)
        {
            _briefingHistoryItems.Add(new BriefingHistoryListItem(
                snapshot.Title,
                snapshot.StatusLine,
                snapshot.RecordedAt.LocalDateTime.ToString("g"),
                FormatConfidenceLabel(snapshot.Confidence),
                GetConfidenceBrush(snapshot.Confidence),
                snapshot.Briefing));
        }

        UpdateBriefingHistoryHint();
        SelectBriefingHistoryItem(selectedBriefing);
    }

    private void SelectBriefingHistoryItem(DeepDiveBriefingDto? briefing)
    {
        _suppressBriefingHistorySelection = true;
        try
        {
            BriefingHistoryList.SelectedItem = briefing is null
                ? null
                : _briefingHistoryItems.FirstOrDefault(item => SameBriefing(item.Briefing, briefing));
        }
        finally
        {
            _suppressBriefingHistorySelection = false;
        }
    }

    private void RenderBriefing(DeepDiveBriefingDto briefing)
    {
        _currentBriefing = briefing;

        BriefingStatusText.Text = BuildBriefingStatusMessage(briefing);
        BriefingEmptyState.IsVisible = false;
        BriefingHeroCard.IsVisible = true;

        BriefingHeroTitleText.Text = briefing.Hero.Title;
        BriefingHeroStatusText.Text = string.IsNullOrWhiteSpace(briefing.Hero.StatusLine)
            ? "Status unavailable."
            : briefing.Hero.StatusLine;
        BriefingConfidenceText.Text = FormatConfidenceLabel(briefing.Hero.Confidence);
        BriefingConfidenceBadge.Background = GetConfidenceBrush(briefing.Hero.Confidence);
        BriefingLastCheckedText.Text = FormatIsoTimestamp(briefing.Hero.LastCheckedIso);
        BriefingClosesText.Text = string.IsNullOrWhiteSpace(briefing.Hero.ClosesText)
            ? "Not provided"
            : briefing.Hero.ClosesText;

        BriefingAddressText.Text = ValueOrFallback(briefing.Hero.Address);
        BriefingPhoneText.Text = ValueOrFallback(briefing.Hero.Phone);
        BriefingQueryText.Text = ValueOrFallback(briefing.Topic.Query);

        BriefingWebsiteButton.IsEnabled = !string.IsNullOrWhiteSpace(briefing.Hero.Website);
        BriefingDirectionsButton.IsEnabled = !string.IsNullOrWhiteSpace(briefing.Hero.DirectionsUrl);
        BriefingCopyPhoneButton.IsEnabled = !string.IsNullOrWhiteSpace(briefing.Hero.Phone);
        BriefingCopyAddressButton.IsEnabled = !string.IsNullOrWhiteSpace(briefing.Hero.Address);

        _briefingCardItems.Clear();
        foreach (var card in briefing.Cards)
        {
            _briefingCardItems.Add(new BriefingCardListItem(
                card.Title,
                card.Type.ToUpperInvariant(),
                card.Bullets.Count == 0 ? ["No structured bullets were returned."] : card.Bullets,
                BuildSourceSummary(card.Sources)));
        }

        BriefingCardsList.IsVisible = _briefingCardItems.Count > 0;

        _briefingAuditItems.Clear();
        foreach (var step in briefing.Audit)
        {
            _briefingAuditItems.Add(new BriefingAuditListItem(
                step.Step.Replace('_', ' ').ToUpperInvariant(),
                step.Detail,
                FormatIsoTimestamp(step.TimestampIso),
                BuildSourceSummary(step.Sources)));
        }

        BriefingAuditSection.IsVisible = _briefingAuditItems.Count > 0;

        _briefingSourceItems.Clear();
        foreach (var source in CollectBriefingSources(briefing))
        {
            _briefingSourceItems.Add(new BriefingSourceListItem(
                source.Name,
                source.Url,
                BuildSourceMeta(source)));
        }

        BriefingSourcesSection.IsVisible = _briefingSourceItems.Count > 0;

        if (briefing.Map is null)
        {
            BriefingMapSection.IsVisible = false;
            BriefingMapLabelText.Text = "Map";
            BriefingMapCoordsText.Text = "-";
        }
        else
        {
            BriefingMapSection.IsVisible = true;
            BriefingMapLabelText.Text = ValueOrFallback(briefing.Map.Label);
            BriefingMapCoordsText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "{0:F5}, {1:F5}",
                briefing.Map.Latitude,
                briefing.Map.Longitude);
        }
    }

    private void ResetBriefingDetailUi()
    {
        _currentBriefing = null;
        BriefingStatusText.Text = "No briefing loaded yet. Ask for a place or product deep dive in chat.";
        BriefingEmptyState.IsVisible = true;
        BriefingHeroCard.IsVisible = false;
        BriefingCardsList.IsVisible = false;
        BriefingAuditSection.IsVisible = false;
        BriefingSourcesSection.IsVisible = false;
        BriefingMapSection.IsVisible = false;

        BriefingHeroTitleText.Text = "Briefing title";
        BriefingHeroStatusText.Text = "Status line";
        BriefingConfidenceText.Text = "UNKNOWN";
        BriefingConfidenceBadge.Background = (IBrush?)this.FindResource("Surface0Brush") ?? Brushes.Gray;
        BriefingLastCheckedText.Text = "-";
        BriefingClosesText.Text = "-";
        BriefingAddressText.Text = "-";
        BriefingPhoneText.Text = "-";
        BriefingQueryText.Text = "-";
        BriefingMapLabelText.Text = "Map";
        BriefingMapCoordsText.Text = "-";

        BriefingWebsiteButton.IsEnabled = false;
        BriefingDirectionsButton.IsEnabled = false;
        BriefingCopyPhoneButton.IsEnabled = false;
        BriefingCopyAddressButton.IsEnabled = false;

        _briefingCardItems.Clear();
        _briefingAuditItems.Clear();
        _briefingSourceItems.Clear();
    }

    private ChatSessionBriefingState GetOrCreateBriefingState(ChatSessionItem session)
    {
        if (!_briefingBySession.TryGetValue(session, out var state))
        {
            state = new ChatSessionBriefingState();
            _briefingBySession[session] = state;
        }

        return state;
    }

    private void UpdateBriefingHistoryHint()
    {
        BriefingHistoryHintText.Text = _briefingHistoryItems.Count == 0
            ? "Deep-dive results from this conversation appear here."
            : $"{_briefingHistoryItems.Count} briefing{(_briefingHistoryItems.Count == 1 ? string.Empty : "s")} stored in this conversation.";
    }

    private static string BuildBriefingStatusMessage(DeepDiveBriefingDto briefing)
    {
        var suffix = briefing.Hero.Confidence.Trim().ToLowerInvariant() switch
        {
            "high" => "ready.",
            "medium" => "ready. Double-check important details.",
            "low" => "loaded. Verify key details before acting.",
            _ => "loaded."
        };

        return $"{briefing.Hero.Title} briefing {suffix}";
    }

    private IBrush GetConfidenceBrush(string? confidence)
    {
        var resourceKey = confidence?.Trim().ToLowerInvariant() switch
        {
            "high" => "GreenBrush",
            "medium" => "YellowBrush",
            "low" => "RedBrush",
            _ => "Overlay0Brush"
        };

        return (IBrush?)this.FindResource(resourceKey) ?? Brushes.Gray;
    }

    /// <summary>
    /// Maps raw confidence strings to compact, user-friendly labels.
    /// </summary>
    private static string FormatConfidenceLabel(string? confidence)
    {
        return confidence?.Trim().ToLowerInvariant() switch
        {
            "high" => "Verified",
            "medium" => "Partial",
            "low" => "Unverified",
            _ => "Unknown"
        };
    }

    private static string FormatIsoTimestamp(string? iso)
    {
        if (DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.LocalDateTime.ToString("g");
        }

        return string.IsNullOrWhiteSpace(iso) ? "Unknown" : iso;
    }

    private static string ValueOrFallback(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static string BuildSourceSummary(IReadOnlyList<BriefingSourceRefDto> sources)
    {
        if (sources.Count == 0)
        {
            return "";
        }

        var names = sources
            .Select(source => source.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        return names.Length == 0
            ? ""
            : "Sources: " + string.Join(", ", names);
    }

    private static IReadOnlyList<BriefingSourceRefDto> CollectBriefingSources(DeepDiveBriefingDto briefing)
    {
        IEnumerable<BriefingSourceRefDto> websiteSource = string.IsNullOrWhiteSpace(briefing.Hero.Website)
            ? Array.Empty<BriefingSourceRefDto>()
            : new[] { new BriefingSourceRefDto("Official website", briefing.Hero.Website, briefing.Hero.LastCheckedIso) };

        return briefing.Cards
            .SelectMany(card => card.Sources)
            .Concat(briefing.Audit.SelectMany(step => step.Sources))
            .Concat(websiteSource)
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .GroupBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static string BuildSourceMeta(BriefingSourceRefDto source)
    {
        var host = Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            ? uri.Host
            : source.Url;

        return $"{host} | checked {FormatIsoTimestamp(source.FetchedIso)}";
    }

    private static bool SameBriefing(DeepDiveBriefingDto left, DeepDiveBriefingDto right)
    {
        return string.Equals(left.Hero.Title, right.Hero.Title, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Topic.Query, right.Topic.Query, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Hero.LastCheckedIso, right.Hero.LastCheckedIso, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<string> BuildAssistantSourceList(string text, DeepDiveBriefingDto? briefing)
    {
        var urls = new List<string>(ExtractUrls(text));
        if (briefing is null)
        {
            return urls;
        }

        foreach (var source in CollectBriefingSources(briefing))
        {
            if (!urls.Contains(source.Url, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(source.Url);
            }
        }

        return urls;
    }

    private async void BriefingCopyPhoneButton_Click(object? sender, RoutedEventArgs e)
    {
        await CopyBriefingValueAsync(_currentBriefing?.Hero.Phone, "phone number");
    }

    private async void BriefingCopyAddressButton_Click(object? sender, RoutedEventArgs e)
    {
        await CopyBriefingValueAsync(_currentBriefing?.Hero.Address, "address");
    }

    private void BriefingWebsiteButton_Click(object? sender, RoutedEventArgs e)
    {
        OpenExternalUrl(_currentBriefing?.Hero.Website);
    }

    private void BriefingDirectionsButton_Click(object? sender, RoutedEventArgs e)
    {
        OpenExternalUrl(_currentBriefing?.Hero.DirectionsUrl);
    }

    private void BriefingSourceOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag as string;
        OpenExternalUrl(url);
    }

    private void BriefingHistoryList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressBriefingHistorySelection || BriefingHistoryList.SelectedItem is not BriefingHistoryListItem item)
        {
            return;
        }

        var state = GetOrCreateBriefingState(_currentSession);
        state.ActiveBriefing = item.Briefing;
        DisplayBriefing(item.Briefing, recordHistory: false, activateTab: false);
    }

    private async Task CopyBriefingValueAsync(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            AppendTranscript($"[system] No {label} available in the current briefing.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(value);
        AppendTranscript($"[system] Copied briefing {label}.");
    }

    private void OpenExternalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AppendTranscript("[system] No URL is available for this briefing action.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Failed to open link: " + ex.Message);
        }
    }

    private sealed class ChatSessionBriefingState
    {
        public List<BriefingHistorySnapshot> History { get; } = [];

        public DeepDiveBriefingDto? ActiveBriefing { get; set; }

        public void Record(DeepDiveBriefingDto briefing)
        {
            ActiveBriefing = briefing;
            var snapshot = new BriefingHistorySnapshot(
                briefing.Hero.Title,
                briefing.Hero.Confidence,
                string.IsNullOrWhiteSpace(briefing.Hero.StatusLine) ? "Status unavailable." : briefing.Hero.StatusLine,
                DateTimeOffset.Now,
                briefing);

            if (History.Count > 0 && SameBriefing(History[0].Briefing, briefing))
            {
                History[0] = snapshot;
                return;
            }

            History.Insert(0, snapshot);
            while (History.Count > 24)
            {
                History.RemoveAt(History.Count - 1);
            }
        }
    }

    private sealed record BriefingHistorySnapshot(
        string Title,
        string Confidence,
        string StatusLine,
        DateTimeOffset RecordedAt,
        DeepDiveBriefingDto Briefing);

    private sealed record BriefingHistoryListItem(
        string Title,
        string StatusLine,
        string UpdatedLabel,
        string ConfidenceLabel,
        IBrush ConfidenceBrush,
        DeepDiveBriefingDto Briefing);

    private sealed record BriefingCardListItem(
        string Title,
        string TypeLabel,
        IReadOnlyList<string> Bullets,
        string SourceSummary);

    private sealed record BriefingAuditListItem(
        string StepLabel,
        string Detail,
        string TimestampLabel,
        string SourceSummary);

    private sealed record BriefingSourceListItem(
        string Name,
        string Url,
        string Meta);
}



