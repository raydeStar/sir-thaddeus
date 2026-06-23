using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SirThaddeus.AuditLog;
using SirThaddeus.RuntimeHost;

namespace Thaddeus.Runtime.Modules;

public sealed class ModuleRuntimeException : Exception
{
    public ModuleRuntimeException(string message) : base(message)
    {
    }
}

public sealed class ModuleRuntimeService
{
    public const string HealthPackModuleId = "com.thaddeus.health";

    private static readonly JsonSerializerOptions s_json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IConfiguration _configuration;
    private readonly IModuleStateStore _stateStore;
    private readonly IAuditLogger _audit;
    private readonly ILogger<ModuleRuntimeService> _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    public ModuleRuntimeService(
        IConfiguration configuration,
        IModuleStateStore stateStore,
        IAuditLogger audit,
        ILogger<ModuleRuntimeService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ModuleSummaryDto>> ListAsync(CancellationToken ct)
    {
        var manifests = DiscoverManifests();
        var state = await _stateStore.GetAsync(ct).ConfigureAwait(false);
        return manifests
            .Select(manifest => ToSummary(manifest, GetState(state, manifest.Id)))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<ModuleDetailDto?> GetDetailAsync(string moduleId, CancellationToken ct)
    {
        var manifest = FindManifest(moduleId);
        if (manifest is null)
            return null;

        var state = await _stateStore.GetAsync(ct).ConfigureAwait(false);
        return ToDetail(manifest, GetState(state, manifest.Id));
    }

    public async Task<IReadOnlyList<ModuleToolDto>?> ListToolsAsync(string moduleId, CancellationToken ct)
    {
        var detail = await GetDetailAsync(moduleId, ct).ConfigureAwait(false);
        return detail?.Tools;
    }

    public async Task<JsonObject?> GetPermissionsAsync(string moduleId, CancellationToken ct)
    {
        var detail = await GetDetailAsync(moduleId, ct).ConfigureAwait(false);
        return detail?.RequestedPermissions;
    }

    public async Task<IReadOnlyList<ModuleAuditEventDto>?> GetAuditEventsAsync(
        string moduleId,
        int limit,
        CancellationToken ct)
    {
        var manifest = FindManifest(moduleId);
        if (manifest is null)
            return null;

        var state = await _stateStore.GetAsync(ct).ConfigureAwait(false);
        return GetState(state, manifest.Id).RecentAuditEvents.TakeLast(Math.Clamp(limit, 1, 100)).ToArray();
    }

    public Task<ModuleDetailDto?> ApproveAsync(string moduleId, CancellationToken ct) =>
        MutateStateAsync(moduleId, "module.approved", "ok", state => state with
        {
            ApprovalStatus = ModuleApprovalStatus.Approved,
            Disabled = false,
            LastError = null
        }, ct);

    public Task<ModuleDetailDto?> DenyAsync(string moduleId, CancellationToken ct) =>
        MutateStateAsync(moduleId, "module.denied", "denied", state => state with
        {
            ApprovalStatus = ModuleApprovalStatus.Denied
        }, ct);

    public Task<ModuleDetailDto?> DisableAsync(string moduleId, CancellationToken ct) =>
        MutateStateAsync(moduleId, "module.disabled", "ok", state => state with
        {
            Disabled = true
        }, ct);

    public Task<ModuleDetailDto?> EnableAsync(string moduleId, CancellationToken ct) =>
        MutateStateAsync(moduleId, "module.enabled", "ok", state => state with
        {
            Disabled = false,
            LastError = null
        }, ct);

    public async Task<ModuleStatusResponse?> CheckStatusAsync(string moduleId, CancellationToken ct)
    {
        var manifest = FindManifest(moduleId);
        if (manifest is null)
            return null;

        var checkedAt = DateTimeOffset.UtcNow;
        var initialState = GetState(await _stateStore.GetAsync(ct).ConfigureAwait(false), manifest.Id);
        if (initialState.Disabled || initialState.ApprovalStatus is not ModuleApprovalStatus.Approved)
        {
            var reason = initialState.Disabled
                ? "Module is disabled."
                : "Module approval is required before runtime status tools can run.";
            await UpdateStateAsync(
                    manifest.Id,
                    "module.status_check",
                    "denied",
                    s => s with { LastStatusCheck = checkedAt, LastError = null },
                    reason,
                    toolName: null,
                    ct)
                .ConfigureAwait(false);

            var gated = GetState(await _stateStore.GetAsync(ct).ConfigureAwait(false), manifest.Id);
            return new ModuleStatusResponse(
                manifest.Id,
                ComputeStatus(gated),
                checkedAt,
                gated.LastError,
                ProviderStatus: null);
        }

        ModuleInvokeResponse? providerStatus = null;
        string? error = null;
        try
        {
            if (manifest.Tools.Contains("health.provider_status", StringComparer.OrdinalIgnoreCase))
                providerStatus = await InvokeToolAsync(moduleId, "health.provider_status", null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            await UpdateStateAsync(
                    manifest.Id,
                    "module.status_check",
                    "error",
                    s => s with { LastStatusCheck = checkedAt, LastError = error },
                    error,
                    toolName: null,
                    ct)
                .ConfigureAwait(false);
        }

        if (providerStatus is not null || error is null)
        {
            await UpdateStateAsync(
                    manifest.Id,
                    "module.status_check",
                    "ok",
                    s => s with { LastStatusCheck = checkedAt, LastError = null },
                    null,
                    toolName: null,
                    ct)
                .ConfigureAwait(false);
        }

        var current = await _stateStore.GetAsync(ct).ConfigureAwait(false);
        var state = GetState(current, manifest.Id);
        return new ModuleStatusResponse(
            manifest.Id,
            ComputeStatus(state),
            checkedAt,
            state.LastError,
            providerStatus);
    }

    public async Task<ModuleInvokeResponse> InvokeToolAsync(
        string moduleId,
        string toolName,
        JsonElement? arguments,
        CancellationToken ct)
    {
        var manifest = FindManifest(moduleId)
            ?? throw new KeyNotFoundException($"Module '{moduleId}' was not found.");

        if (!manifest.Tools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
        {
            var message = $"Module '{moduleId}' does not expose tool '{toolName}'.";
            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "denied",
                    s => s,
                    message,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            throw new ModuleRuntimeException(message);
        }

        var state = GetState(await _stateStore.GetAsync(ct).ConfigureAwait(false), manifest.Id);
        if (state.Disabled)
        {
            var message = $"Module '{manifest.Name}' is disabled. Enable it from the Modules tab before invoking tools.";
            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "denied",
                    s => s,
                    message,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            throw new ModuleRuntimeException(message);
        }

        if (state.ApprovalStatus is not ModuleApprovalStatus.Approved)
        {
            var message = $"Module '{manifest.Name}' needs approval in the Modules tab before its tools can run.";
            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "denied",
                    s => s,
                    message,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            throw new ModuleRuntimeException(message);
        }

        if (manifest.Execution is null || !string.Equals(manifest.Execution.Type, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            var message = $"Module '{manifest.Name}' does not have a supported stdio execution definition.";
            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "error",
                    s => s with { LastError = message },
                    message,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            throw new ModuleRuntimeException(message);
        }

        var invokedAt = DateTimeOffset.UtcNow;
        try
        {
            var argsJson = arguments.HasValue ? arguments.Value.GetRawText() : "{}";
            await using var client = new StdioMcpToolClient(
                manifest.Execution.Command,
                BuildChildEnvironment(manifest),
                "sir-thaddeus-runtime",
                "0.1.0",
                _audit,
                manifest.Execution.Args,
                ResolveWorkingDirectory(manifest));

            await client.StartAsync(ct).ConfigureAwait(false);
            var content = await client.CallToolAsync(toolName, argsJson, ct).ConfigureAwait(false);
            var response = new ModuleInvokeResponse(
                manifest.Id,
                toolName,
                true,
                content,
                TryParseJson(content),
                invokedAt);

            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "ok",
                    s => s with { LastInvocation = invokedAt, LastError = null },
                    null,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "module.invoke_failed module={ModuleId} tool={ToolName}", moduleId, toolName);
            await UpdateStateAsync(
                    manifest.Id,
                    "module.tool_invoked",
                    "error",
                    s => s with { LastInvocation = invokedAt, LastError = ex.Message },
                    ex.Message,
                    toolName,
                    ct)
                .ConfigureAwait(false);
            throw;
        }
    }

    public bool IsHealthBriefRequest(string userText)
    {
        var text = (userText ?? string.Empty).ToLowerInvariant();
        return (text.Contains("morning") || text.Contains("today"))
            && text.Contains("health")
            && (text.Contains("brief") || text.Contains("strategy") || text.Contains("readiness"));
    }

    public async Task<string> BuildHealthBriefChatResponseAsync(CancellationToken ct)
    {
        var detail = await GetDetailAsync(HealthPackModuleId, ct).ConfigureAwait(false);
        if (detail is null)
            return "I do not see the Health Pack installed yet. Open the Modules tab and check that the Health Pack manifest is available.";

        if (detail.Disabled)
            return "The Health Pack is installed but disabled. Open the Modules tab, enable it, and I can generate your morning health brief.";

        if (detail.ApprovalStatus is not ModuleApprovalStatus.Approved)
            return "The Health Pack is installed but still needs approval. Open the Modules tab to review its permissions, then approve it so I can use its health tools.";

        try
        {
            var providerStatus = await InvokeToolAsync(
                    HealthPackModuleId,
                    "health.provider_status",
                    null,
                    ct)
                .ConfigureAwait(false);

            if (NeedsHealthSetup(providerStatus.Json))
            {
                var lifecycle = ReadString(providerStatus.Json, "lifecycle") ?? "not_configured";
                var provider = ReadString(providerStatus.Json, "selectedProvider")
                    ?? ReadString(providerStatus.Json, "providerName")
                    ?? "Health Pack";
                return $"The Health Pack is approved, but {provider} is not ready yet ({lifecycle}). Open Modules -> Health Pack to choose a provider, connect it, and run a sync or backfill. Once it is connected, I can generate the morning health brief from the module.";
            }

            var result = await InvokeToolAsync(
                    HealthPackModuleId,
                    "health.get_morning_strategy_brief",
                    null,
                    ct)
                .ConfigureAwait(false);

            return FormatHealthBriefForChat(result);
        }
        catch (Exception ex)
        {
            return "I could not reach the Health Pack for your morning brief: "
                + ex.Message
                + " Open the Modules tab to inspect its status and configuration.";
        }
    }

    internal IReadOnlyList<ModuleManifestDocument> DiscoverManifests()
    {
        var paths = ResolveConfiguredManifestPaths()
            .Concat(ResolveDefaultManifestPaths())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();

        var manifests = new List<ModuleManifestDocument>();
        foreach (var path in paths)
        {
            try
            {
                manifests.Add(LoadManifest(path));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "module.manifest_load_failed path={Path}", path);
            }
        }

        return manifests
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToArray();
    }

    private async Task<ModuleDetailDto?> MutateStateAsync(
        string moduleId,
        string action,
        string result,
        Func<ModuleStateRecord, ModuleStateRecord> mutate,
        CancellationToken ct)
    {
        var manifest = FindManifest(moduleId);
        if (manifest is null)
            return null;

        await UpdateStateAsync(manifest.Id, action, result, mutate, null, null, ct).ConfigureAwait(false);
        return await GetDetailAsync(manifest.Id, ct).ConfigureAwait(false);
    }

    private async Task UpdateStateAsync(
        string moduleId,
        string action,
        string result,
        Func<ModuleStateRecord, ModuleStateRecord> mutate,
        string? message,
        string? toolName,
        CancellationToken ct)
    {
        await _stateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var document = await _stateStore.GetAsync(ct).ConfigureAwait(false);
            var modules = new Dictionary<string, ModuleStateRecord>(document.Modules, StringComparer.OrdinalIgnoreCase);
            var current = GetState(document, moduleId);
            var evt = new ModuleAuditEventDto(
                "ma_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant(),
                moduleId,
                action,
                result,
                DateTimeOffset.UtcNow,
                message,
                toolName);

            var events = (current.RecentAuditEvents ?? Array.Empty<ModuleAuditEventDto>())
                .Append(evt)
                .TakeLast(50)
                .ToArray();

            modules[moduleId] = mutate(current) with { RecentAuditEvents = events };
            await _stateStore.ReplaceAsync(new ModuleStateDocument(modules), ct).ConfigureAwait(false);

            _audit.Append(new AuditEvent
            {
                Actor = "runtime",
                Action = action,
                Target = moduleId,
                Result = result,
                Details = new Dictionary<string, object>
                {
                    ["moduleId"] = moduleId,
                    ["toolName"] = toolName ?? "",
                    ["message"] = message ?? ""
                }
            });
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private ModuleManifestDocument? FindManifest(string moduleId) =>
        DiscoverManifests()
            .FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase));

    private static ModuleSummaryDto ToSummary(ModuleManifestDocument manifest, ModuleStateRecord state) =>
        new(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Description,
            manifest.ManifestPath,
            ComputeStatus(state),
            state.ApprovalStatus,
            state.Disabled,
            CountPermissions(manifest.Permissions),
            manifest.Tools.Count,
            state.LastStatusCheck,
            state.LastInvocation,
            state.LastError);

    private static ModuleDetailDto ToDetail(ModuleManifestDocument manifest, ModuleStateRecord state) =>
        new(
            manifest.Id,
            manifest.Name,
            manifest.Version,
            manifest.Description,
            manifest.ManifestPath,
            ComputeStatus(state),
            state.ApprovalStatus,
            state.Disabled,
            manifest.Permissions?.DeepClone().AsObject(),
            manifest.Tools.Select(t => new ModuleToolDto(t, null, null, CanInvokeManually(t))).ToArray(),
            manifest.Jobs,
            manifest.Hooks,
            manifest.MemoryNamespaces,
            manifest.Execution,
            state.LastStatusCheck,
            state.LastInvocation,
            state.LastError,
            state.RecentAuditEvents.TakeLast(20).Reverse().ToArray());

    private static ModuleStateRecord GetState(ModuleStateDocument document, string moduleId) =>
        document.Modules.TryGetValue(moduleId, out var state) ? state : ModuleStateRecord.Defaults;

    private static string ComputeStatus(ModuleStateRecord state)
    {
        if (state.Disabled)
            return "disabled";
        if (!string.IsNullOrWhiteSpace(state.LastError))
            return "error";
        return state.ApprovalStatus switch
        {
            ModuleApprovalStatus.Approved => "approved",
            ModuleApprovalStatus.Denied => "denied",
            _ => "pending"
        };
    }

    private static bool CanInvokeManually(string toolName) =>
        toolName.EndsWith("provider_status", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("secret_store_status", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("morning_strategy_brief", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("provider_audit_events", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("backfill", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("get_baselines", StringComparison.OrdinalIgnoreCase)
        || toolName.EndsWith("get_daily_snapshot", StringComparison.OrdinalIgnoreCase);

    private static int CountPermissions(JsonObject? permissions)
    {
        if (permissions is null)
            return 0;

        var count = 0;
        foreach (var property in permissions)
            count += CountNode(property.Value);
        return count;
    }

    private static int CountNode(JsonNode? node)
    {
        if (node is null)
            return 0;
        if (node is JsonArray array)
            return Math.Max(1, array.Count);
        if (node is JsonObject obj)
            return obj.Sum(p => CountNode(p.Value));
        return 1;
    }

    private ModuleManifestDocument LoadManifest(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var execution = ParseExecution(root, manifestDir);
        return new ModuleManifestDocument(
            RequiredString(root, "id"),
            RequiredString(root, "name"),
            RequiredString(root, "version"),
            OptionalString(root, "description"),
            root.TryGetProperty("permissions", out var permissions)
                ? JsonNode.Parse(permissions.GetRawText())?.AsObject()
                : null,
            StringArray(root, "tools"),
            StringArray(root, "jobs"),
            StringArray(root, "hooks"),
            StringArray(root, "memoryNamespaces"),
            execution,
            Path.GetFullPath(path));
    }

    private static ModuleExecutionDefinition? ParseExecution(JsonElement root, string manifestDir)
    {
        if (!root.TryGetProperty("execution", out var execution) || execution.ValueKind is not JsonValueKind.Object)
            return null;

        var type = OptionalString(execution, "type") ?? "";
        var command = OptionalString(execution, "command") ?? "";
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(command))
            return null;

        var cwd = OptionalString(execution, "cwd");
        if (string.IsNullOrWhiteSpace(cwd))
            cwd = manifestDir;
        else if (!Path.IsPathRooted(cwd))
            cwd = Path.GetFullPath(Path.Combine(manifestDir, cwd));

        IReadOnlyList<string> envKeys = Array.Empty<string>();
        if (execution.TryGetProperty("env", out var env) && env.ValueKind is JsonValueKind.Object)
            envKeys = env.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

        return new ModuleExecutionDefinition(
            type,
            command,
            StringArray(execution, "args"),
            cwd,
            envKeys);
    }

    private IReadOnlyDictionary<string, string> BuildChildEnvironment(ModuleManifestDocument manifest)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var source = manifest.Execution?.EnvKeys ?? Array.Empty<string>();
        foreach (var key in source)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value))
                env[key] = value;
        }

        return env;
    }

    private static string ResolveWorkingDirectory(ModuleManifestDocument manifest) =>
        string.IsNullOrWhiteSpace(manifest.Execution?.Cwd)
            ? Path.GetDirectoryName(manifest.ManifestPath)!
            : manifest.Execution!.Cwd!;

    private IEnumerable<string> ResolveConfiguredManifestPaths()
    {
        var configured = _configuration.GetSection("Modules:ManifestPaths").Get<string[]>() ?? Array.Empty<string>();
        foreach (var path in configured)
        {
            if (!string.IsNullOrWhiteSpace(path))
                yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }

        var env = Environment.GetEnvironmentVariable("THADDEUS_MODULE_MANIFESTS");
        if (string.IsNullOrWhiteSpace(env))
            yield break;

        foreach (var path in env.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
    }

    private static IEnumerable<string> ResolveDefaultManifestPaths()
    {
        foreach (var root in CandidateRoots())
        {
            var candidate = Path.Combine(root, "thaddeus-health-pack", "manifest.json");
            if (File.Exists(candidate))
                yield return Path.GetFullPath(candidate);
        }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        var seeds = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var seed in seeds)
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                yield return dir.FullName;
                dir = dir.Parent;
            }
        }
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidOperationException($"Manifest is missing '{name}'.");

    private static string? OptionalString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static IReadOnlyList<string> StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not JsonValueKind.Array)
            return Array.Empty<string>();

