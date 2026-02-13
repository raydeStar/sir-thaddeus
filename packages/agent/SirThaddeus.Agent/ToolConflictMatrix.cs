using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public enum ToolConflictReason
{
    ExplicitUserRequest,
    LowerRisk,
    DeterministicPriority,
    PolicyForbid
}

public sealed record ToolConflictSkip(
    ToolCallRequest ToolCall,
    string? WinnerTool,
    ToolConflictReason Reason,
    string Detail);

public sealed record ToolConflictResolution(
    IReadOnlyList<ToolCallRequest> Winners,
    IReadOnlyList<ToolConflictSkip> Skipped);

/// <summary>
/// Resolves same-turn tool conflicts before MCP execution.
/// </summary>
public static class ToolConflictMatrix
{
    private static readonly HashSet<(ToolCapability Left, ToolCapability Right)> CapabilityConflicts =
    [
        Pair(ToolCapability.WebSearch, ToolCapability.SystemExecute),
        Pair(ToolCapability.BrowserNavigate, ToolCapability.SystemExecute),
        Pair(ToolCapability.ScreenCapture, ToolCapability.SystemExecute),
        Pair(ToolCapability.MemoryWrite, ToolCapability.SystemExecute),
        Pair(ToolCapability.MemoryWrite, ToolCapability.WebSearch)
    ];

    // Tool-level exceptions are intentionally sparse and only used when
    // capability-level conflicts are not expressive enough.
    private static readonly Dictionary<(string Left, string Right), (string Winner, ToolConflictReason Reason)> ToolSpecificRules =
        new(new PairKeyComparer())
        {
            [PairKey("screen_capture", "get_active_window")] = ("get_active_window", ToolConflictReason.DeterministicPriority)
        };

    private static readonly IReadOnlyDictionary<ToolCapability, int> CapabilityRiskPriority =
        new Dictionary<ToolCapability, int>
        {
            [ToolCapability.DeterministicUtility] = 0,
            [ToolCapability.Meta] = 0,
            [ToolCapability.MemoryRead] = 1,
            [ToolCapability.FileRead] = 2,
            [ToolCapability.TimeRead] = 2,
            [ToolCapability.WebSearch] = 3,
            [ToolCapability.BrowserNavigate] = 3,
            [ToolCapability.ScreenCapture] = 4,
            [ToolCapability.MemoryWrite] = 5,
            [ToolCapability.FileWrite] = 5,
            [ToolCapability.SystemExecute] = 6
        };

    public static ToolConflictResolution ResolveTurn(
        IReadOnlyList<ToolCallRequest> requestedCalls,
        IReadOnlySet<string> allowedToolNames)
    {
        if (requestedCalls.Count == 0)
            return new ToolConflictResolution([], []);

        var winners = new List<ToolCallRequest>();
        var skipped = new List<ToolConflictSkip>();

        foreach (var toolCall in requestedCalls)
        {
            var toolName = toolCall.Function.Name;
            if (!allowedToolNames.Contains(toolName))
            {
                skipped.Add(new ToolConflictSkip(
                    toolCall,
                    WinnerTool: null,
                    Reason: ToolConflictReason.PolicyForbid,
                    Detail: "policy_forbid"));
                continue;
            }

            var candidate = toolCall;
            var candidateSkipped = false;

            for (var i = 0; i < winners.Count;)
            {
                var existing = winners[i];
                var decision = ResolvePair(candidate, existing);
                if (decision is null)
                {
                    i++;
                    continue;
                }

                var winnerTool = decision.Value.Winner;
                var reason = decision.Value.Reason;
                var detail = decision.Value.Detail;

                if (winnerTool.Equals(candidate.Function.Name, StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(new ToolConflictSkip(
                        existing,
                        WinnerTool: candidate.Function.Name,
                        Reason: reason,
                        Detail: detail));
                    winners.RemoveAt(i);
                    continue;
                }

                skipped.Add(new ToolConflictSkip(
                    candidate,
                    WinnerTool: existing.Function.Name,
                    Reason: reason,
                    Detail: detail));
                candidateSkipped = true;
                break;
            }

            if (!candidateSkipped)
                winners.Add(candidate);
        }

        return new ToolConflictResolution(winners, skipped);
    }

    public static string ToReasonCode(ToolConflictReason reason) =>
        reason switch
        {
            ToolConflictReason.ExplicitUserRequest => "explicit_user_request",
            ToolConflictReason.LowerRisk => "lower_risk",
            ToolConflictReason.DeterministicPriority => "deterministic_priority",
            ToolConflictReason.PolicyForbid => "policy_forbid",
            _ => "deterministic_priority"
        };

    private static (string Winner, ToolConflictReason Reason, string Detail)? ResolvePair(
        ToolCallRequest left,
        ToolCallRequest right)
    {
        var leftName = left.Function.Name;
        var rightName = right.Function.Name;

        var pairKey = PairKey(leftName, rightName);
        if (ToolSpecificRules.TryGetValue(pairKey, out var specific))
        {
            return (
                Winner: specific.Winner,
                Reason: specific.Reason,
                Detail: $"tool_rule:{pairKey.Left}|{pairKey.Right}");
        }

        var leftCapability = ToolCapabilityRegistry.ResolveCapability(leftName);
        var rightCapability = ToolCapabilityRegistry.ResolveCapability(rightName);
        if (leftCapability is null || rightCapability is null)
            return null;

        var capabilityPair = Pair(leftCapability.Value, rightCapability.Value);
        if (!CapabilityConflicts.Contains(capabilityPair))
            return null;

        var leftPriority = CapabilityRiskPriority.TryGetValue(leftCapability.Value, out var lp) ? lp : int.MaxValue;
        var rightPriority = CapabilityRiskPriority.TryGetValue(rightCapability.Value, out var rp) ? rp : int.MaxValue;

        if (leftPriority < rightPriority)
        {
            return (
                Winner: leftName,
                Reason: ToolConflictReason.LowerRisk,
                Detail: $"capability_rule:{leftCapability}|{rightCapability}");
        }

        if (rightPriority < leftPriority)
        {
            return (
                Winner: rightName,
                Reason: ToolConflictReason.LowerRisk,
                Detail: $"capability_rule:{leftCapability}|{rightCapability}");
        }

        var lexicalWinner = string.Compare(leftName, rightName, StringComparison.OrdinalIgnoreCase) <= 0
            ? leftName
            : rightName;
        return (
            Winner: lexicalWinner,
            Reason: ToolConflictReason.DeterministicPriority,
            Detail: $"capability_tie:{leftCapability}|{rightCapability}");
    }

    private static (ToolCapability Left, ToolCapability Right) Pair(ToolCapability a, ToolCapability b)
        => a <= b ? (a, b) : (b, a);

    private static (string Left, string Right) PairKey(string a, string b)
        => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? (a, b)
            : (b, a);

    private sealed class PairKeyComparer : IEqualityComparer<(string Left, string Right)>
    {
        public bool Equals((string Left, string Right) x, (string Left, string Right) y)
            => x.Left.Equals(y.Left, StringComparison.OrdinalIgnoreCase)
               && x.Right.Equals(y.Right, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Left, string Right) obj)
            => StringComparer.OrdinalIgnoreCase.GetHashCode($"{obj.Left}|{obj.Right}");
    }
}

