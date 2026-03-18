namespace SirThaddeus.Contracts;

public sealed record SearchStatusResponse(
    bool LiveSearchAvailable,
    string EffectiveStatus,
    string EffectiveMessage,
    string SearchMode,
    string WebPermission,
    SearchProviderStatusDto Searxng,
    HostedSearchApiStatusDto SearchApi,
    McpRuntimeStatusDto Mcp,
    SearchTraceStatusDto LastProviderTrace,
    DateTimeOffset CheckedAtUtc);

public sealed record SearchProviderStatusDto(
    string Status,
    string Message,
    string BaseUrl,
    bool Reachable,
    bool ManagedByRuntime,
    bool AutoStartEnabled,
    string LastLaunchStatus);

public sealed record HostedSearchApiStatusDto(
    string Status,
    string Message,
    string Provider,
    string BaseUrl,
    string Engine,
    bool Configured);

public sealed record McpRuntimeStatusDto(
    string Status,
    string Message,
    string ServerPath,
    bool ToolsEnabled,
    bool ToolsAvailable);

public sealed record SearchTraceStatusDto(
    string Status,
    string RequestedQuery,
    string EffectiveQuery,
    string Provider,
    string PathSummary,
    string Failure,
    DateTimeOffset RecordedAtUtc);
