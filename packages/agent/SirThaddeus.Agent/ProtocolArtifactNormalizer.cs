using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Agent;

public sealed record ProtocolArtifactNormalizationResult(
    string Text,
    bool Applied,
    string Reason);

/// <summary>
/// Recovers user-visible text from malformed provider chat-template artifacts.
/// It never infers an answer. A tool-shaped artifact is replaced only when one
/// typed, successful Wiki page update independently proves the persisted value.
/// </summary>
public static partial class ProtocolArtifactNormalizer
{
    private const string WikiPageUpdateByName = "wiki_page_update_by_name";
    private const string VerifiedUpdateReceipt = "Updated the selected Wiki page.";

    public static ProtocolArtifactNormalizationResult Normalize(
        string? draft,
        WikiMutationTarget? target,
        IReadOnlyList<ToolCallRecord> toolCalls)
    {
        var text = draft ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return Inactive(text, "blank-draft");

        var channelMatch = MalformedChannelPrefixRegex().Match(text);
        if (channelMatch.Success)
        {
            var cleaned = text[channelMatch.Length..].TrimStart();
            if (string.IsNullOrWhiteSpace(cleaned))
                return Inactive(text, "empty-after-channel-markers");

            if (!ProtocolOnlyToolCallsRegex().IsMatch(cleaned))
                return new ProtocolArtifactNormalizationResult(
                    cleaned,
                    Applied: true,
                    Reason: "channel-markers-stripped");

            text = cleaned;
        }

        if (!ProtocolOnlyToolCallsRegex().IsMatch(text))
            return Inactive(draft ?? string.Empty, "no-protocol-artifact");

        if (!TryProveSingleSelectedWikiPageUpdate(target, toolCalls))
            return Inactive(draft ?? string.Empty, "protocol-artifact-without-proof");

        return new ProtocolArtifactNormalizationResult(
            VerifiedUpdateReceipt,
            Applied: true,
            Reason: "verified-wiki-page-update-receipt");
    }

    private static bool TryProveSingleSelectedWikiPageUpdate(
        WikiMutationTarget? target,
        IReadOnlyList<ToolCallRecord> toolCalls)
    {
        if (target is not
            {
                Kind: WikiMutationTargetKind.Page,
                Operation: WikiMutationOperation.PageUpdate,
            })
        {
            return false;
        }

        var relevant = toolCalls
            .Where(call => string.Equals(
                call.ToolName,
                WikiPageUpdateByName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (relevant.Length != 1 || !relevant[0].Success)
            return false;

        var call = relevant[0];
        var targetDecision = WikiMutationTargetGuard.Evaluate(target, call.ToolName, call.Arguments);
        if (!targetDecision.Active || !targetDecision.Allowed)
            return false;

        if (!TryParseObject(call.Arguments, out var arguments) ||
            !TryParseObject(call.Result, out var result) ||
            !TryReadTrue(result, "ok") ||
            !TryReadObject(result, "document", out var document) ||
            !TryReadObject(document, "page", out var page) ||
            !TryReadString(page, "title", out var persistedTitle) ||
            !TryReadPositiveInteger(page, "version") ||
            !TryReadString(document, "markdown", out var persistedMarkdown) ||
            !TryReadString(arguments, "pageTitle", out var requestedTitle) ||
            !TryReadString(arguments, "markdown", out var requestedMarkdown))
        {
            return false;
        }

        return string.Equals(persistedTitle, requestedTitle, StringComparison.Ordinal) &&
               string.Equals(persistedTitle, target.PageTitle, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(persistedMarkdown, requestedMarkdown, StringComparison.Ordinal);
    }

    private static bool TryParseObject(string value, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryReadObject(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        return root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static bool TryReadPositiveInteger(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var number) &&
        number > 0;

    private static ProtocolArtifactNormalizationResult Inactive(string text, string reason) =>
        new(text, Applied: false, Reason: reason);

    [GeneratedRegex(
        @"^\s*<\|(?:channel|message|start|end)>\s*(?:thought|analysis|commentary|final|message|assistant)\s*(?:<(?:channel|message|start|end)\|>\s*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MalformedChannelPrefixRegex();

    [GeneratedRegex(
        @"^\s*(?:<\|tool_call>[\s\S]*?<tool_call\|>\s*)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProtocolOnlyToolCallsRegex();
}
