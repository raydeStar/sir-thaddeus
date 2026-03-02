using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Validation.Completion;

/// <summary>
/// Evaluates tool execution results against a <see cref="CompletionContract"/>
/// and produces a <see cref="CompletionReport"/>. Pure logic — no LLM, no I/O.
///
/// The checker inspects <see cref="ToolCallRecord.Result"/> strings for:
///   • JSON properties matching required/optional field names (+ aliases)
///   • URLs matching evidence requirements
///   • Item counts for list-type contracts
///
/// Design rules:
///   • Conservative: ambiguous results lean toward "complete" to avoid
///     false repair loops on otherwise good responses.
///   • Never fabricates missing data — only reports what's absent.
///   • Error-only tool results are treated as non-evidence.
/// </summary>
public sealed class CompletionChecker
{
    /// <summary>
    /// Checks tool results against a completion contract.
    /// </summary>
    /// <param name="contract">The contract defining "done" for this intent.</param>
    /// <param name="toolResults">Tool call records from execution.</param>
    /// <param name="assistantText">The LLM's final text response (optional — checked for evidence).</param>
    /// <returns>A report describing completeness and any gaps.</returns>
    public CompletionReport Check(
        CompletionContract contract,
        IReadOnlyList<ToolCallRecord> toolResults,
        string? assistantText = null)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(toolResults);

        // Null-object contract is always satisfied
        if (ReferenceEquals(contract, CompletionContract.AlwaysSatisfied))
            return CompletionReport.AlwaysSatisfied;

        var foundFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var urlCount = 0;
        var hasNamedSource = false;
        var itemCount = 0;
        var hasAnySuccessfulResult = false;

        // Scan all successful tool results
        foreach (var result in toolResults)
        {
            if (!result.Success)
                continue;

            var resultText = result.Result ?? "";
            if (string.IsNullOrWhiteSpace(resultText))
                continue;

            if (LooksLikeStructuredError(resultText))
                continue;

            hasAnySuccessfulResult = true;

            // Try parsing as JSON and extracting fields
            var jsonItemCount = ScanJsonResult(resultText, contract.Fields, foundFields, ref urlCount, ref hasNamedSource);
            itemCount += jsonItemCount;

            // Fallback: scan raw text for URLs
            if (urlCount == 0)
                urlCount += CountUrls(resultText);
        }

        // Also scan assistant text for evidence (URLs, named sources)
        if (!string.IsNullOrWhiteSpace(assistantText))
        {
            urlCount += CountUrls(assistantText);
            if (!hasNamedSource)
                hasNamedSource = HasNamedSourceReference(assistantText);

            // If the assistant produced a substantive answer, count "answer" as found
            if (assistantText.Length > 20)
                foundFields.Add("answer");
        }

        // Evaluate field requirements
        var missingRequired = new List<string>();
        var missingOptional = new List<string>();
        var requiredCount = 0;
        var requiredFoundCount = 0;
        var optionalCount = 0;
        var optionalFoundCount = 0;

        foreach (var field in contract.Fields)
        {
            var satisfied = foundFields.Contains(field.FieldName) ||
                            field.Aliases.Any(a => foundFields.Contains(a));

            if (field.Necessity == FieldNecessity.Required)
                requiredCount++;
            else
                optionalCount++;

            if (!satisfied)
            {
                if (field.Necessity == FieldNecessity.Required)
                    missingRequired.Add(field.FieldName);
                else
                    missingOptional.Add(field.FieldName);
            }
            else
            {
                if (field.Necessity == FieldNecessity.Required)
                    requiredFoundCount++;
                else
                    optionalFoundCount++;
            }
        }

        // Evaluate evidence requirements
        var issues = new List<string>();

        if (contract.Evidence.MinSourceUrls > 0 && urlCount < contract.Evidence.MinSourceUrls)
            issues.Add($"Expected at least {contract.Evidence.MinSourceUrls} source URL(s), found {urlCount}");

        if (contract.Evidence.RequiresNamedSource && !hasNamedSource)
            issues.Add("No named source citation found");

        if (contract.Evidence.RejectErrorOnlyResults && !hasAnySuccessfulResult && toolResults.Count > 0)
            issues.Add("All tool results were errors");

        // Evaluate min items
        if (contract.MinItems > 0 && itemCount < contract.MinItems)
            issues.Add($"Expected at least {contract.MinItems} item(s), found {itemCount}");

        var isComplete = missingRequired.Count == 0 && issues.Count == 0;
        var confidence = ComputeConfidence(
            requiredCount,
            requiredFoundCount,
            optionalCount,
            optionalFoundCount,
            contract,
            urlCount,
            hasNamedSource,
            itemCount,
            hasAnySuccessfulResult,
            isComplete);
        var stopReason = isComplete
            ? "complete"
            : missingRequired.Count > 0
                ? "missing_required_fields"
                : issues.Count > 0
                    ? "evidence_or_count_requirements_unmet"
                    : "incomplete";

