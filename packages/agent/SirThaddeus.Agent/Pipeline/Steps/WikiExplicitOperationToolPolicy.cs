using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

internal sealed record WikiExplicitOperationProjection(
    bool Active,
    IReadOnlyList<ToolDefinition> Tools,
    int WithheldWriteCount);

internal sealed record WikiExplicitOperationDecision(
    bool Active,
    bool Allowed,
    string Reason);

/// <summary>
/// A selected Wiki target scopes identity but does not itself authorize a
/// mutation. Without a typed operation, keep the target read-only for the turn.
/// The same rule is enforced both in model-visible projection and immediately
/// before the audited execution boundary.
/// </summary>
internal static class WikiExplicitOperationToolPolicy
{
    public static WikiExplicitOperationProjection Project(
        WikiMutationTarget? target,
        IReadOnlyList<ToolDefinition> advertisedTools)
    {
        if (target is null || target.Operation is not null)
            return new(false, advertisedTools, 0);

        var projected = advertisedTools
            .Where(tool => !IsWikiWrite(tool.Function?.Name))
            .ToArray();
        return new(true, projected, advertisedTools.Count - projected.Length);
    }

    public static WikiExplicitOperationDecision EvaluateCall(
        WikiMutationTarget? target,
        string toolName)
    {
        if (target is null || target.Operation is not null)
            return new(false, true, "inactive");

        return IsWikiWrite(toolName)
            ? new(true, false, "typed-operation-required")
            : new(true, true, "read-or-non-wiki-tool");
    }

    public static string BuildBlockedResult(WikiMutationTarget target) =>
        JsonSerializer.Serialize(new
        {
            error = new
            {
                code = "wiki_explicit_operation_required",
                message =
                    $"Blocked: the selected Wiki {target.Kind.ToString().ToLowerInvariant()} " +
                    $"'{target.DisplayName}' is read-only for this submission because no write operation was approved.",
            },
        });

    private static bool IsWikiWrite(string? toolName) =>
        !string.IsNullOrWhiteSpace(toolName) &&
        ToolCapabilityRegistry.ResolveCapability(toolName) == ToolCapability.WikiWrite;
}
