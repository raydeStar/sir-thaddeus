using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Dialogue;

/// <summary>
/// Lightweight pre-planning filter that strips prompt-injection style
/// directives from untrusted content before tool planning.
/// </summary>
public static class PromptInjectionGuard
{
    private static readonly string[] SuspiciousMarkers =
    [
        "ignore previous instructions",
        "ignore all previous",
        "reveal system prompt",
        "print the system prompt",
        "developer message",
        "you are now in developer mode",
        "override safety",
        "bypass permissions",
        "tool call:",
        "call this tool"
    ];

    private static readonly Regex FenceRegex = new(
        "```[\\s\\S]*?```",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static PromptInjectionAssessment Assess(string? rawMessage)
    {
        var message = (rawMessage ?? "").Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return new PromptInjectionAssessment(
                IsUntrusted: false,
                Reason: "",
                FilteredMessage: "");
        }

        var lower = message.ToLowerInvariant();
        var marker = SuspiciousMarkers.FirstOrDefault(m => lower.Contains(m, StringComparison.Ordinal));
        var hasFence = message.Contains("```", StringComparison.Ordinal);

        if (marker is null && !hasFence)
        {
            return new PromptInjectionAssessment(
                IsUntrusted: false,
                Reason: "",
                FilteredMessage: message);
        }

        var filtered = message;
        if (hasFence)
            filtered = FenceRegex.Replace(filtered, " ");

        filtered = string.Join(
            " ",
            filtered.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    var lineLower = line.ToLowerInvariant();
                    return !SuspiciousMarkers.Any(markerText =>
                        lineLower.Contains(markerText, StringComparison.Ordinal));
                }))
            .Trim();

        var reason = marker is null
            ? "code_fence_untrusted_content"
            : $"marker:{marker}";

        return new PromptInjectionAssessment(
            IsUntrusted: true,
            Reason: reason,
            FilteredMessage: filtered);
    }
}

public sealed record PromptInjectionAssessment(
    bool IsUntrusted,
    string Reason,
    string FilteredMessage);