        return value.EnumerateArray()
            .Where(item => item.ValueKind is JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static JsonElement? TryParseJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string FormatHealthBriefForChat(ModuleInvokeResponse result)
    {
        if (result.Json is null)
            return result.Content;

        var root = result.Json.Value;
        var date = ReadString(root, "date");
        var readiness = ReadString(root, "readinessLevel");
        var lines = new List<string>
        {
            string.IsNullOrWhiteSpace(date)
                ? "Here is your morning health brief."
                : $"Here is your morning health brief for {date}."
        };

        if (!string.IsNullOrWhiteSpace(readiness))
            lines.Add($"Readiness: {readiness}.");

        AddArraySection(lines, root, "keySignals", "Key signals");
        AddArraySection(lines, root, "recommendations", "Recommendations");
        AddArraySection(lines, root, "caveats", "Caveats");

        return string.Join(Environment.NewLine + Environment.NewLine, lines);
    }

    private static void AddArraySection(List<string> lines, JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind is not JsonValueKind.Array)
            return;

        var items = array.EnumerateArray()
            .Select(item => item.ValueKind is JsonValueKind.String ? item.GetString() : item.GetRawText())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Take(5)
            .ToArray();

        if (items.Length > 0)
            lines.Add(label + ":" + Environment.NewLine + string.Join(Environment.NewLine, items.Select(item => "- " + item)));
    }

    private static string? ReadString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind is JsonValueKind.String ? value.GetString() : null;
    }

    private static string? ReadString(JsonElement? root, string property) =>
        root.HasValue ? ReadString(root.Value, property) : null;

    private static bool NeedsHealthSetup(JsonElement? status)
    {
        if (!status.HasValue)
            return false;

        var root = status.Value;
        var provider = ReadString(root, "selectedProvider") ?? ReadString(root, "providerName");
        if (string.Equals(provider, "mock", StringComparison.OrdinalIgnoreCase))
            return false;

        if (root.TryGetProperty("connected", out var connected) &&
            connected.ValueKind is JsonValueKind.True)
        {
            return false;
        }

        var lifecycle = ReadString(root, "lifecycle");
        return lifecycle is "not_configured" or "configured" or "auth_required" or "auth_in_progress" or "error" or "revoked";
    }
}
