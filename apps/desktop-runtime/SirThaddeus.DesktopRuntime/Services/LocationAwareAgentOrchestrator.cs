using System.Text.RegularExpressions;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Config;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Runtime wrapper that enforces manual-location follow-up flow:
/// - ask for location when "near me" requests arrive without a location
/// - confirm stale location (>30 days) before continuing
/// - refresh timestamp on "yes", then continue original query
/// </summary>
public sealed class LocationAwareAgentOrchestrator : IAgentOrchestrator
{
    private readonly IAgentOrchestrator _inner;
    private readonly Func<AppSettings?> _getSettings;
    private readonly Func<string?> _getActiveProfileId;
    private readonly Func<string, AppSettings?> _saveManualLocation;
    private readonly Func<AppSettings?> _touchManualLocationTimestamp;
    private readonly Action<AppSettings?> _applySettings;
    private readonly Action? _queueManualLocationPrompt;
    private readonly TimeProvider _timeProvider;

    private readonly object _gate = new();
    private PendingLocationState _pending = PendingLocationState.None;
    private readonly HashSet<string> _vettingAskedProfileKeys = new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan StaleLocationWindow = TimeSpan.FromDays(30);
    private const string VettingQuestion =
        "Where are you located? This is so I can answer 'near me' questions and is not mandatory. " +
        "This information will never be shared with anyone.";
    private const string NearMePrompt =
        "Where are you located? Share your city, state, or ZIP and I can look nearby.";

    private static readonly Regex ZipRegex = new(
        @"\b\d{5}(?:-\d{4})?\b",
        RegexOptions.Compiled);

    public LocationAwareAgentOrchestrator(
        IAgentOrchestrator inner,
        Func<AppSettings?> getSettings,
        Func<string?> getActiveProfileId,
        Func<string, AppSettings?> saveManualLocation,
        Func<AppSettings?> touchManualLocationTimestamp,
        Action<AppSettings?> applySettings,
        Action? queueManualLocationPrompt = null,
        TimeProvider? timeProvider = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _getSettings = getSettings ?? throw new ArgumentNullException(nameof(getSettings));
        _getActiveProfileId = getActiveProfileId ?? throw new ArgumentNullException(nameof(getActiveProfileId));
        _saveManualLocation = saveManualLocation ?? throw new ArgumentNullException(nameof(saveManualLocation));
        _touchManualLocationTimestamp = touchManualLocationTimestamp ?? throw new ArgumentNullException(nameof(touchManualLocationTimestamp));
        _applySettings = applySettings ?? throw new ArgumentNullException(nameof(applySettings));
        _queueManualLocationPrompt = queueManualLocationPrompt;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<AgentResponse> ProcessAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return await _inner.ProcessAsync(userMessage, cancellationToken);

        var trimmed = userMessage.Trim();
        var lower = trimmed.ToLowerInvariant();
        var activeProfileId = _getActiveProfileId();
        var profileKey = AppSettings.NormalizeLocationProfileKey(activeProfileId);
        var settings = _getSettings() ?? new AppSettings();
        var effectiveLocation = settings.GetEffectiveUserLocation(activeProfileId);
        var manualLocation = effectiveLocation.GetResolvedLabel();

        var pending = ReadPendingState();
        if (pending.Mode != PendingLocationMode.None &&
            !string.Equals(pending.ProfileKey, profileKey, StringComparison.OrdinalIgnoreCase))
        {
            WritePendingState(PendingLocationState.None);
            pending = PendingLocationState.None;
        }

        if (pending.Mode != PendingLocationMode.None)
            return await HandlePendingStateAsync(trimmed, lower, pending, cancellationToken);

        if (string.IsNullOrWhiteSpace(manualLocation) &&
            !HasAskedVettingForProfile(profileKey) &&
            ShouldRunFirstTurnVetting(lower))
        {
            MarkVettingAskedForProfile(profileKey);
            WritePendingState(new PendingLocationState(
                PendingLocationMode.AwaitingVettingLocation,
                trimmed,
                "",
                profileKey));

            _queueManualLocationPrompt?.Invoke();
            return BuildPromptResponse(VettingQuestion);
        }

        if (!LooksLikeLocationDependentRequest(lower))
            return await _inner.ProcessAsync(userMessage, cancellationToken);

        if (string.IsNullOrWhiteSpace(manualLocation))
        {
            WritePendingState(new PendingLocationState(
                PendingLocationMode.AwaitingManualLocation,
                trimmed,
                "",
                profileKey));

            return BuildPromptResponse(NearMePrompt);
        }

        if (IsLocationStale(effectiveLocation))
        {
            WritePendingState(new PendingLocationState(
                PendingLocationMode.AwaitingStaleConfirmation,
                trimmed,
                manualLocation,
                profileKey));

            return BuildPromptResponse(
                $"Are you still at {manualLocation}? If yes, I will keep using that location.");
        }

        return await _inner.ProcessAsync(userMessage, cancellationToken);
    }

