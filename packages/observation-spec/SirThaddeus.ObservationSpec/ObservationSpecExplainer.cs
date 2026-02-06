using System.Text;
using System.Text.RegularExpressions;

namespace SirThaddeus.ObservationSpec;

/// <summary>
/// Generates human-readable explanations of Observation Specifications.
/// Helps users understand exactly what an observation will do.
/// </summary>
public sealed partial class ObservationSpecExplainer
{
    /// <summary>
    /// Generates a human-readable explanation of an observation spec.
    /// </summary>
    /// <param name="spec">The spec to explain.</param>
    /// <returns>A multi-line explanation string.</returns>
    public string Explain(ObservationSpecDocument spec)
    {
        var sb = new StringBuilder();

        sb.AppendLine("📋 OBSERVATION SPEC SUMMARY");
        sb.AppendLine(new string('─', 40));
        sb.AppendLine();

        // What is being watched
        sb.AppendLine("🎯 WHAT");
        sb.AppendLine($"   Monitoring: {FormatTargetType(spec.Target.Type)}");
        sb.AppendLine($"   URL: {spec.Target.Url}");
        sb.AppendLine($"   Method: {spec.Target.Method}");
        sb.AppendLine();

        // What we're looking for
        sb.AppendLine("🔍 LOOKING FOR");
        sb.AppendLine($"   Check: {FormatCheckType(spec.Check.Type)}");
        sb.AppendLine($"   Value: \"{spec.Check.Value}\"");
        if (!string.IsNullOrEmpty(spec.Check.Scope))
            sb.AppendLine($"   Scope: {FormatScope(spec.Check.Scope)}");
        if (!string.IsNullOrEmpty(spec.Check.Path))
            sb.AppendLine($"   Path: {spec.Check.Path}");
        sb.AppendLine();

        // How often
        sb.AppendLine("⏱️ SCHEDULE");
        sb.AppendLine($"   Interval: Every {FormatInterval(spec.Schedule.Interval)}");
        if (!string.IsNullOrEmpty(spec.Schedule.Jitter))
            sb.AppendLine($"   Jitter: {spec.Schedule.Jitter}");
        sb.AppendLine();

        // What happens on match
        sb.AppendLine("🔔 NOTIFICATION");
        sb.AppendLine($"   Channels: {string.Join(", ", spec.Notify.OnMatch.Select(FormatNotifyChannel))}");
        sb.AppendLine($"   Frequency: {(spec.Notify.Once ? "Once only" : "Every match")}");
        sb.AppendLine();

        // Safety limits
        sb.AppendLine("🛡️ LIMITS");
        sb.AppendLine($"   Max checks: {spec.Limits.MaxChecks:N0}");
        if (spec.Limits.ExpiresAt.HasValue)
            sb.AppendLine($"   Expires: {spec.Limits.ExpiresAt.Value:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();

        // Summary sentence
        sb.AppendLine(new string('─', 40));
        sb.AppendLine();
        sb.AppendLine("📝 IN PLAIN ENGLISH:");
        sb.AppendLine($"   {GenerateSummary(spec)}");

        return sb.ToString();
    }

    private static string FormatTargetType(TargetType type) => type switch
    {
        TargetType.WebPage => "Web Page (HTML)",
        TargetType.ApiEndpoint => "API Endpoint (JSON)",
        _ => type.ToString()
    };

    private static string FormatCheckType(CheckType type) => type switch
    {
        CheckType.TextContains => "Text contains",
        CheckType.TextNotContains => "Text does NOT contain",
        CheckType.RegexMatch => "Text matches regex",
        CheckType.JsonPathEquals => "JSON value equals",
        CheckType.JsonPathExists => "JSON path exists",
        _ => type.ToString()
    };

    private static string FormatScope(string scope) => scope switch
    {
        "visible_text" => "Visible text only",
        "full_html" => "Full HTML source",
        "json_body" => "JSON response body",
        _ => scope
    };

    [GeneratedRegex(@"^(\d+)(m|h|d)$", RegexOptions.IgnoreCase)]
    private static partial Regex IntervalRegex();

    private static string FormatInterval(string interval)
    {
        var match = IntervalRegex().Match(interval);
        if (!match.Success)
            return interval;

        var value = int.Parse(match.Groups[1].Value);
        var unit = match.Groups[2].Value.ToLowerInvariant();

        return unit switch
        {
            "m" => value == 1 ? "1 minute" : $"{value} minutes",
            "h" => value == 1 ? "1 hour" : $"{value} hours",
            "d" => value == 1 ? "1 day" : $"{value} days",
            _ => interval
        };
    }

    private static string FormatNotifyChannel(string channel) => channel switch
    {
        "local_notification" => "Desktop notification",
        "push" => "Push notification",
        "email" => "Email",
        _ => channel
    };

    private static string GenerateSummary(ObservationSpecDocument spec)
    {
        var checkDesc = spec.Check.Type switch
        {
            CheckType.TextContains => $"the text \"{spec.Check.Value}\" appears",
            CheckType.TextNotContains => $"the text \"{spec.Check.Value}\" disappears",
            CheckType.RegexMatch => $"the pattern \"{spec.Check.Value}\" is found",
            CheckType.JsonPathEquals => $"the value at {spec.Check.Path} equals \"{spec.Check.Value}\"",
            CheckType.JsonPathExists => $"the path {spec.Check.Path} exists",
            _ => "the condition is met"
        };

        var notifyDesc = spec.Notify.Once
            ? "notify me once"
            : "notify me each time";

        var channelDesc = spec.Notify.OnMatch.Count == 1
            ? $"via {FormatNotifyChannel(spec.Notify.OnMatch[0]).ToLowerInvariant()}"
            : $"via {string.Join(" and ", spec.Notify.OnMatch.Select(c => FormatNotifyChannel(c).ToLowerInvariant()))}";

        return $"Check {spec.Target.Url} every {FormatInterval(spec.Schedule.Interval)}. When {checkDesc}, {notifyDesc} {channelDesc}. Stop after {spec.Limits.MaxChecks:N0} checks.";
    }
}
