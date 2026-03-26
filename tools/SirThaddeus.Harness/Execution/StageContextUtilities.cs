using System.Text.RegularExpressions;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Execution;

internal static class StageContextUtilities
{
    public static StageExecutionContext FromOptions(HarnessCommandOptions options)
    {
        return new StageExecutionContext
        {
            AssistantContext = options.StageAssistantContext,
            FollowUpAnchor = options.StageFollowUpAnchor,
            UserCity = options.StageUserCity,
            HasRecentFirstPrinciplesRationale = options.StageHasRecentFirstPrinciplesRationale,
            HasRecentSearchResults = options.StageHasRecentSearchResults
        };
    }

    public static QueryBuilderContext BuildQueryBuilderContext(StageExecutionContext context, string defaultUserCity)
    {
        var userCity = !string.IsNullOrWhiteSpace(context.UserCity)
            ? context.UserCity
            : defaultUserCity;

        var recentMessages = new List<(string Role, string Content)>();
        if (!string.IsNullOrWhiteSpace(context.AssistantContext))
            recentMessages.Add(("assistant", context.AssistantContext));

        return new QueryBuilderContext
        {
            UserCity = userCity,
            UserTimezone = TimeZoneInfo.Local.Id,
            FollowUpAnchor = ResolveFollowUpAnchor(context),
            RecentMessages = recentMessages
        };
    }

    public static ClassifierContext BuildClassifierContext(StageExecutionContext context)
    {
        return new ClassifierContext
        {
            HasRecentFirstPrinciplesRationale = context.HasRecentFirstPrinciplesRationale,
            HasRecentSearchResults = context.HasRecentSearchResults
        };
    }

    public static string ResolveFollowUpAnchor(StageExecutionContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.FollowUpAnchor))
            return context.FollowUpAnchor.Trim();

        return TryExtractAssistantTopic(context.AssistantContext);
    }

    internal static string TryExtractAssistantTopic(string assistantContext)
    {
        if (string.IsNullOrWhiteSpace(assistantContext))
            return "";

        var cleaned = assistantContext.Trim();
        foreach (var leadIn in new[] { "bottom line:", "here's what i found:", "here is what i found:", "summary:", "in short:", "tl;dr:" })
        {
            if (cleaned.StartsWith(leadIn, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[leadIn.Length..].TrimStart();
                break;
            }
        }

        foreach (var rawLine in cleaned.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var line = Regex.Replace(rawLine, @"^(?:[-*]\s+|\d+[.)]\s+)", string.Empty);
            var colonIndex = line.IndexOf(':');
            if (colonIndex >= 0 && colonIndex < line.Length - 1)
            {
                var afterColon = TrimAssistantTopicCandidate(line[(colonIndex + 1)..]);
                if (LooksLikeConcreteTopic(afterColon))
                    return Truncate(afterColon, 80);
            }

            var candidate = TrimAssistantTopicCandidate(line);
            if (LooksLikeConcreteTopic(candidate))
                return Truncate(candidate, 80);
        }

        var sentenceEnd = cleaned.IndexOfAny(['.', '!', '?', '\n']);
        if (sentenceEnd > 10)
            cleaned = cleaned[..sentenceEnd].Trim();

        if (cleaned.Length > 80)
            cleaned = cleaned[..80].Trim();

        return cleaned.Length >= 5 ? cleaned : "";
    }

    private static string TrimAssistantTopicCandidate(string text)
    {
        var candidate = text.Trim();
        foreach (var separator in new[] { " at ", " - ", " — ", " (", "," })
        {
            var index = candidate.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                candidate = candidate[..index].Trim();
                break;
            }
        }

        return candidate.Trim(' ', '.', '!', '?', '"');
    }

    private static bool LooksLikeConcreteTopic(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 4)
            return false;

        if (Regex.IsMatch(candidate, @"^(?:here|here's|summary|bottom line|in short)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return false;

        return Regex.IsMatch(
                   candidate,
                   @"^[A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Z][A-Za-z0-9'&.-]+){1,5}$",
                   RegexOptions.CultureInvariant) ||
               Regex.IsMatch(
                   candidate,
                   @"^[A-Z][A-Za-z0-9'&.-]+(?:\s+[A-Za-z0-9'&.-]+){0,4}\s+(?:Bakery|Pastry|Cafe|Restaurant|Deli|Florist|Shop|Store)\b",
                   RegexOptions.CultureInvariant);
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;

        return text[..(max - 3)] + "...";
    }
}