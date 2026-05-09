using System.Text.RegularExpressions;
using SirThaddeus.AuditLog;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Search;

public sealed partial class SearchOrchestrator
{
    private async Task<AgentResponse?> TryBuildReleasedProductExistenceResponseAsync(
        string userMessage,
        IReadOnlyList<SourceItem> sources,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var text = BuildReleasedProductExistenceAnswer(userMessage, sources);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (text.Contains("I could not confirm from the returned snippets", StringComparison.OrdinalIgnoreCase))
        {
            var offline = await TryBuildExistenceOfflineReasoningResponseAsync(userMessage, toolCallsMade, ct);
            if (offline is not null)
                return offline;

            // If offline reasoning is unavailable, return null so downstream
            // synthesis can still attempt to answer from retrieved sources.
            return null;
        }

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0
        };
    }

    internal static string? BuildReleasedProductExistenceAnswer(
        string userMessage,
        IReadOnlyList<SourceItem> sources)
    {
        var lower = (userMessage ?? string.Empty).Trim().ToLowerInvariant();
        if (!IntentFeatureExtractor.LooksLikeReleasedProductExistenceLookup(lower))
            return null;

        if (sources.Count == 0)
            return null;

        var subject = ExtractReleasedProductExistenceSubject(userMessage ?? string.Empty);
        var subjectTokens = BuildSubjectTokens(subject);
        var subjectMatches = sources
            .Where(source => SourceMentionsSubject(source, subject, subjectTokens))
            .ToList();

        var positiveEvidence = subjectMatches
            .Where(source => HasReleasedProductPositiveSignal(source))
            .ToList();

        if (positiveEvidence.Count > 0)
        {
            var year = TryExtractReleaseYear(positiveEvidence);
            var yearClause = year is null ? string.Empty : $", introduced in {year}";
            var sourceClause = BuildEvidenceSourceClause(positiveEvidence);

            return $"Yes \u2014 {subject} exists as a released product{yearClause}. {sourceClause}";
        }

        var negativeEvidence = subjectMatches
            .Where(source => HasReleasedProductNegativeSignal(source))
            .ToList();

        if (negativeEvidence.Count > 0 && negativeEvidence.Count == subjectMatches.Count)
        {
            var sourceClause = BuildEvidenceSourceClause(negativeEvidence);
            return
                $"No \u2014 I did not find evidence that {subject} exists as a released product. " +
                $"The returned sources include negative indicators such as unreleased, rumor, or no-such-model language. {sourceClause}";
        }

        if (subjectMatches.Count == 0 &&
            TryBuildAbsentFromReleaseCatalogResponse(subject, subjectTokens, sources) is { Length: > 0 } absentFromCatalog)
        {
            return absentFromCatalog;
        }

        return
            $"I could not confirm from the returned snippets whether {subject} is a released product. " +
            "If you want, I can run a tighter follow-up query focused on official release pages.";
    }

    private async Task<AgentResponse?> TryBuildExistenceOfflineReasoningResponseAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var lower = (userMessage ?? string.Empty).Trim().ToLowerInvariant();
        if (!IntentFeatureExtractor.LooksLikeReleasedProductExistenceLookup(lower))
            return null;

        var subject = ExtractReleasedProductExistenceSubject(userMessage ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are answering a factual existence question about a consumer product. " +
                "Use your training data to determine whether the product has been officially released. " +
                "Start your answer with \"Yes\" or \"No\" followed by an em dash (\u2014) and a brief factual statement. " +
                "Keep the answer to one or two sentences. " +
                "Do not mention web search, tool limitations, or data access. " +
                "Do not fabricate URLs, citations, or links."),
            ChatMessage.User(userMessage ?? string.Empty)
        };

        try
        {
            // Use a fresh timeout rather than the pipeline's token, which
            // may already be cancelled from earlier web-search failures.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _llm.ChatAsync(
                messages,
                tools: null,
                maxTokensOverride: 256,
                cts.Token);

            var answer = (response.Content ?? "").Trim();
            answer = Regex.Replace(answer, @"https?://\S+", "").Trim();
            answer = Regex.Replace(answer, @"www\.\S+", "").Trim();

            if (string.IsNullOrWhiteSpace(answer))
                return null;

            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "EXISTENCE_OFFLINE_REASONING",
                Result = "llm_answered",
                Details = new Dictionary<string, object>
                {
                    ["subject"] = subject
                }
            });

            return new AgentResponse
            {
                Text = answer,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 1
            };
        }
        catch (Exception ex)
        {
            _audit.Append(new AuditEvent
            {
                Actor = "search",
                Action = "EXISTENCE_OFFLINE_REASONING",
                Result = "llm_call_failed",
                Details = new Dictionary<string, object>
                {
                    ["subject"] = subject,
                    ["error"] = ex.Message
                }
            });
            return null;
        }
    }

    private async Task<AgentResponse?> TryBuildMediaInstallmentOfflineReasoningResponseAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var parsed = TryParseSeasonEpisode(userMessage ?? string.Empty);
        if (parsed is null)
            return null;

        var (entity, season, episode) = parsed.Value;
        if (string.IsNullOrWhiteSpace(entity))
            return null;

        var fallbackText =
            $"I could not verify an official Season {season} Episode {episode} release for {entity} from the current evidence, so I should not invent a plot summary.";

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(
                "You are answering whether a requested TV, streaming, or media installment exists. " +
                "If the requested season or episode does not exist because the series ended, was canceled, or was never made, say that directly. " +
                "Do not invent plots, scenes, or episode summaries. " +
                "Keep the answer to one or two sentences. " +
                "Use plain factual wording and do not mention tool limitations."),
            ChatMessage.User(userMessage ?? string.Empty)
        };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await _llm.ChatAsync(
                messages,
                tools: null,
                maxTokensOverride: 220,
                cts.Token);

            var answer = (response.Content ?? string.Empty).Trim();
            answer = Regex.Replace(answer, @"https?://\S+", string.Empty).Trim();
            answer = Regex.Replace(answer, @"www\.\S+", string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(answer))
                answer = fallbackText;

            if (LooksLikeFabricatedMediaPlot(answer) || !LooksLikeMediaExistenceAnswer(answer))
                answer = fallbackText;

            return new AgentResponse
            {
                Text = answer,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 1
            };
        }
        catch
        {
            return new AgentResponse
            {
                Text = fallbackText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = 0
            };
        }
    }

    private static bool LooksLikeMediaExistenceAnswer(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var lower = answer.ToLowerInvariant();
        return lower.Contains("does not exist", StringComparison.Ordinal) ||
               lower.Contains("doesn't exist", StringComparison.Ordinal) ||
               lower.Contains("was canceled", StringComparison.Ordinal) ||
               lower.Contains("was cancelled", StringComparison.Ordinal) ||
               lower.Contains("never released", StringComparison.Ordinal) ||
               lower.Contains("never made", StringComparison.Ordinal) ||
               lower.Contains("not verify", StringComparison.Ordinal) ||
               lower.Contains("no official", StringComparison.Ordinal) ||
               lower.Contains("ended before", StringComparison.Ordinal);
    }

    private static bool LooksLikeFabricatedMediaPlot(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return false;

        var lower = answer.ToLowerInvariant();
        return answer.Length > 320 ||
               lower.Contains("the plot", StringComparison.Ordinal) ||
               lower.Contains("the episode follows", StringComparison.Ordinal) ||
               lower.Contains("the story follows", StringComparison.Ordinal) ||
               lower.Contains("the season concludes", StringComparison.Ordinal);
    }

    private async Task<AgentResponse?> TryBuildExistenceGuardedResponseAsync(
        string userMessage,
        IReadOnlyList<SourceItem> initialSources,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken ct)
    {
        var queryBundle = BuildExistenceQueryBundle(userMessage);
        if (queryBundle.Count <= 1)
            return null;

        var evidence = initialSources
            .Where(s => !string.IsNullOrWhiteSpace(s.Url))
            .ToList();
        var addedFollowupEvidence = false;
        var nonexistenceScore = 0;

        var isLikelyNonexistent = evidence.Count > 0 &&
            IsLikelyNonexistent(userMessage, evidence, out nonexistenceScore);

        if (!isLikelyNonexistent)
        {
            foreach (var followupQuery in queryBundle.Skip(1))
            {
                var followupResult = await CallWebSearchAsync(
                    followupQuery,
                    "any",
                    toolCallsMade,
                    ct,
                    originalUserMessage: userMessage,
                    maxResults: 5);

                var followupSources = ParseSourcesFromToolResult(followupResult);
                if (followupSources.Count == 0)
                    continue;

                addedFollowupEvidence = true;
                foreach (var source in followupSources)
                {
                    if (evidence.Any(existing =>
                        string.Equals(existing.Url, source.Url, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    evidence.Add(source);
                }

                if (IsLikelyNonexistent(userMessage, evidence, out nonexistenceScore))
                {
                    isLikelyNonexistent = true;
                    break;
                }
            }
        }

        if (!isLikelyNonexistent)
            return null;

        var seasonLabel = TryExtractSeasonLabel(userMessage);
        var seasonPhrase = seasonLabel is null ? "the requested installment" : seasonLabel;
        var text = TryBuildMediaInstallmentFallback(userMessage) ??
            $"Based on available sources, {seasonPhrase} does not exist. " +
            "The evidence indicates it was canceled or never released, so there is no official episode plot to summarize.";

        _audit.Append(new AuditEvent
        {
            Actor = "search",
            Action = "EXISTENCE_GUARD_TRIGGERED",
            Result = "does_not_exist",
            Details = new Dictionary<string, object>
            {
                ["query_bundle_count"] = queryBundle.Count,
                ["evidence_count"] = evidence.Count,
                ["nonexistence_score"] = nonexistenceScore,
                ["added_followup_evidence"] = addedFollowupEvidence
            }
        });

        return new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = 0
        };
    }

    internal static IReadOnlyList<string> BuildExistenceQueryBundle(string userQuestion)
    {
        if (string.IsNullOrWhiteSpace(userQuestion))
            return [];

        var normalized = userQuestion.Trim();
        var lower = normalized.ToLowerInvariant();
        var hasSeasonEpisode =
            Regex.IsMatch(lower, @"\bseason\s+\d+\b") &&
            Regex.IsMatch(lower, @"\bepisode\s+\d+\b");
        if (!hasSeasonEpisode)
            return [normalized];

        var parsed = TryParseSeasonEpisode(normalized);
        if (parsed is null)
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        var (entity, season, episode) = parsed.Value;
        if (string.IsNullOrWhiteSpace(entity))
        {
            return
            [
                normalized,
                $"{normalized} cancelled",
                $"{normalized} number of seasons",
                $"{normalized} episode list"
            ];
        }

        return
        [
            $"{entity} season {season} episode {episode} plot",
            $"{entity} season {season} cancelled",
            $"{entity} number of seasons",
            $"{entity} season {season} episode list"
        ];
    }

    internal static bool IsLikelyNonexistent(
        string question,
        IReadOnlyList<SourceItem> evidence,
        out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(question) || evidence.Count == 0)
            return false;

        foreach (var source in evidence)
        {
            var text = $"{source.Title} {source.Snippet}".ToLowerInvariant();

            if (text.Contains("does not exist", StringComparison.Ordinal) ||
                text.Contains("doesn't exist", StringComparison.Ordinal) ||
                text.Contains("never renewed", StringComparison.Ordinal) ||
                text.Contains("never released", StringComparison.Ordinal) ||
                text.Contains("no season", StringComparison.Ordinal) ||
                text.Contains("no episode", StringComparison.Ordinal) ||
                text.Contains("canceled", StringComparison.Ordinal) ||
                text.Contains("cancelled", StringComparison.Ordinal) ||
                text.Contains("ended after season", StringComparison.Ordinal))
            {
                score += 6;
            }

            if (text.Contains("episode list", StringComparison.Ordinal) ||
                text.Contains("air date", StringComparison.Ordinal) ||
                text.Contains("released", StringComparison.Ordinal) ||
                text.Contains("available now", StringComparison.Ordinal))
            {
                score -= 3;
            }
        }

        var seasonLabel = TryExtractSeasonLabel(question);
        if (!string.IsNullOrWhiteSpace(seasonLabel))
        {
            var seasonNumberMatch = Regex.Match(seasonLabel, @"\d+");
            if (seasonNumberMatch.Success &&
                int.TryParse(seasonNumberMatch.Value, out var requestedSeason) &&
                requestedSeason > 1)
            {
                var priorSeasonLabel = $"season {requestedSeason - 1}";
                var hasPriorSeason = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains(priorSeasonLabel, StringComparison.OrdinalIgnoreCase));
                var hasCancelSignal = evidence.Any(s =>
                    ($"{s.Title} {s.Snippet}")
                    .Contains("cancel", StringComparison.OrdinalIgnoreCase));

                if (hasPriorSeason && hasCancelSignal)
                    score += 10;
            }
        }

        return score >= 12;
    }

    private static string? TryExtractSeasonLabel(string userMessage)
    {
        var match = Regex.Match(userMessage ?? "", @"\bseason\s+\d+\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static (string Entity, int Season, int Episode)? TryParseSeasonEpisode(string question)
    {
        var lower = question.ToLowerInvariant();
        var seasonMatch = Regex.Match(lower, @"\bseason\s+(\d+)\b");
        var episodeMatch = Regex.Match(lower, @"\bepisode\s+(\d+)\b");
        if (!seasonMatch.Success || !episodeMatch.Success)
            return null;

        if (!int.TryParse(seasonMatch.Groups[1].Value, out var season) ||
            !int.TryParse(episodeMatch.Groups[1].Value, out var episode))
        {
            return null;
        }

        foreach (var pattern in new[]
        {
            @"\bepisode\s+\d+\s+of\s+season\s+\d+\s+of\s+(?<entity>.+?)(?:\s+about)?[?.!]*$",
            @"\bseason\s+\d+\s+episode\s+\d+\s+of\s+(?<entity>.+?)(?:\s+about)?[?.!]*$",
            @"\bseason\s+\d+\s+of\s+(?<entity>.+?)(?:\s+about)?[?.!]*$"
        })
        {
            var entityMatch = Regex.Match(question, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!entityMatch.Success)
                continue;

            var parsedEntity = entityMatch.Groups["entity"].Value.Trim(' ', '?', '.', '"', '\'');
            if (!string.IsNullOrWhiteSpace(parsedEntity))
                return (parsedEntity, season, episode);
        }

        var marker = lower.IndexOf(" of ", StringComparison.Ordinal);
        if (marker < 0)
            marker = lower.IndexOf(" for ", StringComparison.Ordinal);

        var entity = marker >= 0
            ? question[(marker + 4)..].Trim(' ', '?', '.', '"', '\'')
            : question[..Math.Min(seasonMatch.Index, question.Length)].Trim(' ', '?', '.', '"', '\'');

        return (entity, season, episode);
    }

    private static string ExtractReleasedProductExistenceSubject(string userMessage)
    {
        var normalized = (userMessage ?? string.Empty).Trim().TrimEnd('?', '.', '!');
        var match = Regex.Match(
            normalized,
            @"^(?:does|did|is)\s+(.+?)\s+exist(?:\s+as\s+a[n]?\s+.+)?$",
            RegexOptions.IgnoreCase);

        return match.Success
            ? match.Groups[1].Value.Trim()
            : normalized;
    }

    private static bool HasReleasedProductPositiveSignal(SourceItem source)
    {
        var text = $"{source.Title} {source.Snippet}";
        var isTrustedProductDomain =
            source.Domain.Equals("apple.com", StringComparison.OrdinalIgnoreCase) ||
            source.Domain.Equals("www.apple.com", StringComparison.OrdinalIgnoreCase) ||
            source.Domain.Equals("support.apple.com", StringComparison.OrdinalIgnoreCase) ||
            source.Domain.Contains("gsmarena.com", StringComparison.OrdinalIgnoreCase);
         var hasNegativeReleaseCue = text.Contains("not released", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("unreleased", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("rumor", StringComparison.OrdinalIgnoreCase) ||
                         text.Contains("rumour", StringComparison.OrdinalIgnoreCase);

         return text.Contains("year introduced", StringComparison.OrdinalIgnoreCase) ||
             (!hasNegativeReleaseCue && text.Contains("released", StringComparison.OrdinalIgnoreCase)) ||
               text.Contains("available now", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("in production", StringComparison.OrdinalIgnoreCase) ||
               (!hasNegativeReleaseCue && HasTrustedLifecycleSupportSignal(source)) ||
             (isTrustedProductDomain && HasReleasedProductCatalogSignal(source));
    }

    private static bool HasTrustedLifecycleSupportSignal(SourceItem source)
    {
        var text = $"{source.Title} {source.Snippet} {source.Domain}";
        var hasLifecycleSignal = text.Contains("supported by", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("supported from", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("support status", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("version support", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("endoflife", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("end of life", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("eol", StringComparison.OrdinalIgnoreCase);
        if (!hasLifecycleSignal)
            return false;

        return source.Domain.Contains("endoflife", StringComparison.OrdinalIgnoreCase) ||
               source.Domain.Contains("support.", StringComparison.OrdinalIgnoreCase) ||
               source.Domain.StartsWith("support", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReleasedProductNegativeSignal(SourceItem source)
    {
        var text = $"{source.Title} {source.Snippet}";
        return text.Contains("not released", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("unreleased", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("rumor", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("rumour", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("concept", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryBuildAbsentFromReleaseCatalogResponse(
        string subject,
        IReadOnlyList<string> subjectTokens,
        IReadOnlyList<SourceItem> sources)
    {
        if (string.IsNullOrWhiteSpace(subject) || subjectTokens.Count == 0 || sources.Count == 0)
            return null;

        var familyTokens = BuildProductFamilyTokens(subjectTokens);
        if (familyTokens.Count == 0)
            return null;

        var catalogEvidence = sources
            .Where(source => SourceMentionsProductFamily(source, familyTokens) &&
                             HasReleasedProductCatalogSignal(source))
            .ToList();
        if (catalogEvidence.Count < 2)
            return null;

        var sourceClause = BuildEvidenceSourceClause(catalogEvidence);
        return
            $"No \u2014 I did not find {subject} in the returned release/model-list evidence. " +
            $"The strongest sources discuss the same product family but do not identify {subject} as a released model. {sourceClause}";
    }

    private static bool HasReleasedProductCatalogSignal(SourceItem source)
    {
        var text = $"{source.Title} {source.Snippet} {source.Domain}";
        return text.Contains("list of", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("release", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("released", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("model", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("models", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("lineup", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("chronological", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("history", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("introduced", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("compare", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("specs", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("support", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildProductFamilyTokens(IReadOnlyList<string> subjectTokens)
    {
        var generic = new HashSet<string>(StringComparer.Ordinal)
        {
            "product",
            "device",
            "model",
            "phone",
            "version",
            "release",
            "released"
        };

        return subjectTokens
            .Where(token => token.Any(char.IsLetter))
            .Where(token => !generic.Contains(token))
            .ToList();
    }

    private static bool SourceMentionsProductFamily(SourceItem source, IReadOnlyList<string> familyTokens)
    {
        if (familyTokens.Count == 0)
            return false;

        var lower = $"{source.Title} {source.Snippet}".ToLowerInvariant();
        return familyTokens.Any(token => lower.Contains(token, StringComparison.Ordinal));
    }

    private static string BuildEvidenceSourceClause(IReadOnlyList<SourceItem> sources)
    {
        var labels = sources
            .OrderBy(source => IsCommunityDiscussionSource(source) ? 1 : 0)
            .Select(source => string.IsNullOrWhiteSpace(source.Domain) ? source.Title : source.Domain)
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();

        return labels.Count == 0
            ? "I based that on the returned search evidence."
            : $"Evidence checked: {string.Join(", ", labels)}.";
    }

    private static bool IsCommunityDiscussionSource(SourceItem source)
    {
        var text = $"{source.Domain} {source.Title} {source.Url}";
        return text.Contains("discussions.", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("community", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("forum", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> BuildSubjectTokens(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return [];

        var tokens = Regex.Matches(subject.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return tokens;
    }

    private static bool SourceMentionsSubject(SourceItem source, string subject, IReadOnlyList<string> subjectTokens)
    {
        var text = $"{source.Title} {source.Snippet}";
        if (text.Contains(subject, StringComparison.OrdinalIgnoreCase))
            return true;

        if (subjectTokens.Count == 0)
            return false;

        var lower = text.ToLowerInvariant();
        var matchedTokenCount = subjectTokens.Count(token => lower.Contains(token, StringComparison.Ordinal));

        if (subjectTokens.Count <= 2)
            return matchedTokenCount == subjectTokens.Count;

        // For longer subjects, allow one token miss to avoid over-pruning.
        return matchedTokenCount >= subjectTokens.Count - 1;
    }

    private static string? TryExtractReleaseYear(IReadOnlyList<SourceItem> sources)
    {
        foreach (var source in sources)
        {
            var text = $"{source.Title} {source.Snippet}";
            var yearIntroducedMatch = Regex.Match(
                text,
                @"year introduced:\s*(\d{4})",
                RegexOptions.IgnoreCase);
            if (yearIntroducedMatch.Success)
                return yearIntroducedMatch.Groups[1].Value;

            var releasedMatch = Regex.Match(
                text,
                @"released\s+(\d{4})",
                RegexOptions.IgnoreCase);
            if (releasedMatch.Success)
                return releasedMatch.Groups[1].Value;
        }

        return null;
    }
}