        return new CompletionReport
        {
            IsComplete = isComplete,
            MissingFields = missingRequired,
            MissingOptionalFields = missingOptional,
            Issues = issues,
            ItemCount = itemCount,
            Confidence = confidence,
            StopReason = stopReason,
            Contract = contract
        };
    }

    // ── JSON Scanning ────────────────────────────────────────────────

    /// <summary>
    /// Parses a JSON result and looks for field names in the contract.
    /// Returns the number of items found (for array results).
    /// </summary>
    private static int ScanJsonResult(
        string json,
        IReadOnlyList<FieldRequirement> fields,
        HashSet<string> foundFields,
        ref int urlCount,
        ref bool hasNamedSource)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                var count = 0;
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        ScanJsonObject(item, fields, foundFields, ref urlCount, ref hasNamedSource);
                        count++;
                    }
                }
                return Math.Max(count, 1);
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                ScanJsonObject(root, fields, foundFields, ref urlCount, ref hasNamedSource);

                // Check for nested arrays (e.g. { "results": [...] })
                if (root.TryGetProperty("results", out var resultsArray) &&
                    resultsArray.ValueKind == JsonValueKind.Array)
                {
                    var count = 0;
                    foreach (var item in resultsArray.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.Object)
                        {
                            ScanJsonObject(item, fields, foundFields, ref urlCount, ref hasNamedSource);
                            count++;
                        }
                    }
                    return count;
                }

                return 1;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON — fall through to raw text scanning
        }

        return 0;
    }

    private static void ScanJsonObject(
        JsonElement obj,
        IReadOnlyList<FieldRequirement> fields,
        HashSet<string> foundFields,
        ref int urlCount,
        ref bool hasNamedSource)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            var name = prop.Name;
            var value = prop.Value;

            // Check if this property matches any field requirement
            foreach (var field in fields)
            {
                if (name.Equals(field.FieldName, StringComparison.OrdinalIgnoreCase) ||
                    field.Aliases.Any(a => name.Equals(a, StringComparison.OrdinalIgnoreCase)))
                {
                    if (HasNonEmptyValue(value))
                        foundFields.Add(field.FieldName);
                }
            }

            // Track "answer" field explicitly
            if (name.Equals("answer", StringComparison.OrdinalIgnoreCase) && HasNonEmptyValue(value))
                foundFields.Add("answer");

            var hasKnownUrlFieldName = IsKnownUrlPropertyName(name);

            // Check for URLs in string values
            if (value.ValueKind == JsonValueKind.String)
            {
                var str = value.GetString() ?? "";

                // Avoid double-counting URL values on known URL properties:
                // they are counted in the dedicated URL-field block below.
                if (LooksLikeUrl(str) && !hasKnownUrlFieldName)
                    urlCount++;

                if (name.Equals("source", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("source_name", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("publisher", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(str))
                        hasNamedSource = true;
                }
            }

            // Track URL-like property names
            if (hasKnownUrlFieldName &&
                value.ValueKind == JsonValueKind.String &&
                LooksLikeUrl(value.GetString() ?? ""))
            {
                urlCount++;
                foundFields.Add(name);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static bool HasNonEmptyValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => !string.IsNullOrWhiteSpace(element.GetString()),
            JsonValueKind.Number => true,
            JsonValueKind.True => true,
            JsonValueKind.False => true,
            JsonValueKind.Array => element.GetArrayLength() > 0,
            JsonValueKind.Object => true,
            _ => false
        };

    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksLikeUrl(string text) =>
        text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static int CountUrls(string text) =>
        UrlPattern.Matches(text).Count;

    private static bool HasNamedSourceReference(string text) =>
        text.Contains("according to", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("source:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("reported by", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("published by", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownUrlPropertyName(string name) =>
        name.Equals("url", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("source_url", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("link", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("homepage", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("website", StringComparison.OrdinalIgnoreCase);

    private static double ComputeConfidence(
        int requiredCount,
        int requiredFoundCount,
        int optionalCount,
        int optionalFoundCount,
        CompletionContract contract,
        int urlCount,
        bool hasNamedSource,
        int itemCount,
        bool hasAnySuccessfulResult,
        bool isComplete)
    {
        if (isComplete)
            return 1.0;

        // Coverage model:
        // - required coverage dominates
        // - optional coverage contributes lightly
        // - evidence + min-items reduce confidence when unmet
        var requiredCoverage = requiredCount == 0
            ? 1.0
            : (double)requiredFoundCount / requiredCount;
        var optionalCoverage = optionalCount == 0
            ? 1.0
            : (double)optionalFoundCount / optionalCount;

        var evidenceScore = 1.0;
        if (contract.Evidence.MinSourceUrls > 0)
        {
            evidenceScore = Math.Min(1.0, (double)urlCount / contract.Evidence.MinSourceUrls);
        }
        if (contract.Evidence.RequiresNamedSource && !hasNamedSource)
        {
            evidenceScore *= 0.5;
        }

        var itemScore = contract.MinItems <= 0
            ? 1.0
            : Math.Min(1.0, (double)itemCount / contract.MinItems);

        var successScore = hasAnySuccessfulResult ? 1.0 : 0.4;

        var weighted =
            (requiredCoverage * 0.55) +
            (optionalCoverage * 0.10) +
            (evidenceScore * 0.20) +
            (itemScore * 0.10) +
            (successScore * 0.05);

        return Math.Clamp(weighted, 0.0, 0.99);
    }

    /// <summary>
    /// Detects structured error payloads from tool results.
    /// </summary>
    private static bool LooksLikeStructuredError(string result)
    {
        if (result.StartsWith("Tool error:", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("error", out _))
                return true;
        }
        catch (JsonException)
        {
            // Not JSON — not a structured error
        }

        return false;
    }
}
