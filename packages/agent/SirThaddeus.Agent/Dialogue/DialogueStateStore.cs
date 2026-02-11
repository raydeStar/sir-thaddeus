using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Dialogue;

public sealed class DialogueStateStore : IDialogueStateStore
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private DialogueState _state;

    public DialogueStateStore(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = new DialogueState
        {
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
    }

    public event Action<DialogueState>? Changed;

    public DialogueState Get()
    {
        lock (_sync)
            return _state;
    }

    public void Seed(DialogueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        DialogueState normalized;
        lock (_sync)
        {
            normalized = Normalize(state);
            _state = normalized;
        }

        Changed?.Invoke(normalized);
    }

    public void Update(DialogueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        DialogueState normalized;
        lock (_sync)
        {
            normalized = Normalize(state with { UpdatedAtUtc = _timeProvider.GetUtcNow() });
            _state = normalized;
        }

        Changed?.Invoke(normalized);
    }

    public void Reset()
    {
        var reset = new DialogueState
        {
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };

        lock (_sync)
            _state = reset;

        Changed?.Invoke(reset);
    }

    private static DialogueState Normalize(DialogueState state)
    {
        var summary = state.RollingSummary ?? "";
        summary = summary.Trim();
        if (summary.Length > 220)
            summary = summary[..220].TrimEnd() + "...";

        // Keep to at most two lines to maintain compact snapshots.
        var lines = summary
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2);
        summary = string.Join(" ", lines);
        summary = Regex.Replace(summary, @"\s{2,}", " ");

        return state with
        {
            Topic = (state.Topic ?? "").Trim(),
            LocationName = NormalizeOptionalValue(state.LocationName),
            CountryCode = string.IsNullOrWhiteSpace(state.CountryCode) ? null : state.CountryCode.Trim().ToUpperInvariant(),
            RegionCode = string.IsNullOrWhiteSpace(state.RegionCode) ? null : state.RegionCode.Trim().ToUpperInvariant(),
            TimeScope = NormalizeOptionalValue(state.TimeScope),
            RollingSummary = summary
        };
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().TrimEnd('.', ',', '!', '?');
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("na", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("(none)", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim();
    }
}
