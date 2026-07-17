using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Projects one verbatim answer span only when the user explicitly requested
/// an answer-only response and the same span already exists in both the model
/// draft and a successful tool result. It never generates or infers an answer.
/// </summary>
internal static partial class EvidenceBackedAnswerOnlyProjection
{
    private const int MaxCandidateCharacters = 120;

    private static readonly HashSet<string> ContentKeys =
    [
        "content",
        "excerpt",
        "markdown",
        "result",
        "text",
        "textcontent",
        "value"
    ];

    public static EvidenceProjectionResult Project(
        string? userRequest,
        string? assistantDraft,
        IReadOnlyList<ToolCallRecord> toolCalls)
    {
        var original = assistantDraft ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            return Inactive(original, "empty_draft");

        var requestLead = RequestLead(userRequest);
        if (!AnswerOnlyPattern().IsMatch(requestLead))
            return Inactive(original, "not_answer_only");
        if (ExplanationPattern().IsMatch(requestLead))
            return Inactive(original, "explanation_requested");
        if (PluralContractPattern().IsMatch(requestLead))
            return Inactive(original, "plural_contract");

        var successfulResults = toolCalls
            .Where(call => call.Success && !string.IsNullOrWhiteSpace(call.Result))
            .SelectMany(call => ExtractEvidenceText(call.Result))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        if (successfulResults.Length == 0)
            return Inactive(original, "no_successful_tool_evidence");

        var matches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in DraftCandidates(original))
        {
            var normalized = Normalize(candidate);
            if (normalized.Length == 0 || matches.ContainsKey(normalized))
                continue;
            if (!successfulResults.Any(result => Normalize(result).Contains(normalized, StringComparison.Ordinal)))
                continue;

            var start = original.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                matches[normalized] = original.Substring(start, candidate.Length);
        }

        if (matches.Count == 0)
            return Inactive(original, "no_shared_span");

        var nestedAnswers = matches
            .Where(entry => !matches.Keys.Any(other =>
                !string.Equals(other, entry.Key, StringComparison.Ordinal) &&
                entry.Key.Contains(other, StringComparison.Ordinal)))
            .ToArray();
        if (nestedAnswers.Length != 1)
            return Inactive(original, "ambiguous_shared_spans");

        var projected = nestedAnswers[0].Value.Trim();
        if (string.Equals(Normalize(original), Normalize(projected), StringComparison.Ordinal))
            return Inactive(original, "already_exact");

        return new EvidenceProjectionResult(projected, true, "projected");
    }

    private static IEnumerable<string> ExtractEvidenceText(string result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            return ExtractEvidenceText(document.RootElement, null).ToArray();
        }
        catch (JsonException)
        {
            return [result];
        }
    }

    private static IEnumerable<string> ExtractEvidenceText(JsonElement element, string? parentKey)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in ExtractEvidenceText(property.Value, property.Name.ToLowerInvariant()))
                        yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    foreach (var value in ExtractEvidenceText(child, parentKey))
                        yield return value;
                }
                break;
            case JsonValueKind.String when parentKey is not null && ContentKeys.Contains(parentKey):
                if (element.GetString() is { Length: > 0 } text)
                    yield return text;
                break;
            case JsonValueKind.Number when parentKey is not null && ContentKeys.Contains(parentKey):
                yield return element.GetRawText();
                break;
        }
    }

    private static IEnumerable<string> DraftCandidates(string draft)
    {
        var candidates = new List<string>();
        var cleanedDraft = CleanCandidate(draft);
        if (!cleanedDraft.Contains('\n') && cleanedDraft.Length <= MaxCandidateCharacters)
            candidates.Add(cleanedDraft);

        candidates.AddRange(BoldValuePattern().Matches(draft).Select(match => match.Groups["value"].Value));
        candidates.AddRange(QuotedValuePattern().Matches(draft).Select(match => match.Groups["value"].Value));
        candidates.AddRange(LabelValuePattern().Matches(draft).Select(match => match.Groups["value"].Value));
        if (CopulaValuePattern().Match(draft.Trim()) is { Success: true } copula)
            candidates.Add(copula.Groups["value"].Value);

        return candidates
            .Select(CleanCandidate)
            .Where(candidate => candidate.Length is > 0 and <= MaxCandidateCharacters)
            .Where(candidate => candidate.Any(char.IsLetterOrDigit))
            .DistinctBy(Normalize, StringComparer.Ordinal);
    }

    private static string CleanCandidate(string value)
    {
        var cleaned = LeadingMarkdownPattern().Replace(value.Trim(), string.Empty);
        cleaned = cleaned.Trim(' ', '\t', '\r', '\n', '`', '*', '_', '"', '\'');
        cleaned = cleaned.TrimEnd('.', '!', '?', ',', ';', ':');
        return cleaned.Trim(' ', '\t', '\r', '\n', '`', '*', '_', '"', '\'');
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKC))
        {
            if (character is '`' or '*' or '_')
                continue;
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString().Trim().TrimEnd('.', '!', '?', ',', ';', ':');
    }

    private static string RequestLead(string? userRequest)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
            return string.Empty;

        var lead = userRequest[..Math.Min(userRequest.Length, 600)]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var blockEnd = lead.IndexOf("\n\n", StringComparison.Ordinal);
        return blockEnd >= 0 ? lead[..blockEnd] : lead;
    }

    private static EvidenceProjectionResult Inactive(string original, string reason) =>
        new(original, false, reason);

    [GeneratedRegex(
        @"\b(?:reply|return|answer|respond|provide|give)\b[^.!?\n]{0,64}\b(?:only|just|alone)\b|" +
        @"\b(?:only|just)\b[^.!?\n]{0,48}\b(?:value|code|name|location|number|contents?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnswerOnlyPattern();

    [GeneratedRegex(
        @"\b(?:explain|why|reason|rationale|show\s+(?:the\s+)?work|steps?|summar(?:y|ize))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplanationPattern();

    [GeneratedRegex(
        @"\b(?:both|all\s+(?:the\s+)?(?:values|codes|names|locations)|two\s+(?:values|codes|names|locations))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PluralContractPattern();

    [GeneratedRegex(@"\*\*(?<value>[^*\n]{1,120})\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex BoldValuePattern();

    [GeneratedRegex("[\\\"'](?<value>[^\\\"'\\n]{1,120})[\\\"']", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValuePattern();

    [GeneratedRegex(@"^[^:\n]{1,64}:\s*(?<value>[^\n]{1,120})$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LabelValuePattern();

    [GeneratedRegex(
        @"\b(?:is|was|are|equals?|reads?|shows?)\s+(?<value>[^.!?\n]{1,120})[.!?]?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CopulaValuePattern();

    [GeneratedRegex(@"^(?:[-*]\s+|#+\s+)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarkdownPattern();
}

internal readonly record struct EvidenceProjectionResult(string Text, bool Applied, string Reason);
