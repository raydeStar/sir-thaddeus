using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using SirThaddeus.Agent.Search.DeepDive;

namespace SirThaddeus.DesktopRuntime.ViewModels;

public enum BriefingPanelState
{
    /// <summary>Nothing requested yet — show a welcome hint.</summary>
    Idle,
    Loading,
    Success,
    Partial,
    Failure
}

/// <summary>
/// Presentation state for the dedicated deep-dive briefing panel.
/// Exposes hero fields, sidebar sources, and briefing history for the UI.
/// </summary>
public sealed class BriefingPanelViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private BriefingPanelState _state = BriefingPanelState.Idle;
    private string _statusMessage = "";
    private DeepDiveBriefing? _currentBriefing;

    public BriefingPanelViewModel()
    {
        OpenUrlCommand = new RelayCommand(OpenUrl, CanOpenUrl);
        CopyTextCommand = new RelayCommand(CopyText, CanCopyText);
        LoadHistoryCommand = new RelayCommand(LoadHistoryItem);
    }

    // ────────────────────────────────────────────
    //  State
    // ────────────────────────────────────────────

    public BriefingPanelState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
                return;

            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(IsLoading));
            OnPropertyChanged(nameof(IsSuccess));
            OnPropertyChanged(nameof(IsPartial));
            OnPropertyChanged(nameof(IsFailure));
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public DeepDiveBriefing? CurrentBriefing
    {
        get => _currentBriefing;
        private set
        {
            if (!SetProperty(ref _currentBriefing, value))
                return;

            OnPropertyChanged(nameof(HasBriefing));
            OnPropertyChanged(nameof(HasMap));
            OnPropertyChanged(nameof(MapTitle));
            OnPropertyChanged(nameof(MapCoordinates));
            OnPropertyChanged(nameof(HasWebsite));
            OnPropertyChanged(nameof(HasDirections));
            OnPropertyChanged(nameof(HasPhone));
            OnPropertyChanged(nameof(HasAddress));
            OnPropertyChanged(nameof(WebsiteUrl));
            OnPropertyChanged(nameof(DirectionsUrl));
            OnPropertyChanged(nameof(PhoneText));
            OnPropertyChanged(nameof(SidebarSources));
            OnPropertyChanged(nameof(HasSidebarSources));
            OnPropertyChanged(nameof(ReviewSnippets));
            OnPropertyChanged(nameof(HasReviewSnippets));
        }
    }

    // ────────────────────────────────────────────
    //  Computed properties
    // ────────────────────────────────────────────

    public bool IsIdle => State == BriefingPanelState.Idle;
    public bool IsLoading => State == BriefingPanelState.Loading;
    public bool IsSuccess => State == BriefingPanelState.Success;
    public bool IsPartial => State == BriefingPanelState.Partial;
    public bool IsFailure => State == BriefingPanelState.Failure;
    public bool HasBriefing => CurrentBriefing is not null;
    public bool HasMap => CurrentBriefing?.Map is not null;

    public string MapTitle => CurrentBriefing?.Map?.Label ?? "Map preview";
    public string MapCoordinates => CurrentBriefing?.Map is null
        ? "No coordinates available."
        : $"{CurrentBriefing.Map.Latitude:F5}, {CurrentBriefing.Map.Longitude:F5}";

    public bool HasWebsite => !string.IsNullOrWhiteSpace(CurrentBriefing?.Hero?.Website);
    public bool HasDirections => !string.IsNullOrWhiteSpace(CurrentBriefing?.Hero?.DirectionsUrl);
    public bool HasPhone => !string.IsNullOrWhiteSpace(CurrentBriefing?.Hero?.Phone);
    public bool HasAddress => !string.IsNullOrWhiteSpace(CurrentBriefing?.Hero?.Address);
    public string WebsiteUrl => CurrentBriefing?.Hero?.Website ?? "";
    public string DirectionsUrl => CurrentBriefing?.Hero?.DirectionsUrl ?? "";
    public string PhoneText => CurrentBriefing?.Hero?.Phone ?? "";

    // ────────────────────────────────────────────
    //  Sidebar: aggregated sources + review snippets
    // ────────────────────────────────────────────

    /// <summary>
    /// Unique source references aggregated from all cards, de-duped by URL.
    /// </summary>
    public IReadOnlyList<SourceRef> SidebarSources =>
        CurrentBriefing?.Cards
            .SelectMany(c => c.Sources)
            .GroupBy(s => s.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList() ?? [];

    public bool HasSidebarSources => SidebarSources.Count > 0;

    /// <summary>
    /// First few review bullets for the sidebar highlight.
    /// </summary>
    public IReadOnlyList<string> ReviewSnippets
    {
        get
        {
            var card = CurrentBriefing?.Cards.FirstOrDefault(c =>
                c.Type.Equals("reviews", StringComparison.OrdinalIgnoreCase));

            return card?.Bullets.Take(3).ToList() ?? [];
        }
    }

    public bool HasReviewSnippets => ReviewSnippets.Count > 0;

    // ────────────────────────────────────────────
    //  Briefing history (session-scoped)
    // ────────────────────────────────────────────

    public ObservableCollection<BriefingHistoryEntry> History { get; } = [];

    public ICommand OpenUrlCommand { get; }
    public ICommand CopyTextCommand { get; }
    public ICommand LoadHistoryCommand { get; }

    public bool TrySetBriefing(DeepDiveBriefing? briefing, out IReadOnlyList<string> errors)
    {
        if (!DeepDiveBriefingValidator.TryValidate(briefing, out errors))
        {
            CurrentBriefing = null;
            State = BriefingPanelState.Failure;
            StatusMessage = "Briefing payload failed validation.";
            return false;
        }

        // Ensure mapping logic stays contract-compatible for the UI layer.
        _ = DeepDiveBriefingViewModelProjection.Map(briefing!);

        CurrentBriefing = briefing;
        State = briefing!.Hero.Confidence.Equals(DeepDiveConstants.ConfidenceLow, StringComparison.OrdinalIgnoreCase)
            ? BriefingPanelState.Partial
            : BriefingPanelState.Success;
        StatusMessage = BuildStatusMessage(briefing);
        RecordHistory(briefing);
        return true;
    }

    public bool TrySetBriefingFromJson(string json, out IReadOnlyList<string> errors)
    {
        if (!DeepDiveBriefingValidator.TryParseAndValidateJson(json, out var briefing, out errors))
        {
            CurrentBriefing = null;
            State = BriefingPanelState.Failure;
            StatusMessage = "Fixture payload is invalid.";
            return false;
        }

        return TrySetBriefing(briefing, out errors);
    }

    public bool TryLoadFixture(string fixturePath, out IReadOnlyList<string> errors)
    {
        State = BriefingPanelState.Loading;
        StatusMessage = "Loading fixture briefing...";
        CurrentBriefing = null;

        try
        {
            var json = File.ReadAllText(fixturePath);
            return TrySetBriefingFromJson(json, out errors);
        }
        catch (Exception ex)
        {
            errors = [$"Could not load fixture '{fixturePath}': {ex.Message}"];
            CurrentBriefing = null;
            State = BriefingPanelState.Failure;
            StatusMessage = "Fixture file could not be loaded.";
            return false;
        }
    }

    public void SetLoading(string message = "Preparing deep-dive briefing...")
    {
        State = BriefingPanelState.Loading;
        StatusMessage = message;
    }

    public void SetFailure(string message)
    {
        CurrentBriefing = null;
        State = BriefingPanelState.Failure;
        StatusMessage = string.IsNullOrWhiteSpace(message) ? "Briefing failed." : message;
    }

    private string BuildStatusMessage(DeepDiveBriefing briefing)
    {
        var suffix = briefing.Hero.Confidence.Equals(DeepDiveConstants.ConfidenceHigh, StringComparison.OrdinalIgnoreCase)
            ? "ready"
            : "ready (verify important details)";
        return $"{briefing.Hero.Title} briefing is {suffix}.";
    }

    // ────────────────────────────────────────────
    //  Commands
    // ────────────────────────────────────────────

    private bool CanOpenUrl(object? parameter) =>
        parameter is string text && !string.IsNullOrWhiteSpace(text);

    private void OpenUrl(object? parameter)
    {
        if (parameter is not string url || string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Link open failures are non-fatal.
        }
    }

    private bool CanCopyText(object? parameter) =>
        parameter is string text && !string.IsNullOrWhiteSpace(text);

    private void CopyText(object? parameter)
    {
        if (parameter is not string text || string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch
        {
            // Clipboard can fail when the owner window is not active.
        }
    }

    private void LoadHistoryItem(object? parameter)
    {
        if (parameter is not BriefingHistoryEntry entry)
            return;

        CurrentBriefing = entry.Briefing;
        State = entry.Briefing?.Hero.Confidence.Equals(
            DeepDiveConstants.ConfidenceLow, StringComparison.OrdinalIgnoreCase) == true
                ? BriefingPanelState.Partial
                : BriefingPanelState.Success;
        if (entry.Briefing is not null)
            StatusMessage = BuildStatusMessage(entry.Briefing);
    }

    // ────────────────────────────────────────────
    //  History helpers
    // ────────────────────────────────────────────

    private void RecordHistory(DeepDiveBriefing briefing)
    {
        // Avoid duplicate consecutive entries for the same topic.
        if (History.Count > 0 &&
            History[0].Title.Equals(briefing.Hero.Title, StringComparison.OrdinalIgnoreCase))
            return;

        History.Insert(0, new BriefingHistoryEntry
        {
            Title = briefing.Hero.Title,
            Confidence = briefing.Hero.Confidence,
            StatusLine = briefing.Hero.StatusLine,
            Timestamp = DateTime.Now,
            Briefing = briefing
        });

        // Keep a reasonable cap.
        const int maxHistory = 50;
        while (History.Count > maxHistory)
            History.RemoveAt(History.Count - 1);
    }
}

/// <summary>
/// Lightweight entry for the briefing history sidebar.
/// Retains the full briefing for instant reload without re-querying.
/// </summary>
public sealed class BriefingHistoryEntry
{
    public string Title { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string StatusLine { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public DeepDiveBriefing? Briefing { get; set; }

    public string TimestampDisplay => Timestamp.ToString("MMM d, h:mm tt");

    public BriefingHistoryEntry() { }

    public BriefingHistoryEntry(string title, string confidence, string statusLine, DateTime timestamp, DeepDiveBriefing briefing)
    {
        Title = title;
        Confidence = confidence;
        StatusLine = statusLine;
        Timestamp = timestamp;
        Briefing = briefing;
    }
}
