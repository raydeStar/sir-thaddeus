using System.Text.Json;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// User-facing description of a tool effect. This is deliberately independent
/// of any UI transport so desktop, headless, and tests observe the same facts.
/// </summary>
public sealed record ToolEffectDescriptor(
    string Kind,
    bool Mutating,
    bool Reversible,
    string Boundary,
    string Summary,
    string? Target,
    string? UndoStrategy,
    string Capability);

/// <summary>
/// Evidence produced after an effect attempt. Successful tool transport is not
/// automatically called independent verification.
/// </summary>
public sealed record ToolEffectOutcome(
    string Status,
    string Evidence,
    bool IndependentlyVerified,
    string? ResolvedTarget);

public static class ToolEffectClassifier
{
    public static ToolEffectDescriptor Describe(string toolName, string args)
    {
        var capability = ToolCapabilityRegistry.ResolveCapability(toolName);
        var canonical = toolName.Trim().ToLowerInvariant();
        var target = TryReadTarget(args);

        return capability switch
        {
            ToolCapability.WikiWrite => DescribeWikiWrite(canonical, target),
            ToolCapability.MemoryWrite => new(
                Kind: canonical.Contains("delete", StringComparison.Ordinal) ? "delete" :
                    canonical.Contains("update", StringComparison.Ordinal) ? "update" : "create",
                Mutating: true,
                Reversible: true,
                Boundary: "local",
                Summary: Humanize(toolName),
                Target: target,
                UndoStrategy: "memory-history",
                Capability: nameof(ToolCapability.MemoryWrite)),
            ToolCapability.FileWrite => new(
                Kind: "write",
                Mutating: true,
                Reversible: false,
                Boundary: "local",
                Summary: Humanize(toolName),
                Target: target,
                UndoStrategy: null,
                Capability: nameof(ToolCapability.FileWrite)),
            ToolCapability.SystemExecute => new(
                Kind: canonical.Contains("read", StringComparison.Ordinal) ? "read" : "execute",
                Mutating: !canonical.Contains("read", StringComparison.Ordinal) &&
                          !canonical.EndsWith("_preview", StringComparison.Ordinal),
                Reversible: canonical is "clipboard_write",
                Boundary: "local",
                Summary: Humanize(toolName),
                Target: target,
                UndoStrategy: canonical is "clipboard_write" ? "clipboard-history" : null,
                Capability: nameof(ToolCapability.SystemExecute)),
            ToolCapability.WebSearch or ToolCapability.BrowserNavigate => Read(
                toolName, target, "web", capability.Value),
            ToolCapability.WikiRead or ToolCapability.MemoryRead or ToolCapability.FileRead or
                ToolCapability.ScreenCapture or ToolCapability.TimeRead => Read(
                    toolName, target, "local", capability.Value),
            _ => Read(toolName, target, "local", capability ?? ToolCapability.Meta),
        };
    }

    public static ToolEffectOutcome Complete(
        ToolEffectDescriptor effect,
        string toolName,
        bool ok,
        string resultText)
    {
        if (!ok)
        {
            return new ToolEffectOutcome(
                Status: "failed",
                Evidence: "tool-error",
                IndependentlyVerified: false,
                ResolvedTarget: effect.Target);
        }

        var resolvedTarget = TryReadTarget(resultText) ?? effect.Target;
        if (effect.Mutating && effect.Capability == nameof(ToolCapability.WikiWrite) &&
            HasVersionedWikiEvidence(resultText))
        {
            return new ToolEffectOutcome(
                Status: "applied",
                Evidence: "versioned-wiki-state",
                IndependentlyVerified: true,
                ResolvedTarget: resolvedTarget);
        }

        return new ToolEffectOutcome(
            Status: effect.Mutating ? "applied" : "observed",
            Evidence: "tool-result",
            IndependentlyVerified: false,
            ResolvedTarget: resolvedTarget);
    }

    private static ToolEffectDescriptor DescribeWikiWrite(string canonical, string? target)
    {
        var kind = canonical.Contains("delete", StringComparison.Ordinal) ||
                   canonical.Contains("remove", StringComparison.Ordinal)
            ? "delete"
            : canonical.Contains("create", StringComparison.Ordinal)
                ? "create"
                : canonical.Contains("restore", StringComparison.Ordinal)
                    ? "restore"
                    : "update";
        var undo = kind switch
        {
            "create" when canonical.Contains("page", StringComparison.Ordinal) => "wiki-soft-delete",
            "delete" when canonical.Contains("page", StringComparison.Ordinal) => "wiki-restore",
            "restore" when canonical.Contains("page", StringComparison.Ordinal) => "wiki-revision",
            "update" when canonical.Contains("page", StringComparison.Ordinal) => "wiki-revision",
            _ => null,
        };

        return new ToolEffectDescriptor(
            Kind: kind,
            Mutating: true,
            Reversible: undo is not null,
            Boundary: "local",
            Summary: Humanize(canonical),
            Target: target,
            UndoStrategy: undo,
            Capability: nameof(ToolCapability.WikiWrite));
    }

    private static ToolEffectDescriptor Read(
        string toolName,
        string? target,
        string boundary,
        ToolCapability capability) =>
        new(
            Kind: "read",
            Mutating: false,
            Reversible: false,
            Boundary: boundary,
            Summary: Humanize(toolName),
            Target: target,
            UndoStrategy: null,
            Capability: capability.ToString());

    private static bool HasVersionedWikiEvidence(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return FindProperty(document.RootElement, "version") is { ValueKind: JsonValueKind.Number } ||
                   FindProperty(document.RootElement, "deleted") is { ValueKind: JsonValueKind.True } ||
                   FindProperty(document.RootElement, "restored") is { ValueKind: JsonValueKind.True };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? TryReadTarget(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var name in new[]
                     {
                         "page_id", "pageId", "id", "path", "title", "root_name",
                         "rootName", "folder_id", "folderId", "query", "url",
                     })
            {
                var property = FindProperty(document.RootElement, name);
                if (property is { ValueKind: JsonValueKind.String })
                {
                    var value = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value.Length <= 160 ? value : value[..157] + "...";
                }
            }
        }
        catch (JsonException)
        {
            // Arguments and results originate at an untrusted boundary.
        }
        return null;
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
                var nested = FindProperty(property.Value, name);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindProperty(item, name);
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string Humanize(string value) =>
        string.Join(' ', value
            .Replace('-', '_')
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}
