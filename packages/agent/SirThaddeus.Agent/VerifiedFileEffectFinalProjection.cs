using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Agent;

public sealed record VerifiedFileEffectFinalProjectionResult(
    string Text,
    bool Applied,
    string Reason);

/// <summary>
/// Builds a short user-visible completion only when one direct file mutation
/// has an independently verified receipt. The projector never includes file
/// content, hashes, or absolute paths, and it stays inactive when another
/// model turn is needed to satisfy the user's response contract.
/// </summary>
public static partial class VerifiedFileEffectFinalProjection
{
    private const string FileWrite = "file_write";
    private const string FileReplace = "file_replace";

    public static VerifiedFileEffectFinalProjectionResult Project(
        string? userRequest,
        IReadOnlyList<ToolCallRecord> toolCalls,
        int currentBatchCallCount)
    {
        if (currentBatchCallCount != 1)
            return Inactive("multi_call_batch");

        var mutations = toolCalls
            .Where(call => IsMutationTool(call.ToolName))
            .ToArray();
        if (mutations.Length != 1)
            return Inactive("mutation_count");

        var requestLead = RequestLead(userRequest);
        if (string.IsNullOrWhiteSpace(requestLead))
            return Inactive("empty_request");
        if (ExplicitFormatPattern().IsMatch(requestLead))
            return Inactive("explicit_format");
        if (FollowUpPattern().IsMatch(requestLead))
            return Inactive("follow_up_requested");
        if (NonActionPattern().IsMatch(requestLead))
            return Inactive("hypothetical_or_deferred");
        if (ConditionalFailurePattern().IsMatch(requestLead))
            return Inactive("conditional_failure_contract");

        var call = mutations[0];
        if (!call.Success)
            return Inactive("tool_failed");
        if (!TryParseObject(call.Arguments, out var arguments) ||
            !TryParseObject(call.Result, out var result))
        {
            return Inactive("invalid_json");
        }

        if (!TryReadTrue(result, "ok") || !TryReadTrue(result, "verified"))
            return Inactive("not_verified");
        if (!TryReadNonNegativeInteger(result, "bytes"))
            return Inactive("invalid_bytes");
        if (!TryReadString(result, "sha256", out var sha256) ||
            !Sha256Pattern().IsMatch(sha256))
        {
            return Inactive("invalid_sha256");
        }
        if (!TryReadString(arguments, "path", out var requestedPath) ||
            !TryReadString(result, "path", out var observedPath))
        {
            return Inactive("missing_path");
        }

        var requestedName = FileName(requestedPath);
        if (requestedName.Length == 0 ||
            !string.Equals(requestedName, FileName(observedPath), StringComparison.OrdinalIgnoreCase))
        {
            return Inactive("target_mismatch");
        }
        if (!requestLead.Contains(requestedName, StringComparison.OrdinalIgnoreCase))
            return Inactive("target_not_explicit");

        var verb = string.Equals(call.ToolName, FileWrite, StringComparison.OrdinalIgnoreCase)
            ? "wrote"
            : "updated";
        return new VerifiedFileEffectFinalProjectionResult(
            $"Done - I {verb} and verified `{requestedName}`.",
            Applied: true,
            Reason: "verified_single_effect");
    }

    public static bool IsMutationTool(string? toolName) =>
        string.Equals(toolName, FileWrite, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(toolName, FileReplace, StringComparison.OrdinalIgnoreCase);

    private static string RequestLead(string? userRequest)
    {
        if (string.IsNullOrWhiteSpace(userRequest))
            return string.Empty;

        var lead = userRequest[..Math.Min(userRequest.Length, 1000)]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var blockEnd = lead.IndexOf("\n\n", StringComparison.Ordinal);
        return blockEnd >= 0 ? lead[..blockEnd] : lead;
    }

    private static string FileName(string path)
    {
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return (separator >= 0 ? normalized[(separator + 1)..] : normalized).Trim();
    }

    private static bool TryParseObject(string? value, out JsonElement root)
    {
        root = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

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

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.String)
            return false;
        value = node.GetString()?.Trim() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryReadTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.True;

    private static bool TryReadNonNegativeInteger(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) &&
        node.ValueKind == JsonValueKind.Number &&
        node.TryGetInt64(out var number) &&
        number >= 0;

    private static VerifiedFileEffectFinalProjectionResult Inactive(string reason) =>
        new(string.Empty, Applied: false, reason);

    [GeneratedRegex(
        @"\b(?:json|xml|yaml|csv)\b[^.!?\n]{0,48}\b(?:only|exactly|response|reply|return)\b|" +
        @"\b(?:reply|respond|return|answer)\b[^.!?\n]{0,48}\b(?:only|just|exactly)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitFormatPattern();

    [GeneratedRegex(
        @"\b(?:explain|summari[sz]e|show\s+me|read\s+back|display|tell\s+me\s+what|" +
        @"describe\s+what|list\s+what)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FollowUpPattern();

    [GeneratedRegex(
        @"\b(?:could|would)\s+(?:you\s+)?(?:create|write|replace|update)|" +
        @"\b(?:later|after\s+i|not\s+now|do\s+not\s+(?:call|create|modify|write|replace))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NonActionPattern();

    [GeneratedRegex(
        @"\bif\b[^.!?\n]{0,96}\b(?:rejects?|rejected|fails?|failed|absent|missing|otherwise)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConditionalFailurePattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
}
