using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Thaddeus.Runtime.Modules;

public enum ModuleApprovalStatus
{
    Pending,
    Approved,
    Denied
}

public sealed record ModuleExecutionDefinition(
    string Type,
    string Command,
    IReadOnlyList<string> Args,
    string? Cwd,
    IReadOnlyList<string> EnvKeys);

public sealed record ModuleManifestDocument(
    string Id,
    string Name,
    string Version,
    string? Description,
    JsonObject? Permissions,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Jobs,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> MemoryNamespaces,
    ModuleExecutionDefinition? Execution,
    string ManifestPath);

public sealed record ModuleAuditEventDto(
    string Id,
    string ModuleId,
    string Action,
    string Result,
    DateTimeOffset At,
    string? Message,
    string? ToolName);

public sealed record ModuleToolDto(
    string Name,
    string? Description,
    JsonElement? InputSchema,
    bool CanInvokeManually);

public sealed record ModuleSummaryDto(
    string Id,
    string Name,
    string Version,
    string? Description,
    string ManifestPath,
    string Status,
    ModuleApprovalStatus ApprovalStatus,
    bool Disabled,
    int PermissionCount,
    int ToolCount,
    DateTimeOffset? LastStatusCheck,
    DateTimeOffset? LastInvocation,
    string? LastError);

public sealed record ModuleDetailDto(
    string Id,
    string Name,
    string Version,
    string? Description,
    string ManifestPath,
    string Status,
    ModuleApprovalStatus ApprovalStatus,
    bool Disabled,
    JsonObject? RequestedPermissions,
    IReadOnlyList<ModuleToolDto> Tools,
    IReadOnlyList<string> Jobs,
    IReadOnlyList<string> Hooks,
    IReadOnlyList<string> MemoryNamespaces,
    ModuleExecutionDefinition? Execution,
    DateTimeOffset? LastStatusCheck,
    DateTimeOffset? LastInvocation,
    string? LastError,
    IReadOnlyList<ModuleAuditEventDto> RecentAuditEvents);

public sealed record ModuleListResponse(IReadOnlyList<ModuleSummaryDto> Modules);

public sealed record ModuleInvokeRequest(JsonElement? Arguments);

public sealed record ModuleInvokeResponse(
    string ModuleId,
    string ToolName,
    bool Ok,
    string Content,
    JsonElement? Json,
    DateTimeOffset InvokedAt);

public sealed record ModuleStatusResponse(
    string ModuleId,
    string Status,
    DateTimeOffset CheckedAt,
    string? LastError,
    ModuleInvokeResponse? ProviderStatus);

public sealed record ModuleStateDocument(IReadOnlyDictionary<string, ModuleStateRecord> Modules)
{
    public static ModuleStateDocument Empty { get; } =
        new(new Dictionary<string, ModuleStateRecord>(StringComparer.OrdinalIgnoreCase));
}

public sealed record ModuleStateRecord(
    ModuleApprovalStatus ApprovalStatus,
    bool Disabled,
    string? LastError,
    DateTimeOffset? LastStatusCheck,
    DateTimeOffset? LastInvocation,
    IReadOnlyList<ModuleAuditEventDto> RecentAuditEvents)
{
    public static ModuleStateRecord Defaults { get; } =
        new(ModuleApprovalStatus.Pending, false, null, null, null, Array.Empty<ModuleAuditEventDto>());
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ModuleStateDocument))]
[JsonSerializable(typeof(ModuleStateRecord))]
[JsonSerializable(typeof(ModuleAuditEventDto))]
public partial class ModuleStateJsonContext : JsonSerializerContext
{
}