    public void ResetConversation()
    {
        _inner.ResetConversation();
        WritePendingState(PendingLocationState.None);
    }

    public void SeedDialogueState(DialogueState state) => _inner.SeedDialogueState(state);

    public DialogueContextSnapshot GetContextSnapshot() => _inner.GetContextSnapshot();

    public bool ContextLocked
    {
        get => _inner.ContextLocked;
        set => _inner.ContextLocked = value;
    }

    public Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default) =>
        _inner.GetAvailableToolCountAsync(cancellationToken);

    private async Task<AgentResponse> HandlePendingStateAsync(
        string message,
        string lower,
        PendingLocationState pending,
        CancellationToken cancellationToken)
    {
        if (pending.Mode == PendingLocationMode.AwaitingVettingLocation)
        {
            if (IsSkipOrOptOut(lower) || IsNegative(lower))
            {
                WritePendingState(PendingLocationState.None);
                return await ContinuePendingQueryThroughWrapperAsync(pending.OriginalQuery, cancellationToken);
            }

            if (TryExtractManualLocation(message, out var vettingLocation))
            {
                var updated = _saveManualLocation(vettingLocation);
                _applySettings(updated);
                WritePendingState(PendingLocationState.None);
                return await ContinuePendingQueryThroughWrapperAsync(pending.OriginalQuery, cancellationToken);
            }

            if (LooksLikeLocationDependentRequest(lower))
            {
                WritePendingState(new PendingLocationState(
                    PendingLocationMode.AwaitingManualLocation,
                    message,
                    "",
                    pending.ProfileKey));
                return BuildPromptResponse(NearMePrompt);
            }

            WritePendingState(PendingLocationState.None);
            return await _inner.ProcessAsync(message, cancellationToken);
        }

        if (pending.Mode == PendingLocationMode.AwaitingStaleConfirmation)
        {
            if (IsAffirmative(lower))
            {
                var updated = _touchManualLocationTimestamp();
                _applySettings(updated);
                WritePendingState(PendingLocationState.None);
                return await ContinuePendingQueryAsync(pending.OriginalQuery, cancellationToken);
            }

            if (IsNegative(lower))
            {
                WritePendingState(new PendingLocationState(
                    PendingLocationMode.AwaitingManualLocation,
                    pending.OriginalQuery,
                    "",
                    pending.ProfileKey));

                return BuildPromptResponse(
                    NearMePrompt);
            }

            if (TryExtractManualLocation(message, out var newLocation))
            {
                var updated = _saveManualLocation(newLocation);
                _applySettings(updated);
                WritePendingState(PendingLocationState.None);
                return await ContinuePendingQueryAsync(pending.OriginalQuery, cancellationToken);
            }

            return BuildPromptResponse(
                $"Please answer yes/no, or share your city/state or ZIP instead of {pending.KnownLocation}.");
        }

        if (pending.Mode == PendingLocationMode.AwaitingManualLocation)
        {
            if (IsSkipOrOptOut(lower))
            {
                WritePendingState(PendingLocationState.None);
                return BuildPromptResponse(
                    "No problem. I can still help. If you want nearby results later, share your city or ZIP.");
            }

            if (TryExtractManualLocation(message, out var manualLocation))
            {
                var updated = _saveManualLocation(manualLocation);
                _applySettings(updated);
                WritePendingState(PendingLocationState.None);
                return await ContinuePendingQueryAsync(pending.OriginalQuery, cancellationToken);
            }

            return BuildPromptResponse(
                NearMePrompt);
        }

        return await _inner.ProcessAsync(message, cancellationToken);
    }

    private Task<AgentResponse> ContinuePendingQueryAsync(string originalQuery, CancellationToken cancellationToken)
        => _inner.ProcessAsync(originalQuery, cancellationToken);

    private Task<AgentResponse> ContinuePendingQueryThroughWrapperAsync(string originalQuery, CancellationToken cancellationToken)
        => ProcessAsync(originalQuery, cancellationToken);

    private static bool LooksLikeLocationDependentRequest(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("around me", StringComparison.Ordinal) ||
               lower.Contains("close to me", StringComparison.Ordinal) ||
               lower.Contains("closest ", StringComparison.Ordinal) ||
               (lower.Contains("restaurant", StringComparison.Ordinal) &&
                lower.Contains("near", StringComparison.Ordinal));
    }

    private static bool ShouldRunFirstTurnVetting(string lower)
    {
        if (LooksLikeLocationDependentRequest(lower))
            return true;

        var normalized = NormalizeLooseText(lower);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        if (normalized.StartsWith("hello ", StringComparison.Ordinal) ||
            normalized.StartsWith("hi ", StringComparison.Ordinal) ||
            normalized.StartsWith("hey ", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized is "hi" or "hello" or "hey" or "yo" or
            "hello there" or "hey there" or
            "good morning" or "good afternoon" or "good evening" or
            "how are you" or "whats up" or "what is up";
    }

    private bool IsLocationStale(LocationSettings location)
    {
        var updatedAt = location.GetResolvedUpdatedAt();
        if (string.IsNullOrWhiteSpace(updatedAt))
            return true;

        if (!DateTimeOffset.TryParse(updatedAt, out var parsed))
            return true;

        var age = _timeProvider.GetUtcNow() - parsed;
        return age > StaleLocationWindow;
    }

    private static bool IsAffirmative(string lower)
    {
        var normalized = NormalizeLooseText(lower);
        return normalized == "yes" ||
               normalized == "y" ||
               normalized == "yeah" ||
               normalized == "yep" ||
               normalized == "correct" ||
               normalized == "still there" ||
               normalized == "still here" ||
               normalized == "i am" ||
               normalized == "im";
    }

    private static bool IsNegative(string lower)
    {
        var normalized = NormalizeLooseText(lower);
        return normalized == "no" ||
               normalized == "n" ||
               normalized == "nope" ||
               normalized == "nah" ||
               normalized == "not anymore" ||
               normalized == "moved";
    }

    private static bool IsSkipOrOptOut(string lower)
    {
        var normalized = NormalizeLooseText(lower);
        return normalized == "skip" ||
               normalized == "pass" ||
               normalized == "not now" ||
               normalized == "prefer not" ||
               normalized == "no thanks" ||
               normalized == "dont want to share" ||
               normalized == "do not want to share";
    }

    private static bool TryExtractManualLocation(string input, out string location)
    {
        location = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim().Trim('?', '.', '!', ',', ';', ':');
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var lower = trimmed.ToLowerInvariant();
        if (IsAffirmative(lower) || IsNegative(lower))
            return false;

        trimmed = Regex.Replace(
            trimmed,
            @"^\s*(i\s*(am|'m)\s*(in|at)|im\s*(in|at)|in|at)\s+",
            "",
            RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        if (ZipRegex.IsMatch(trimmed))
        {
            location = trimmed;
            return true;
        }

        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            location = trimmed;
            return true;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 1 && tokens[0].Length >= 3 && tokens[0].All(char.IsLetter))
        {
            location = trimmed;
            return true;
        }

        if (tokens.Length >= 2)
        {
            var last = tokens[^1];
            if (last.Length == 2 && last.All(char.IsLetter))
            {
                location = trimmed;
                return true;
            }

            if (tokens.Any(t => t.Any(char.IsDigit)))
            {
                location = trimmed;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeLooseText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c) || c == ' ')
            .ToArray();

        return string.Join(
            " ",
            new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static AgentResponse BuildPromptResponse(string text)
    {
        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = [],
            LlmRoundTrips = 0
        };
    }

    private PendingLocationState ReadPendingState()
    {
        lock (_gate)
            return _pending;
    }

    private void WritePendingState(PendingLocationState state)
    {
        lock (_gate)
            _pending = state;
    }

    private bool HasAskedVettingForProfile(string profileKey)
    {
        lock (_gate)
            return _vettingAskedProfileKeys.Contains(profileKey);
    }

    private void MarkVettingAskedForProfile(string profileKey)
    {
        lock (_gate)
            _vettingAskedProfileKeys.Add(profileKey);
    }

    private enum PendingLocationMode
    {
        None,
        AwaitingVettingLocation,
        AwaitingManualLocation,
        AwaitingStaleConfirmation
    }

    private sealed record PendingLocationState(
        PendingLocationMode Mode,
        string OriginalQuery,
        string KnownLocation,
        string ProfileKey)
    {
        public static PendingLocationState None { get; } = new(
            PendingLocationMode.None,
            "",
            "",
            "");
    }
}
