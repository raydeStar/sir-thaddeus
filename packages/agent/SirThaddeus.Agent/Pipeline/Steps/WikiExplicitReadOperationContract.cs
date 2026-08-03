using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

internal sealed record WikiExplicitReadProjection(
    bool Active,
    bool ToolAvailable,
    string? ToolName,
    IReadOnlyList<ToolDefinition> Tools,
    string Reason);

internal sealed record WikiExplicitReadBinding(
    bool Active,
    bool Allowed,
    string Arguments,
    string Reason);

/// <summary>
/// Projects an explicitly selected page read into one payload-only tool and
/// restores the trusted page identity at the audited execution boundary. It
/// never infers either read intent or target identity from user prose.
/// </summary>
internal static class WikiExplicitReadOperationContract
{
    public const string ReadToolName = "wiki_page_read";

    public static WikiExplicitReadProjection Project(
        WikiMutationTarget? target,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (!IsEligible(target))
            return new(false, false, null, advertisedTools, "inactive");

        var source = advertisedTools.FirstOrDefault(tool =>
            string.Equals(tool.Function?.Name, ReadToolName, StringComparison.OrdinalIgnoreCase));
        if (source?.Function is null)
            return new(true, false, ReadToolName, [], "read-tool-unavailable");

        var projected = source with
        {
            Function = source.Function with
            {
                Description =
                    $"Read the user-selected Wiki page '{target!.DisplayName}'. " +
                    "Page identity is runtime-owned; supply only an optional maximum character count.",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["maxChars"] = new Dictionary<string, object>
                        {
                            ["type"] = "integer",
                            ["minimum"] = 1,
                            ["maximum"] = 60000,
                            ["description"] = "Optional maximum Markdown characters to return.",
                        },
                    },
                    ["required"] = Array.Empty<string>(),
                    ["additionalProperties"] = false,
                },
            },
        };

        return new(true, true, ReadToolName, [projected], "explicit-page-read");
    }

    public static WikiExplicitReadBinding Bind(
        WikiMutationTarget? target,
        string toolName,
        string arguments)
    {
        if (!IsEligible(target))
            return new(false, true, arguments, "inactive");

        if (!string.Equals(toolName, ReadToolName, StringComparison.OrdinalIgnoreCase))
            return new(true, false, "{}", "tool-outside-approved-read");

        var payload = new Dictionary<string, object>
        {
            ["pageId"] = target!.PageId!,
        };
        if (TryReadMaxChars(arguments, out var maxChars))
            payload["maxChars"] = maxChars;

        return new(true, true, JsonSerializer.Serialize(payload), "bound-explicit-page-read");
    }

    public static string BuildBlockedResult(WikiMutationTarget target, string reason) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "wiki_explicit_read_mismatch",
                message =
                    $"Blocked: the approved read may apply only to selected Wiki page '{target.DisplayName}'.",
                reason,
            },
        });

    public static bool TryBuildVerifiedReceipt(
        WikiMutationTarget? target,
        string toolName,
        bool toolSucceeded,
        string result,
        out string receipt)
    {
        receipt = string.Empty;
        if (!toolSucceeded ||
            !IsEligible(target) ||
            !string.Equals(toolName, ReadToolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) ||
                ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("document", out var readDocument) ||
                !readDocument.TryGetProperty("page", out var page) ||
                !page.TryGetProperty("id", out var pageId) ||
                pageId.ValueKind != JsonValueKind.String ||
                !string.Equals(pageId.GetString(), target!.PageId, StringComparison.Ordinal) ||
                !readDocument.TryGetProperty("markdown", out var markdownElement) ||
                markdownElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(markdownElement.GetString()))
            {
                return false;
            }

            var displayName = JsonSerializer.Serialize(target.DisplayName);
            receipt = $"Selected Wiki page {displayName}:\n\n{markdownElement.GetString()!.Trim()}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsEligible(WikiMutationTarget? target) =>
        target is
        {
            Kind: WikiMutationTargetKind.Page,
            Operation: WikiMutationOperation.PageRead,
            PageId: { Length: > 0 },
        };

    private static bool TryReadMaxChars(string arguments, out int maxChars)
    {
        maxChars = default;
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "maxChars", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(property.Name, "max_chars", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var value))
                    return false;
                maxChars = Math.Clamp(value, 1, 60000);
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
