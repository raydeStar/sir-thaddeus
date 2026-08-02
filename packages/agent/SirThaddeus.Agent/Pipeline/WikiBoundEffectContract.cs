using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline;

public sealed record WikiBoundEffectProjection(
    bool Active,
    bool ToolAvailable,
    string? ToolName,
    IReadOnlyList<ToolDefinition> Tools,
    string Reason);

public sealed record WikiBoundEffectBinding(
    bool Active,
    bool Allowed,
    string Arguments,
    string Reason);

/// <summary>
/// Projects one explicitly user-approved Wiki operation into a payload-only
/// model contract and restores runtime-owned target identity immediately before
/// the audited tool boundary. It never infers either operation or target from
/// prose and is inactive unless both were supplied as typed turn state.
/// </summary>
public static class WikiBoundEffectContract
{
    private sealed record OperationContract(
        WikiMutationTargetKind TargetKind,
        string ToolName,
        IReadOnlyDictionary<string, object> Properties,
        IReadOnlySet<string> Required);

    private static readonly IReadOnlyDictionary<WikiMutationOperation, OperationContract> Contracts =
        new Dictionary<WikiMutationOperation, OperationContract>
        {
            [WikiMutationOperation.PageCreate] = new(
                WikiMutationTargetKind.Root,
                "wiki_page_create",
                new Dictionary<string, object>
                {
                    ["title"] = StringProperty(
                        "Page name requested with wording such as titled or named. This is not body content."),
                    ["markdown"] = StringProperty(
                        "Exact page body/content from the original user request. This is not the page title."),
                },
                new HashSet<string>(StringComparer.Ordinal) { "title", "markdown" }),
            [WikiMutationOperation.PageUpdate] = new(
                WikiMutationTargetKind.Page,
                "wiki_page_update_by_name",
                new Dictionary<string, object>
                {
                    ["markdown"] = StringProperty(
                        "Exact full replacement Markdown from the original user request."),
                },
                new HashSet<string>(StringComparer.Ordinal) { "markdown" }),
            [WikiMutationOperation.PageRename] = new(
                WikiMutationTargetKind.Page,
                "wiki_page_rename_by_name",
                new Dictionary<string, object>
                {
                    ["newTitle"] = StringProperty("New page title."),
                },
                new HashSet<string>(StringComparer.Ordinal) { "newTitle" }),
            [WikiMutationOperation.PageDelete] = new(
                WikiMutationTargetKind.Page,
                "wiki_page_delete_by_name",
                new Dictionary<string, object>(),
                new HashSet<string>(StringComparer.Ordinal)),
            [WikiMutationOperation.RootRename] = new(
                WikiMutationTargetKind.Root,
                "wiki_root_rename",
                new Dictionary<string, object>
                {
                    ["name"] = StringProperty("New root display name."),
                },
                new HashSet<string>(StringComparer.Ordinal) { "name" }),
        };

    public static bool TryParseOperation(string? value, out WikiMutationOperation operation)
    {
        operation = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim().Replace("-", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out operation) && Contracts.ContainsKey(operation);
    }

    public static bool IsCompatible(WikiMutationTargetKind targetKind, WikiMutationOperation operation) =>
        Contracts.TryGetValue(operation, out var contract) && contract.TargetKind == targetKind;

    public static string DisplayName(WikiMutationOperation operation) => operation switch
    {
        WikiMutationOperation.PageCreate => "create a page",
        WikiMutationOperation.PageUpdate => "replace the page",
        WikiMutationOperation.PageRename => "rename the page",
        WikiMutationOperation.PageDelete => "delete the page",
        WikiMutationOperation.RootRename => "rename the root",
        _ => operation.ToString(),
    };

    public static WikiBoundEffectProjection Project(
        WikiMutationTarget? target,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (target?.Operation is not { } operation)
            return new(false, false, null, advertisedTools, "inactive");

        if (!Contracts.TryGetValue(operation, out var contract) || contract.TargetKind != target.Kind)
            return new(true, false, null, [], "operation-target-mismatch");

        var source = advertisedTools.FirstOrDefault(tool =>
            string.Equals(tool.Function?.Name, contract.ToolName, StringComparison.OrdinalIgnoreCase));
        if (source?.Function is null)
            return new(true, false, contract.ToolName, [], "approved-tool-unavailable");

        var projected = source with
        {
            Function = source.Function with
            {
                Description =
                    $"Apply the user-approved operation to the runtime-bound Wiki {target.Kind.ToString().ToLowerInvariant()} '{target.DisplayName}'. " +
                    "Supply only values requested in the original user request; target identity and execution metadata are runtime-owned. " +
                    "Bracketed orchestration text such as [USER-APPROVED WORK PLAN] is metadata and must never be copied into payload values.",
                Parameters = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = contract.Properties,
                    ["required"] = contract.Required.ToArray(),
                    ["additionalProperties"] = false,
                },
            },
        };
        return new(true, true, contract.ToolName, [projected], "approved-effect");
    }

    public static WikiBoundEffectBinding Bind(
        WikiMutationTarget? target,
        string toolName,
        string arguments)
    {
        if (target?.Operation is not { } operation)
            return new(false, true, arguments, "inactive");

        if (!Contracts.TryGetValue(operation, out var contract) ||
            contract.TargetKind != target.Kind ||
            !string.Equals(toolName, contract.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return new(true, false, "{}", "tool-outside-approved-effect");
        }

        if (!TryReadPayload(arguments, contract, out var payload))
            return new(true, false, "{}", "invalid-payload");

        switch (operation)
        {
            case WikiMutationOperation.PageCreate:
                payload["rootId"] = target.RootId;
                break;
            case WikiMutationOperation.PageUpdate:
            case WikiMutationOperation.PageRename:
            case WikiMutationOperation.PageDelete:
                payload["rootName"] = target.RootName;
                payload["pageTitle"] = target.PageTitle!;
                break;
            case WikiMutationOperation.RootRename:
                payload["rootId"] = target.RootId;
                break;
            default:
                return new(true, false, "{}", "unsupported-operation");
        }

        return new(true, true, JsonSerializer.Serialize(payload), "bound");
    }

    public static string BuildBlockedResult(WikiMutationTarget target, string reason) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "wiki_bound_effect_mismatch",
                message =
                    $"Blocked: the approved operation '{DisplayName(target.Operation!.Value)}' may apply only to " +
                    $"the selected Wiki {target.Kind.ToString().ToLowerInvariant()} '{target.DisplayName}'.",
                reason,
            },
        });

    private static Dictionary<string, object> StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description,
    };

    private static bool TryReadPayload(
        string arguments,
        OperationContract contract,
        out Dictionary<string, object?> payload)
    {
        payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!contract.Properties.ContainsKey(property.Name) ||
                    property.Value.ValueKind != JsonValueKind.String)
                    return false;
                payload[property.Name] = property.Value.GetString();
            }

            foreach (var name in contract.Required)
            {
                if (!payload.TryGetValue(name, out var value) ||
                    value is not string text ||
                    string.IsNullOrWhiteSpace(text))
                    return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
