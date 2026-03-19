using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Principal;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.Routing;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.LlmClient;
using SirThaddeus.Memory.Sqlite;
using SirThaddeus.PersonalityEngine.Profiles;
using SirThaddeus.RuntimeHost;

var options = HeadlessOptions.Parse(args);
if (options.ShowHelp)
{
    PrintHelp();
    return;
}

var load = SettingsManager.LoadWithDiagnostics();
var settings = load.Settings;
var personalityStore = new PersonalityProfileStore();
var profilePreferredName = ResolvePreferredNameFromProfileStore(settings);
var handles = ResolvePromptHandles(settings, personalityStore, profilePreferredName);
var settingsUndoStack = new Stack<AppSettings>();

using var audit = JsonLineAuditLogger.CreateDefault();
using var searxngLauncher = new SearxngHostLauncher();
var searxngStartupGate = new SemaphoreSlim(1, 1);
var searxngLastLaunchStatus = options.ServerMode ? "Queued" : "Pending";
var searxngLastLaunchMessage = options.ServerMode
    ? "SearxNG startup has been queued for the background runtime."
    : "SearxNG startup has not run yet.";

void RecordSearxngLaunchStatus(string status, string message)
{
    searxngLastLaunchStatus = string.IsNullOrWhiteSpace(status) ? "Unknown" : status.Trim();
    searxngLastLaunchMessage = string.IsNullOrWhiteSpace(message) ? "" : message.Trim();
}

Task EnsureManagedSearxngSerializedAsync(AppSettings currentSettings, CancellationToken cancellationToken)
    => EnsureManagedSearxngSerializedCoreAsync(currentSettings, cancellationToken);

async Task EnsureManagedSearxngSerializedCoreAsync(AppSettings currentSettings, CancellationToken cancellationToken)
{
    await searxngStartupGate.WaitAsync(cancellationToken);
    try
    {
        await EnsureManagedSearxngAsync(
            currentSettings,
            options.EnableTools,
            searxngLauncher,
            audit,
            RecordSearxngLaunchStatus,
            cancellationToken);
    }
    finally
    {
        searxngStartupGate.Release();
    }
}

void QueueManagedSearxngUpdate(AppSettings currentSettings)
{
    _ = EnsureManagedSearxngSerializedAsync(currentSettings, CancellationToken.None);
}

if (options.ServerMode)
{
    QueueManagedSearxngUpdate(settings);
}
else
{
    await EnsureManagedSearxngSerializedAsync(settings, CancellationToken.None);
}

using var llm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildPrimary(settings));
using var gatekeeperLlm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildGatekeeper(settings));

var mcp = await RuntimeMcpClientFactory.CreateAsync(
    enableTools: options.EnableTools,
    allowDegradedStartup: options.ServerMode,
    overrideServerPath: options.McpServerPath,
    settings,
    audit,
    baseDirectory: Directory.GetCurrentDirectory(),
    clientName: "HeadlessRuntime",
    clientVersion: "0.1.0",
    cancellationToken: CancellationToken.None);
await using var mcpScope = mcp.Scope;
ConsolePermissionGate? permissionGate = null;
ApiPermissionGate? apiPermissionGate = null;

var toolsAvailable = mcp.ToolsAvailable;
if (options.EnableTools && !toolsAvailable)
{
    Console.WriteLine(mcp.Message);
}

IMcpToolClient agentMcp = mcp.Client;
if (toolsAvailable)
{
    IToolPermissionGate toolPermissionGate;
    if (options.ServerMode)
    {
        apiPermissionGate = new ApiPermissionGate(
            settings,
            () => RunExecutionContext.CurrentRunId);
        toolPermissionGate = apiPermissionGate;
    }
    else
    {
        permissionGate = new ConsolePermissionGate(
            audit,
            settings,
            persistGroupAsAlways: group =>
            {
                settings = PersistGroupPolicyAsAlways(settings, group);
                permissionGate?.UpdateSettings(settings);
            });
        toolPermissionGate = permissionGate;
    }

    agentMcp = new AuditedMcpToolClient(
        mcp.Client,
        audit,
        toolPermissionGate,
        sessionId: Guid.NewGuid().ToString("N")[..12],
        runtimeControls: () => RuntimeControlState.FromSettings(settings));
}

AutoMemoryExtractor? autoMemoryExtractor = null;
SqliteMemoryStore? autoMemoryStore = null;
if (toolsAvailable && settings.Memory.Enabled)
{
    var dbPath = RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPath(settings.Memory.DbPath);
    autoMemoryStore = new SqliteMemoryStore(dbPath);
    await autoMemoryStore.EnsureSchemaAsync(CancellationToken.None);

    autoMemoryExtractor = new AutoMemoryExtractor(
        llm,
        autoMemoryStore,
        log: (action, message) => audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = action,
            Result = "error",
            Details = new Dictionary<string, object>
            {
                ["message"] = message
            }
        }));
}

AgentOrchestrator BuildOrchestrator(AppSettings currentSettings)
{
    llm.UpdateOptions(RuntimeLlmOptionsFactory.BuildPrimary(currentSettings));
    gatekeeperLlm.UpdateOptions(RuntimeLlmOptionsFactory.BuildGatekeeper(currentSettings));

    var footmanRouter = new FastLlmFootmanRouter(gatekeeperLlm);

    return new AgentOrchestrator(
        llm,
        agentMcp,
        audit,
        currentSettings.Llm.SystemPrompt,
        activePersonalityId: currentSettings.ActivePersonalityId,
        personalityProfilesDirectory: SettingsManager.ResolvePersonalityProfilesDirectory(currentSettings),
        footmanRouter: footmanRouter,
        autoMemoryExtractor: autoMemoryExtractor,
        gatekeeperLlm: gatekeeperLlm)
    {
        ActiveProfileId = currentSettings.ActiveProfileId,
        MemoryEnabled = toolsAvailable && currentSettings.Memory.Enabled,
        UserLocationHint = currentSettings.GetEffectiveUserLocation(currentSettings.ActiveProfileId).GetResolvedLabel(),
        UserTimezone = currentSettings.GetEffectiveUserLocation(currentSettings.ActiveProfileId).GetResolvedTimezone(),
        PreferredUnits = currentSettings.Weather.GetNormalizedUnitSystem(),
        MaxTokensBudget = currentSettings.Llm.MaxTokens
    };
}

var orchestrator = BuildOrchestrator(settings);

string ResolveCurrentMcpServerPath(AppSettings currentSettings)
{
    return string.IsNullOrWhiteSpace(options.McpServerPath)
        ? RuntimePathResolver.ResolveMcpServerPath(currentSettings.Mcp.ServerPath, Directory.GetCurrentDirectory())
        : Path.GetFullPath(options.McpServerPath.Trim());
}

async Task<SearchTraceStatusDto> BuildLastProviderTraceAsync(CancellationToken ct)
{
    var events = await audit.ReadTailAsync(200, ct);
    var trace = events.LastOrDefault(evt =>
        string.Equals(evt.Action, "WEB_SEARCH_PROVIDER_TRACE", StringComparison.OrdinalIgnoreCase));
    if (trace is null)
    {
        return new SearchTraceStatusDto(
            Status: "None",
            RequestedQuery: "",
            EffectiveQuery: "",
            Provider: "",
            PathSummary: "",
            Failure: "",
            RecordedAtUtc: default);
    }

    var requestedQuery = ReadAuditDetail(trace, "requested_query");
    var effectiveQuery = ReadAuditDetail(trace, "effective_query");
    var provider = ReadAuditDetail(trace, "provider");
    var pathSummary = ReadAuditDetail(trace, "path_summary");
    var failureCode = ReadAuditDetail(trace, "failure_code");
    var failureMessage = ReadAuditDetail(trace, "failure_message");
    var failure = !string.IsNullOrWhiteSpace(failureCode) ? failureCode : failureMessage;

    return new SearchTraceStatusDto(
        Status: string.IsNullOrWhiteSpace(failure) ? "Recorded" : "Failure",
        RequestedQuery: requestedQuery,
        EffectiveQuery: effectiveQuery,
        Provider: provider,
        PathSummary: pathSummary,
        Failure: failure,
        RecordedAtUtc: trace.Timestamp);
}

async Task<SearchStatusResponse> BuildSearchStatusAsync(CancellationToken ct)
{
    var currentSettings = settings;
    var mode = NormalizeWebSearchMode(currentSettings.WebSearch.Mode);
    var webPermission = ToolGroupPolicy.ResolveEffectivePolicy(
        "web",
        ToolGroupPolicy.BuildSnapshot(currentSettings, isDebugBuild: false));

    var searxngBaseUrl = NormalizeBaseUrl(currentSettings.WebSearch.SearxngBaseUrl, "http://localhost:8080");
    var searxngBaseUrlValid = Uri.TryCreate(searxngBaseUrl, UriKind.Absolute, out var searxngBaseUri);
    var searxngReachable = searxngBaseUrlValid &&
                           searxngBaseUri is not null &&
                           await ProbeSearchEndpointAsync(searxngBaseUri, ct);

    string searxngStatus;
    string searxngMessage;
    if (mode is not ("auto" or "searxng"))
    {
        searxngStatus = "Skipped";
        searxngMessage = $"webSearch.mode '{mode}' does not use SearxNG.";
    }
    else if (!searxngBaseUrlValid || searxngBaseUri is null)
    {
        searxngStatus = "Invalid URL";
        searxngMessage = $"Invalid SearxNG base URL: {currentSettings.WebSearch.SearxngBaseUrl}";
    }
    else if (searxngReachable)
    {
        searxngStatus = "Ready";
        searxngMessage = $"SearxNG responded at {searxngBaseUrl}.";
    }
    else if (!currentSettings.WebSearch.SearxngAutoStart)
    {
        searxngStatus = "Disabled";
        searxngMessage = "SearxNG auto-start is disabled and the local endpoint is not reachable.";
    }
    else
    {
        searxngStatus = searxngLastLaunchStatus;
        searxngMessage = searxngLastLaunchMessage;
    }

    var searchApiConfigured = !string.IsNullOrWhiteSpace(currentSettings.WebSearch.SearchApiKey);
    var searchApiStatus = searchApiConfigured ? "Configured" : "Unconfigured";
    var searchApiMessage = searchApiConfigured
        ? "Hosted Search API fallback is configured."
        : "No hosted Search API key is configured.";

    var mcpServerPath = ResolveCurrentMcpServerPath(currentSettings);
    var mcpToolsEnabled = options.EnableTools;
    var mcpToolsAvailable = false;
    var mcpStatus = mcpToolsEnabled ? "Unavailable" : "Disabled";
    var mcpMessage = mcpToolsEnabled
        ? mcp.Message
        : "Runtime started without --tools, so MCP-backed web search is unavailable.";

    if (mcpToolsEnabled && toolsAvailable)
    {
        try
        {
            var toolList = await mcp.Client.ListToolsAsync(ct);
            mcpToolsAvailable = toolList.Count > 0;
            mcpStatus = mcpToolsAvailable ? "Ready" : "Degraded";
            mcpMessage = mcpToolsAvailable
                ? $"MCP responded with {toolList.Count} tool(s)."
                : "MCP responded but reported no tools.";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            mcpStatus = "Unreachable";
            mcpMessage = ex.Message;
        }
    }

    var effectiveStatus = "Ready";
    var effectiveMessage = "Web search is ready.";

    if (mode == "manual")
    {
        effectiveStatus = "Disabled";
        effectiveMessage = "webSearch.mode is manual, so live web search is turned off.";
    }
    else if (webPermission == "off")
    {
        effectiveStatus = "Disabled";
        effectiveMessage = "MCP web permission is off, so web search calls are blocked.";
    }
    else if (!mcpToolsEnabled)
    {
        effectiveStatus = "Disabled";
        effectiveMessage = "Runtime started without --tools, so MCP-backed web search is unavailable.";
    }
    else if (!mcpToolsAvailable)
    {
        effectiveStatus = "Unavailable";
        effectiveMessage = mcpMessage;
    }
    else
    {
        (effectiveStatus, effectiveMessage) = mode switch
        {
            "auto" when searxngReachable => (
                "Ready",
                "Auto mode is ready. SearxNG is reachable, with hosted Search API and Google News still available as fallback."),
            "auto" when searchApiConfigured => (
                "Ready",
                "Auto mode is running in fallback mode. SearxNG is unavailable, so hosted Search API or Google News will be used."),
            "auto" => (
                "Ready",
                "Auto mode is running in fallback mode. SearxNG is unavailable and hosted Search API is not configured, so Google News fallback is all that remains."),
            "searxng" when searxngReachable => (
                "Ready",
                "SearxNG-only mode is ready."),
            "searxng" => (
                "Unavailable",
                "SearxNG-only mode is configured, but the endpoint is not reachable."),
            "search_api" when searchApiConfigured => (
                "Ready",
                "Hosted Search API mode is configured."),
            "search_api" => (
                "Unavailable",
                "Hosted Search API mode is selected, but no API key is configured."),
            "google_news" => (
                "Ready",
                "Google News-only mode is ready."),
            "ddg_html" => (
                "Degraded",
                "DDG HTML mode is legacy and may be blocked upstream."),
            _ => (
                "Ready",
                "Web search is ready.")
        };
    }

    var lastProviderTrace = await BuildLastProviderTraceAsync(ct);

    return new SearchStatusResponse(
        LiveSearchAvailable: string.Equals(effectiveStatus, "Ready", StringComparison.OrdinalIgnoreCase),
        EffectiveStatus: effectiveStatus,
        EffectiveMessage: effectiveMessage,
        SearchMode: mode,
        WebPermission: webPermission,
        Searxng: new SearchProviderStatusDto(
            Status: searxngStatus,
            Message: searxngMessage,
            BaseUrl: searxngBaseUrl,
            Reachable: searxngReachable,
            ManagedByRuntime: searxngLauncher.IsManagedSearxngRunning,
            AutoStartEnabled: currentSettings.WebSearch.SearxngAutoStart,
            LastLaunchStatus: searxngLastLaunchStatus),
        SearchApi: new HostedSearchApiStatusDto(
            Status: searchApiStatus,
            Message: searchApiMessage,
            Provider: currentSettings.WebSearch.SearchApiProvider,
            BaseUrl: currentSettings.WebSearch.SearchApiBaseUrl,
            Engine: currentSettings.WebSearch.SearchApiEngine,
            Configured: searchApiConfigured),
        Mcp: new McpRuntimeStatusDto(
            Status: mcpStatus,
            Message: mcpMessage,
            ServerPath: mcpServerPath,
            ToolsEnabled: mcpToolsEnabled,
            ToolsAvailable: mcpToolsAvailable),
        LastProviderTrace: lastProviderTrace,
        CheckedAtUtc: DateTimeOffset.UtcNow);
}

if (options.ServerMode)
{
    using var serverCancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        serverCancellation.Cancel();
    };

    Console.WriteLine($"Runtime API listening on http://127.0.0.1:{options.ServerPort}");
    await RuntimeApiServer.RunAsync(
        options.ServerPort,
        BuildOrchestrator,
        () => settings,
        updatedSettings =>
        {
            settings = updatedSettings;
            QueueManagedSearxngUpdate(settings);
        },
        BuildSearchStatusAsync,
        audit,
        apiPermissionGate,
        serverCancellation.Token);
    return;
}

PrintBanner(settings, toolsAvailable, handles, options.EnableTools ? mcp.Message : null);
PrintHelpHint();

var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

while (!cancellation.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"{handles.User}> ");
    Console.ResetColor();

    var line = Console.ReadLine();
    if (line is null)
        break;

    var input = line.Trim();
    if (input.Length == 0)
        continue;

    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    if (input.Equals("/help", StringComparison.OrdinalIgnoreCase))
    {
        PrintHelp();
        continue;
    }

    if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase))
    {
        orchestrator.ResetConversation();
        permissionGate?.ClearSessionGrants();
        Console.WriteLine("Session reset.");
        continue;
    }

    if (input.Equals("/tools", StringComparison.OrdinalIgnoreCase))
    {
        var count = await orchestrator.GetAvailableToolCountAsync(cancellation.Token);
        Console.WriteLine($"Available tools: {count}");
        continue;
    }

    if (input.Equals("/who", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("/whoami", StringComparison.OrdinalIgnoreCase))
    {
        PrintRuntimeIdentity(settings, toolsAvailable, handles, personalityStore);
        continue;
    }

    if (input.Equals("/w", StringComparison.OrdinalIgnoreCase))
    {
        PrintRuntimeIdentity(settings, toolsAvailable, handles, personalityStore);
        continue;
    }

    if (input.Equals("/quickstart", StringComparison.OrdinalIgnoreCase))
    {
        PrintQuickStart();
        continue;
    }

    if (input.Equals("/status", StringComparison.OrdinalIgnoreCase))
    {
        await PrintStatusAsync(settings, options, orchestrator, toolsAvailable, mcp.Message, cancellation.Token);
        continue;
    }

    if (input.Equals("/doctor", StringComparison.OrdinalIgnoreCase))
    {
        await RunDoctorAsync(settings, options, orchestrator, toolsAvailable, mcp.Message, cancellation.Token);
        continue;
    }

    if (input.Equals("/undo", StringComparison.OrdinalIgnoreCase))
    {
        if (settingsUndoStack.Count == 0)
        {
            Console.WriteLine("Nothing to undo.");
            continue;
        }

        settings = settingsUndoStack.Pop();
        SettingsManager.Save(settings);
        handles = ResolvePromptHandles(settings, personalityStore, ResolvePreferredNameFromProfileStore(settings));
        orchestrator = BuildOrchestrator(settings);
        Console.WriteLine("Reverted last profile/settings change and reloaded chatbot.");
        continue;
    }

    if (input.Equals("/p", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/p ", StringComparison.OrdinalIgnoreCase))
    {
        input = "/profile" + input[2..];
    }

    if (input.Equals("/u", StringComparison.OrdinalIgnoreCase) ||
        input.StartsWith("/u ", StringComparison.OrdinalIgnoreCase))
    {
        input = "/profile user" + input[2..];
    }

    if (input.StartsWith("/profile", StringComparison.OrdinalIgnoreCase))
    {
        var before = settings;
        var handled = HandleProfileCommand(
            input,
            personalityStore,
            ref settings,
            ref handles,
            ref orchestrator,
            BuildOrchestrator);
        if (!ReferenceEquals(before, settings) && !Equals(before, settings))
            settingsUndoStack.Push(before);
        if (handled)
            continue;
    }

    try
    {
        var response = await orchestrator.ProcessAsync(input, cancellation.Token);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{handles.Assistant}> ");
        Console.ResetColor();
        Console.WriteLine(response.Text);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled.");
    }
    catch (HttpRequestException ex) when (LooksLikeLlmConnectivityFailure(ex, settings.Llm.BaseUrl))
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"{handles.Assistant}> ");
        Console.ResetColor();
        Console.WriteLine($"I can't reach the configured LLM endpoint right now ({settings.Llm.BaseUrl}).");
        Console.WriteLine("Start or reload your local model server, then try again.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
    }
}

static void PrintBanner(
    AppSettings settings,
    bool toolsAvailable,
    (string User, string Assistant) handles,
    string? toolsMessage)
{
    Console.WriteLine("  ____  _        _____ _               _     _                 ");
    Console.WriteLine(" / ___|(_)_ __  |_   _| |__   __ _  __| | __| | ___ _   _ ___ ");
    Console.WriteLine(" \\___ \\| | '__|   | | | '_ \\ / _` |/ _` |/ _` |/ _ \\ | | / __|");
    Console.WriteLine("  ___) | | |      | | | | | | (_| | (_| | (_| |  __/ |_| \\__ \\");
    Console.WriteLine(" |____/|_|_|      |_| |_| |_|\\__,_|\\__,_|\\__,_|\\___|\\__,_|___/");
    Console.WriteLine();
    Console.WriteLine($"Model: {settings.Llm.Model}");
    Console.WriteLine($"LLM:   {settings.Llm.BaseUrl}");
    Console.WriteLine($"Tools: {(toolsAvailable ? "enabled" : "disabled")}");
    if (!toolsAvailable && !string.IsNullOrWhiteSpace(toolsMessage))
    {
        Console.WriteLine($"Note:  {toolsMessage}");
    }

    Console.WriteLine($"Chat:  {handles.User} <-> {handles.Assistant}");
    Console.WriteLine();
}

static void PrintHelpHint()
{
    Console.WriteLine("Type a message, or /help for commands.");
}

static bool LooksLikeLlmConnectivityFailure(HttpRequestException ex, string baseUrl)
{
    var message = ex.Message ?? "";
    if (!Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var uri))
        return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase);

    return message.Contains(uri.Host, StringComparison.OrdinalIgnoreCase) &&
           (message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase));
}

static async Task PrintStatusAsync(
    AppSettings settings,
    HeadlessOptions options,
    AgentOrchestrator orchestrator,
    bool toolsAvailable,
    string toolsMessage,
    CancellationToken cancellationToken)
{
    var (llmReachable, llmDetail) = await CheckHttpEndpointReachableAsync(settings.Llm.BaseUrl, cancellationToken);
    var mcpPath = string.IsNullOrWhiteSpace(options.McpServerPath)
        ? RuntimePathResolver.ResolveMcpServerPath(settings.Mcp.ServerPath, Directory.GetCurrentDirectory())
        : Path.GetFullPath(options.McpServerPath.Trim());
    var mcpExists = File.Exists(mcpPath);
    var toolInfo = !options.EnableTools
        ? "disabled"
        : toolsAvailable
        ? await TryGetToolCountStatusAsync(orchestrator, cancellationToken)
        : $"unavailable ({toolsMessage})";

    Console.WriteLine("Status");
    Console.WriteLine($"LLM endpoint: {(llmReachable ? "reachable" : "unreachable")} ({settings.Llm.BaseUrl})");
    if (!string.IsNullOrWhiteSpace(llmDetail))
        Console.WriteLine($"  detail: {llmDetail}");
    Console.WriteLine($"MCP executable: {(mcpExists ? "found" : "missing")} ({mcpPath})");
    Console.WriteLine($"Tools: {toolInfo}");
    Console.WriteLine($"Active user profile: {NormalizeUserProfileId(settings.ActiveProfileId)}");
    Console.WriteLine($"Active personality: {NormalizePersonalityId(settings.ActivePersonalityId)}");
}

static async Task RunDoctorAsync(
    AppSettings settings,
    HeadlessOptions options,
    AgentOrchestrator orchestrator,
    bool toolsAvailable,
    string toolsMessage,
    CancellationToken cancellationToken)
{
    var findings = new List<string>();
    var recommendations = new List<string>();

    var (llmReachable, llmDetail) = await CheckHttpEndpointReachableAsync(settings.Llm.BaseUrl, cancellationToken);
    if (!llmReachable)
    {
        findings.Add($"LLM endpoint unreachable: {settings.Llm.BaseUrl} ({llmDetail})");
        recommendations.Add("Start or reload your local model server, or update llm.baseUrl.");
    }

    var mcpPath = string.IsNullOrWhiteSpace(options.McpServerPath)
        ? RuntimePathResolver.ResolveMcpServerPath(settings.Mcp.ServerPath, Directory.GetCurrentDirectory())
        : Path.GetFullPath(options.McpServerPath.Trim());
    if (options.EnableTools && !toolsAvailable)
    {
        findings.Add($"Tool startup degraded: {toolsMessage}");
        recommendations.Add("Check MCP server launch path and startup health, then restart the runtime.");
    }

    if (options.EnableTools && !File.Exists(mcpPath))
    {
        findings.Add($"MCP executable missing: {mcpPath}");
        recommendations.Add("Run ./dev/terminal.ps1 without --NoBuild to rebuild MCP artifacts.");
    }

    var toolStatus = toolsAvailable
        ? await TryGetToolCountStatusAsync(orchestrator, cancellationToken)
        : "disabled";
    if (toolsAvailable && toolStatus.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
    {
        findings.Add($"Tool handshake issue: {toolStatus}");
        recommendations.Add("Check MCP server launch path and permissions, then restart terminal runtime.");
    }

    var profilesDir = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
    var activePersonalityId = NormalizePersonalityId(settings.ActivePersonalityId);
    var profilePath = Path.Combine(profilesDir, $"{activePersonalityId}.json");
    if (!File.Exists(profilePath))
    {
        findings.Add($"Active personality file not found: {profilePath}");
        recommendations.Add("Switch to a valid personality: /profile thaddeus list then /profile thaddeus load <id>.");
    }

    if (findings.Count == 0)
    {
        Console.WriteLine("Doctor: no issues found.");
        return;
    }

    Console.WriteLine("Doctor Findings");
    foreach (var finding in findings)
        Console.WriteLine($"- {finding}");

    if (recommendations.Count > 0)
    {
        Console.WriteLine("Recommended Fixes");
        foreach (var recommendation in recommendations.Distinct(StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"- {recommendation}");
    }
}

static async Task<(bool Reachable, string Detail)> CheckHttpEndpointReachableAsync(
    string baseUrl,
    CancellationToken cancellationToken)
{
    var trimmed = (baseUrl ?? "").Trim();
    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        return (false, "invalid URL");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    var probe = new Uri(baseUri, "/v1/models");

    try
    {
        using var response = await http.GetAsync(probe, cancellationToken);
        return (true, $"{(int)response.StatusCode} {response.StatusCode}");
    }
    catch (Exception ex)
    {
        return (false, ex.Message);
    }
}

static async Task<string> TryGetToolCountStatusAsync(
    AgentOrchestrator orchestrator,
    CancellationToken cancellationToken)
{
    try
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var count = await orchestrator.GetAvailableToolCountAsync(timeout.Token);
        return $"enabled ({count} tools)";
    }
    catch (Exception ex)
    {
        return $"error: {ex.Message}";
    }
}

static async Task EnsureManagedSearxngAsync(
    AppSettings settings,
    bool toolsEnabled,
    SearxngHostLauncher launcher,
    IAuditLogger audit,
    Action<string, string> recordLaunchStatus,
    CancellationToken cancellationToken)
{
    if (!toolsEnabled)
    {
        launcher.StopManagedSearxng();
        recordLaunchStatus("Disabled", "Runtime started without --tools, so SearxNG auto-start was skipped.");
        return;
    }

    SearxngLaunchResult result;
    try
    {
        result = await launcher.EnsureRunningAsync(settings.WebSearch, cancellationToken);
    }
    catch (Exception ex)
    {
        audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "SEARXNG_AUTOSTART",
            Target = settings.WebSearch.SearxngBaseUrl,
            Result = "error",
            Details = new Dictionary<string, object>
            {
                ["status"] = "exception",
                ["message"] = ex.Message
            }
        });
        recordLaunchStatus("Exception", ex.Message);
        return;
    }

    var mode = (settings.WebSearch.Mode ?? "auto").Trim().ToLowerInvariant();
    var resultLabel = result.Status.ToString();
    var auditResult = result.Status switch
    {
        SearxngLaunchStatus.Started => "ok",
        SearxngLaunchStatus.AlreadyRunning => "ok",
        SearxngLaunchStatus.NotRequired => "skipped",
        SearxngLaunchStatus.Disabled => "skipped",
        _ => "error"
    };

    audit.Append(new AuditEvent
    {
        Actor = "runtime",
        Action = "SEARXNG_AUTOSTART",
        Target = result.BaseUrl ?? settings.WebSearch.SearxngBaseUrl,
        Result = auditResult,
        Details = new Dictionary<string, object>
        {
            ["status"] = resultLabel,
            ["mode"] = mode,
            ["message"] = result.Message
        }
    });

    recordLaunchStatus(resultLabel, result.Message);

    if (result.Status is SearxngLaunchStatus.Started or SearxngLaunchStatus.AlreadyRunning)
    {
        Console.WriteLine($"SearxNG: {result.Message}");
        return;
    }

    if (mode == "searxng")
    {
        Console.WriteLine($"SearxNG: {result.Message}");
        Console.WriteLine("SearxNG-only mode is configured; web search will fail until SearxNG is reachable.");
    }
}

static async Task<bool> ProbeSearchEndpointAsync(Uri baseUri, CancellationToken cancellationToken)
{
    using var http = new HttpClient
    {
        BaseAddress = baseUri,
        Timeout = TimeSpan.FromSeconds(2)
    };

    try
    {
        using var response = await http.GetAsync("/search?q=thaddeus&format=json", cancellationToken);
        if (response.IsSuccessStatusCode)
            return true;
    }
    catch
    {
        // Best effort probe only.
    }

    try
    {
        using var response = await http.GetAsync("/", cancellationToken);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

static string NormalizeBaseUrl(string? value, string fallback)
{
    var raw = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    return raw.TrimEnd('/');
}

static string NormalizeWebSearchMode(string? value)
{
    var normalized = (value ?? "auto").Trim().ToLowerInvariant();
    return normalized switch
    {
        "auto" => "auto",
        "searxng" => "searxng",
        "search_api" => "search_api",
        "api" => "search_api",
        "google_news" => "google_news",
        "ddg_html" => "ddg_html",
        "manual" => "manual",
        _ => "auto"
    };
}

static string ReadAuditDetail(AuditEvent auditEvent, string key)
{
    if (auditEvent.Details is null ||
        !auditEvent.Details.TryGetValue(key, out var value) ||
        value is null)
    {
        return "";
    }

    return value switch
    {
        string text => text,
        JsonElement jsonElement => jsonElement.ValueKind switch
        {
            JsonValueKind.String => jsonElement.GetString() ?? "",
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number => jsonElement.ToString(),
            _ => jsonElement.ToString()
        },
        _ => value.ToString() ?? ""
    };
}

static void PrintHelp()
{
    Console.WriteLine("Headless Runtime Commands");
    Console.WriteLine("  /help   Show help");
    Console.WriteLine("  /reset  Clear conversation state");
    Console.WriteLine("  /tools  Show detected MCP tool count");
    Console.WriteLine("  /who    Show active user/assistant handles");
    Console.WriteLine("  /whoami Show active user/assistant identities");
    Console.WriteLine("  /quickstart Show common commands");
    Console.WriteLine("  /status Show runtime health");
    Console.WriteLine("  /doctor Run diagnostics with suggested fixes");
    Console.WriteLine("  /undo   Revert last profile/settings change");
    Console.WriteLine("  /profile ...   Manage user and personality profiles");
    Console.WriteLine("  /p ...  Alias for /profile ...");
    Console.WriteLine("  /u ...  Alias for /profile user ...");
    Console.WriteLine("  /w      Alias for /whoami");
    Console.WriteLine("  /exit   Quit");
    Console.WriteLine();
    Console.WriteLine("CLI options");
    Console.WriteLine("  --tools                 Enable MCP tool calls");
    Console.WriteLine("  --mcp-server <path>     MCP server executable path");
    Console.WriteLine("  --server                Run HTTP runtime API host");
    Console.WriteLine("  --port <number>         HTTP API port (default: 5378)");
    Console.WriteLine("  --help                  Show this help");
}

static void PrintQuickStart()
{
    Console.WriteLine("Quickstart");
    Console.WriteLine("  /whoami");
    Console.WriteLine("  /status");
    Console.WriteLine("  /doctor");
    Console.WriteLine("  /profile user show");
    Console.WriteLine("  /profile user set-name <name>");
    Console.WriteLine("  /profile user set-alias <alias>");
    Console.WriteLine("  /profile thaddeus list");
    Console.WriteLine("  /profile thaddeus load <personality-id>");
    Console.WriteLine("  /profile thaddeus set-alias <alias>");
    Console.WriteLine("  /profile export <file-path>");
    Console.WriteLine("  /profile import <file-path>");
    Console.WriteLine("  /undo");
}

static bool HandleProfileCommand(
    string input,
    PersonalityProfileStore personalityStore,
    ref AppSettings settings,
    ref (string User, string Assistant) handles,
    ref AgentOrchestrator orchestrator,
    Func<AppSettings, AgentOrchestrator> buildOrchestrator)
{
    var parts = SplitCommand(input);
    if (parts.Count == 1 || parts[1].Equals("help", StringComparison.OrdinalIgnoreCase))
    {
        PrintProfileHelp();
        return true;
    }

    if (parts.Count < 3)
    {
        PrintProfileHelp();
        return true;
    }

    if (parts[1].Equals("export", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 3)
        {
            Console.WriteLine("Usage: /profile export <file-path>");
            return true;
        }

        var path = JoinTail(parts, 2);
        ExportProfiles(path, settings, personalityStore);
        return true;
    }

    if (parts[1].Equals("import", StringComparison.OrdinalIgnoreCase))
    {
        if (parts.Count < 3)
        {
            Console.WriteLine("Usage: /profile import <file-path>");
            return true;
        }

        var path = JoinTail(parts, 2);
        var changed = ImportProfiles(path, personalityStore, ref settings);
        if (changed)
        {
            SettingsManager.Save(settings);
            handles = ResolvePromptHandles(settings, personalityStore, ResolvePreferredNameFromProfileStore(settings));
            orchestrator = buildOrchestrator(settings);
            Console.WriteLine("Chatbot reloaded (profile import).");
        }
        return true;
    }

    var scope = parts[1].ToLowerInvariant();
    var action = parts[2].ToLowerInvariant();

    if (scope == "user")
    {
        var changed = HandleUserProfileAction(parts, ref settings);
        if (changed)
        {
            SettingsManager.Save(settings);
            handles = ResolvePromptHandles(settings, personalityStore, ResolvePreferredNameFromProfileStore(settings));
            orchestrator = buildOrchestrator(settings);
            Console.WriteLine("Chatbot reloaded (user profile updated).");
        }
        return true;
    }

    if (scope == "thaddeus" || scope == "assistant" || scope == "personality")
    {
        var changed = HandlePersonalityAction(parts, personalityStore, ref settings);
        if (changed)
        {
            SettingsManager.Save(settings);
            handles = ResolvePromptHandles(settings, personalityStore, ResolvePreferredNameFromProfileStore(settings));
            orchestrator = buildOrchestrator(settings);
            Console.WriteLine("Chatbot reloaded (personality profile updated).");
        }
        return true;
    }

    Console.WriteLine("Unknown profile scope. Use 'user' or 'thaddeus'.");
    return true;
}

static bool HandleUserProfileAction(IReadOnlyList<string> parts, ref AppSettings settings)
{
    var action = parts[2].ToLowerInvariant();
    switch (action)
    {
        case "show":
            PrintUserProfile(settings);
            return false;
        case "list":
            ListUserProfiles(settings);
            return false;
        case "set-name":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user set-name <display name>");
                return false;
            }
            var displayName = JoinTail(parts, 3);
            settings = settings with
            {
                UserProfile = settings.UserProfile with
                {
                    DisplayName = displayName
                }
            };
            Console.WriteLine("Updated user display name.");
            return true;
        case "set-alias":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user set-alias <alias>");
                return false;
            }
            {
                var key = NormalizeUserProfileId(settings.ActiveProfileId);
                var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
                aliases[key] = JoinTail(parts, 3).Trim();
                settings = settings with
                {
                    UserProfile = settings.UserProfile with
                    {
                        AliasesByProfile = aliases
                    }
                };
                Console.WriteLine($"Updated alias for '{key}'.");
                return true;
            }
        case "set-about":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user set-about <about text>");
                return false;
            }
            var aboutMe = JoinTail(parts, 3);
            settings = settings with
            {
                UserProfile = settings.UserProfile with
                {
                    AboutMe = aboutMe
                }
            };
            Console.WriteLine("Updated user about text.");
            return true;
        case "set-location":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user set-location <city/state/etc>");
                return false;
            }
            settings = SetProfileLocation(settings, settings.ActiveProfileId, JoinTail(parts, 3));
            Console.WriteLine("Updated active profile location.");
            return true;
        case "clear-location":
            settings = ClearProfileLocation(settings, settings.ActiveProfileId);
            Console.WriteLine("Cleared active profile location.");
            return true;
        case "create":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user create <profile-id>");
                return false;
            }
            {
                var id = NormalizeUserProfileId(parts[3]);
                var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
                var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
                if (map.ContainsKey(id))
                {
                    Console.WriteLine($"User profile '{id}' already exists.");
                    return false;
                }

                map[id] = settings.GetEffectiveUserLocation(settings.ActiveProfileId);
                aliases[id] = id;
                settings = settings with
                {
                    UserProfile = settings.UserProfile with
                    {
                        LocationsByProfile = map,
                        AliasesByProfile = aliases
                    },
                    ActiveProfileId = id
                };
                Console.WriteLine($"Created and loaded user profile '{id}'.");
                return true;
            }
        case "load":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user load <profile-id>");
                return false;
            }
            {
                var id = NormalizeUserProfileId(parts[3]);
                var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
                if (!map.ContainsKey(id))
                {
                    Console.WriteLine($"User profile '{id}' was not found.");
                    return false;
                }

                settings = settings with { ActiveProfileId = id };
                Console.WriteLine($"Loaded user profile '{id}'.");
                return true;
            }
        case "delete":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile user delete <profile-id> --yes");
                return false;
            }
            {
                var id = NormalizeUserProfileId(parts[3]);
                var confirmed = parts.Any(p => p.Equals("--yes", StringComparison.OrdinalIgnoreCase));
                if (!confirmed)
                {
                    Console.WriteLine("Refusing delete without confirmation. Re-run with --yes.");
                    return false;
                }
                if (string.Equals(id, AppSettings.DefaultLocationProfileKey, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Cannot delete the default user profile slot.");
                    return false;
                }

                var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
                var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
                if (!map.Remove(id))
                {
                    Console.WriteLine($"User profile '{id}' was not found.");
                    return false;
                }
                aliases.Remove(id);

                var nextActive = string.Equals(settings.ActiveProfileId, id, StringComparison.OrdinalIgnoreCase)
                    ? AppSettings.DefaultLocationProfileKey
                    : settings.ActiveProfileId;

                settings = settings with
                {
                    UserProfile = settings.UserProfile with
                    {
                        LocationsByProfile = map,
                        AliasesByProfile = aliases
                    },
                    ActiveProfileId = nextActive
                };
                Console.WriteLine($"Deleted user profile '{id}'.");
                return true;
            }
        default:
            Console.WriteLine($"Unknown user profile action '{action}'.");
            PrintProfileHelp();
            return false;
    }
}

static bool HandlePersonalityAction(
    IReadOnlyList<string> parts,
    PersonalityProfileStore personalityStore,
    ref AppSettings settings)
{
    var action = parts[2].ToLowerInvariant();
    var directory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
    personalityStore.EnsureBuiltInsInstalled(directory);

    switch (action)
    {
        case "list":
            var all = personalityStore.ListProfiles(directory)
                .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var profile in all)
            {
                var marker = string.Equals(profile.Id, settings.ActivePersonalityId, StringComparison.OrdinalIgnoreCase)
                    ? "*"
                    : " ";
                var alias = string.IsNullOrWhiteSpace(profile.Alias)
                    ? ToTerminalHandle(profile.DisplayName, "sir-thaddeus")
                    : profile.Alias.Trim();
                Console.WriteLine($"{marker} {profile.Id} ({profile.DisplayName}) alias={alias}");
            }
            return false;
        case "load":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile thaddeus load <personality-id>");
                return false;
            }
            {
                var id = NormalizePersonalityId(parts[3]);
                var available = personalityStore.ListProfiles(directory);
                if (!available.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Personality '{id}' was not found.");
                    return false;
                }

                settings = settings with
                {
                    ActivePersonalityId = id,
                    PersonalityProfilesDir = directory
                };
                Console.WriteLine($"Loaded personality '{id}'.");
                return true;
            }
        case "create":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile thaddeus create <personality-id>");
                return false;
            }
            {
                var id = NormalizePersonalityId(parts[3]);
                var path = personalityStore.ResolveProfilePath(directory, id);
                if (File.Exists(path))
                {
                    Console.WriteLine($"Personality '{id}' already exists.");
                    return false;
                }

                var template = PersonalityProfileTemplateFactory.CreateAverageTemplate(id);
                var json = PersonalityProfileTemplateFactory.RenderMinimalTemplateJson(template);
                personalityStore.SaveProfileTemplate(directory, template, json);
                settings = settings with
                {
                    ActivePersonalityId = id,
                    PersonalityProfilesDir = directory
                };
                Console.WriteLine($"Created personality '{id}' from template.");
                return true;
            }
        case "delete":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile thaddeus delete <personality-id> --yes");
                return false;
            }
            {
                var id = NormalizePersonalityId(parts[3]);
                var confirmed = parts.Any(p => p.Equals("--yes", StringComparison.OrdinalIgnoreCase));
                if (!confirmed)
                {
                    Console.WriteLine("Refusing delete without confirmation. Re-run with --yes.");
                    return false;
                }
                if (string.Equals(id, BuiltInProfileCatalog.HelpfulDefaultId, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("Cannot delete the fallback default personality.");
                    return false;
                }

                var path = personalityStore.ResolveProfilePath(directory, id);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"Personality '{id}' was not found.");
                    return false;
                }

                File.Delete(path);

                if (string.Equals(settings.ActivePersonalityId, id, StringComparison.OrdinalIgnoreCase))
                {
                    settings = settings with
                    {
                        ActivePersonalityId = BuiltInProfileCatalog.HelpfulDefaultId,
                        PersonalityProfilesDir = directory
                    };
                    Console.WriteLine($"Deleted '{id}'. Switched to '{BuiltInProfileCatalog.HelpfulDefaultId}'.");
                    return true;
                }

                Console.WriteLine($"Deleted personality '{id}'.");
                return false;
            }
        case "set-alias":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile thaddeus set-alias <alias>");
                Console.WriteLine("   or: /profile thaddeus set-alias <personality-id> <alias>");
                return false;
            }
            {
                string targetId;
                string alias;
                if (parts.Count == 4)
                {
                    targetId = NormalizePersonalityId(settings.ActivePersonalityId);
                    alias = parts[3];
                }
                else
                {
                    targetId = NormalizePersonalityId(parts[3]);
                    alias = JoinTail(parts, 4);
                }

                return SetPersonalityAlias(targetId, alias, personalityStore, directory, ref settings);
            }
        case "edit":
            if (parts.Count < 4)
            {
                Console.WriteLine("Usage: /profile thaddeus edit <personality-id>");
                return false;
            }
            return EditPersonality(parts[3], personalityStore, directory, ref settings);
        default:
            Console.WriteLine($"Unknown personality action '{action}'.");
            PrintProfileHelp();
            return false;
    }
}

static bool EditPersonality(
    string rawId,
    PersonalityProfileStore personalityStore,
    string directory,
    ref AppSettings settings)
{
    var id = NormalizePersonalityId(rawId);
    var path = personalityStore.ResolveProfilePath(directory, id);
    if (!File.Exists(path))
    {
        Console.WriteLine($"Personality '{id}' was not found.");
        return false;
    }

    if (!TryReadPersonalityProfile(path, out var profile, out var error))
    {
        Console.WriteLine($"Could not load personality '{id}': {error}");
        return false;
    }

    Console.WriteLine($"Editing '{id}'. Press Enter to keep current values.");
    Console.Write($"display name [{profile.DisplayName}]: ");
    var displayName = Console.ReadLine();
    Console.Write($"alias [{profile.Alias}]: ");
    var alias = Console.ReadLine();
    Console.Write($"self name [{profile.Identity.SelfName}]: ");
    var selfName = Console.ReadLine();
    Console.Write($"core identity [{profile.Instructions.CoreIdentity}]: ");
    var coreIdentity = Console.ReadLine();

    var nextDisplayName = string.IsNullOrWhiteSpace(displayName) ? profile.DisplayName : displayName.Trim();
    var nextAlias = string.IsNullOrWhiteSpace(alias) ? profile.Alias : alias.Trim();
    var nextSelfName = string.IsNullOrWhiteSpace(selfName) ? profile.Identity.SelfName : selfName.Trim();
    var nextCoreIdentity = string.IsNullOrWhiteSpace(coreIdentity) ? profile.Instructions.CoreIdentity : coreIdentity.Trim();

    var updated = profile with
    {
        DisplayName = nextDisplayName,
        Alias = nextAlias,
        Description = nextCoreIdentity,
        Identity = profile.Identity with
        {
            SelfName = nextSelfName,
            SelfDescription = nextCoreIdentity
        },
        Instructions = profile.Instructions with
        {
            CoreIdentity = nextCoreIdentity
        }
    };

    personalityStore.SaveProfile(directory, updated);
    settings = settings with
    {
        ActivePersonalityId = id,
        PersonalityProfilesDir = directory
    };
    Console.WriteLine($"Saved personality '{id}'.");
    return true;
}

static bool TryReadPersonalityProfile(string path, out PersonalityProfile profile, out string error)
{
    profile = new PersonalityProfile();
    error = "";
    try
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var validator = new PersonalityProfileValidator();
        var validation = validator.ValidateJson(doc.RootElement);
        if (!validation.IsValid)
        {
            error = $"{validation.ReasonCode}: {validation.Detail}";
            return false;
        }

        profile = PersonalityProfileProjection.FromJson(doc.RootElement);
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static bool SetPersonalityAlias(
    string personalityId,
    string alias,
    PersonalityProfileStore personalityStore,
    string directory,
    ref AppSettings settings)
{
    var id = NormalizePersonalityId(personalityId);
    var path = personalityStore.ResolveProfilePath(directory, id);
    if (!File.Exists(path))
    {
        Console.WriteLine($"Personality '{id}' was not found.");
        return false;
    }

    if (!TryReadPersonalityProfile(path, out var profile, out var error))
    {
        Console.WriteLine($"Could not load personality '{id}': {error}");
        return false;
    }

    var normalizedAlias = alias.Trim();
    if (string.IsNullOrWhiteSpace(normalizedAlias))
    {
        Console.WriteLine("Alias cannot be empty.");
        return false;
    }

    personalityStore.SaveProfile(directory, profile with { Alias = normalizedAlias });
    settings = settings with
    {
        ActivePersonalityId = id,
        PersonalityProfilesDir = directory
    };

    Console.WriteLine($"Updated alias for personality '{id}' to '{normalizedAlias}'.");
    return true;
}

static void ExportProfiles(string outputPath, AppSettings settings, PersonalityProfileStore personalityStore)
{
    try
    {
        var path = outputPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Export path is required.");
            return;
        }

        var full = Path.GetFullPath(path);
        var directory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
        personalityStore.EnsureBuiltInsInstalled(directory);
        var personalities = personalityStore.ListProfiles(directory)
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => new
            {
                id = p.Id,
                alias = p.Alias,
                json = File.ReadAllText(p.SourcePath),
                sourcePath = p.SourcePath
            })
            .ToList();

        var payload = new
        {
            version = 1,
            exportedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
            settings = new
            {
                activeProfileId = settings.ActiveProfileId,
                activePersonalityId = settings.ActivePersonalityId,
                userProfile = settings.UserProfile
            },
            personalities
        };

        var parent = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        File.WriteAllText(full, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

        Console.WriteLine($"Exported profiles to '{full}'.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Profile export failed: {ex.Message}");
    }
}

static bool ImportProfiles(string inputPath, PersonalityProfileStore personalityStore, ref AppSettings settings)
{
    try
    {
        var path = inputPath.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            Console.WriteLine("Import path is required.");
            return false;
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full))
        {
            Console.WriteLine($"Import file not found: {full}");
            return false;
        }

        var json = File.ReadAllText(full);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var changed = false;
        if (root.TryGetProperty("settings", out var settingsNode))
        {
            if (settingsNode.TryGetProperty("userProfile", out var userProfileNode))
            {
                var importedUserProfile = userProfileNode.Deserialize<UserProfileSettings>();
                if (importedUserProfile is not null)
                {
                    settings = settings with { UserProfile = importedUserProfile };
                    changed = true;
                }
            }

            if (settingsNode.TryGetProperty("activeProfileId", out var activeProfileNode) &&
                activeProfileNode.ValueKind == JsonValueKind.String)
            {
                settings = settings with { ActiveProfileId = activeProfileNode.GetString() };
                changed = true;
            }

            if (settingsNode.TryGetProperty("activePersonalityId", out var activePersonalityNode) &&
                activePersonalityNode.ValueKind == JsonValueKind.String)
            {
                settings = settings with { ActivePersonalityId = activePersonalityNode.GetString() ?? settings.ActivePersonalityId };
                changed = true;
            }
        }

        var profilesDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
        personalityStore.EnsureBuiltInsInstalled(profilesDirectory);
        var validator = new PersonalityProfileValidator();
        var importedCount = 0;
        var skippedCount = 0;

        if (root.TryGetProperty("personalities", out var personalitiesNode) &&
            personalitiesNode.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in personalitiesNode.EnumerateArray())
            {
                if (!item.TryGetProperty("json", out var jsonNode) || jsonNode.ValueKind != JsonValueKind.String)
                {
                    skippedCount++;
                    continue;
                }

                var profileJson = jsonNode.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(profileJson))
                {
                    skippedCount++;
                    continue;
                }

                try
                {
                    using var profileDoc = JsonDocument.Parse(profileJson, new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                    var validation = validator.ValidateJson(profileDoc.RootElement);
                    if (!validation.IsValid)
                    {
                        skippedCount++;
                        continue;
                    }

                    var profile = PersonalityProfileProjection.FromJson(profileDoc.RootElement);
                    personalityStore.SaveProfileTemplate(profilesDirectory, profile, profileJson);
                    importedCount++;
                    changed = true;
                }
                catch
                {
                    skippedCount++;
                }
            }
        }

        settings = settings with { PersonalityProfilesDir = profilesDirectory };
        Console.WriteLine($"Imported profiles from '{full}'. Imported={importedCount}, Skipped={skippedCount}.");
        return changed;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Profile import failed: {ex.Message}");
        return false;
    }
}

static AppSettings SetProfileLocation(AppSettings settings, string? profileId, string locationValue)
{
    var key = NormalizeUserProfileId(profileId);
    var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
    var now = DateTimeOffset.UtcNow.ToString("O");
    var normalized = locationValue.Trim();
    var location = new LocationSettings
    {
        Mode = "manual",
        Value = normalized,
        UpdatedAt = now,
        Enabled = true,
        Label = normalized,
        Timezone = ""
    };
    map[key] = location;

    return settings with
    {
        Location = location,
        UserProfile = settings.UserProfile with
        {
            Location = location,
            LocationsByProfile = map
        }
    };
}

static AppSettings ClearProfileLocation(AppSettings settings, string? profileId)
{
    var key = NormalizeUserProfileId(profileId);
    var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
    var location = new LocationSettings
    {
        Mode = "unset",
        Value = "",
        UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
        Enabled = false,
        Label = "",
        Timezone = ""
    };
    map[key] = location;

    return settings with
    {
        Location = location,
        UserProfile = settings.UserProfile with
        {
            Location = location,
            LocationsByProfile = map
        }
    };
}

static Dictionary<string, LocationSettings> CopyProfileLocationMap(Dictionary<string, LocationSettings> source)
{
    var map = new Dictionary<string, LocationSettings>(source, StringComparer.OrdinalIgnoreCase);
    if (!map.ContainsKey(AppSettings.DefaultLocationProfileKey))
    {
        map[AppSettings.DefaultLocationProfileKey] = new LocationSettings
        {
            Mode = "unset",
            Value = "",
            UpdatedAt = "",
            Enabled = false,
            Label = "",
            Timezone = ""
        };
    }

    return map;
}

static Dictionary<string, string> CopyProfileAliasMap(Dictionary<string, string> source)
{
    var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in source)
    {
        var normalizedKey = NormalizeUserProfileId(key);
        var normalizedValue = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalizedValue))
            continue;
        map[normalizedKey] = normalizedValue;
    }

    return map;
}

static string NormalizeUserProfileId(string? profileId)
{
    var normalized = (profileId ?? "").Trim();
    return string.IsNullOrWhiteSpace(normalized)
        ? AppSettings.DefaultLocationProfileKey
        : normalized;
}

static string NormalizePersonalityId(string profileId)
    => (profileId ?? "").Trim().ToLowerInvariant();

static string JoinTail(IReadOnlyList<string> parts, int startIndex)
{
    if (parts.Count <= startIndex)
        return "";
    return string.Join(' ', parts.Skip(startIndex));
}

static List<string> SplitCommand(string input)
{
    var result = new List<string>();
    if (string.IsNullOrWhiteSpace(input))
        return result;

    var span = input.AsSpan().Trim();
    var i = 0;
    while (i < span.Length)
    {
        while (i < span.Length && char.IsWhiteSpace(span[i]))
            i++;
        if (i >= span.Length)
            break;

        if (span[i] == '"')
        {
            i++;
            var start = i;
            while (i < span.Length && span[i] != '"')
                i++;
            result.Add(span[start..i].ToString());
            if (i < span.Length && span[i] == '"')
                i++;
            continue;
        }

        var tokenStart = i;
        while (i < span.Length && !char.IsWhiteSpace(span[i]))
            i++;
        result.Add(span[tokenStart..i].ToString());
    }

    return result;
}

static void PrintProfileHelp()
{
    Console.WriteLine("Profile Commands");
    Console.WriteLine("  /profile help");
    Console.WriteLine("  /profile export <file-path>");
    Console.WriteLine("  /profile import <file-path>");
    Console.WriteLine("  /profile user show");
    Console.WriteLine("  /profile user list");
    Console.WriteLine("  /profile user set-name <text>");
    Console.WriteLine("  /profile user set-alias <alias>");
    Console.WriteLine("  /profile user set-about <text>");
    Console.WriteLine("  /profile user set-location <text>");
    Console.WriteLine("  /profile user clear-location");
    Console.WriteLine("  /profile user create <profile-id>");
    Console.WriteLine("  /profile user load <profile-id>");
    Console.WriteLine("  /profile user delete <profile-id> --yes");
    Console.WriteLine("  /profile thaddeus list");
    Console.WriteLine("  /profile thaddeus load <personality-id>");
    Console.WriteLine("  /profile thaddeus create <personality-id>");
    Console.WriteLine("  /profile thaddeus set-alias <alias>");
    Console.WriteLine("  /profile thaddeus set-alias <personality-id> <alias>");
    Console.WriteLine("  /profile thaddeus edit <personality-id>");
    Console.WriteLine("  /profile thaddeus delete <personality-id> --yes");
}

static void PrintUserProfile(AppSettings settings)
{
    var activeId = NormalizeUserProfileId(settings.ActiveProfileId);
    var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
    aliases.TryGetValue(activeId, out var alias);

    // Prefer dictionary alias, then explicit Alias on profile, then DisplayName handles
    var resolvedAlias = !string.IsNullOrWhiteSpace(alias) 
        ? alias 
        : (!string.IsNullOrWhiteSpace(settings.UserProfile.Alias) 
            ? settings.UserProfile.Alias 
            : ToTerminalHandle(settings.UserProfile.DisplayName, "user"));
    var effectiveLocation = settings.GetEffectiveUserLocation(settings.ActiveProfileId).GetResolvedLabel() ?? "(unset)";
    Console.WriteLine($"Active user profile id: {activeId}");
    Console.WriteLine($"Alias: {resolvedAlias}");
    Console.WriteLine($"Display name: {settings.UserProfile.DisplayName}");
    Console.WriteLine($"About: {settings.UserProfile.AboutMe}");
    Console.WriteLine($"Location: {effectiveLocation}");
}

static void ListUserProfiles(AppSettings settings)
{
    var map = CopyProfileLocationMap(settings.UserProfile.LocationsByProfile);
    var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
    foreach (var id in map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
    {
        var marker = string.Equals(id, settings.ActiveProfileId, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
        var loc = map[id].GetResolvedLabel();
        var locText = string.IsNullOrWhiteSpace(loc) ? "(no location)" : loc;
        var alias = aliases.TryGetValue(id, out var value) ? value : id;
        Console.WriteLine($"{marker} {id} ({alias}) - {locText}");
    }
}

static void PrintRuntimeIdentity(
    AppSettings settings,
    bool toolsAvailable,
    (string User, string Assistant) handles,
    PersonalityProfileStore personalityStore)
{
    var machineIdentity = OperatingSystem.IsWindows()
        ? (WindowsIdentity.GetCurrent()?.Name ?? Environment.UserName)
        : Environment.UserName;
    var activeUserId = NormalizeUserProfileId(settings.ActiveProfileId);
    var activePersonalityId = NormalizePersonalityId(settings.ActivePersonalityId);
    var personalityAlias = ResolveActivePersonalityAlias(settings, personalityStore);
    var userAlias = ResolveActiveUserAlias(settings);
    Console.WriteLine($"User:      {handles.User}");
    Console.WriteLine($"UserAlias: {userAlias}");
    Console.WriteLine($"UserId:    {activeUserId}");
    Console.WriteLine($"WhoAmI:    {machineIdentity}");
    Console.WriteLine($"Assistant: {handles.Assistant}");
    Console.WriteLine($"Persona:   {activePersonalityId} (alias: {personalityAlias})");
    Console.WriteLine($"Model:     {settings.Llm.Model}");
    Console.WriteLine($"LLM:       {settings.Llm.BaseUrl}");
    Console.WriteLine($"Tools:     {(toolsAvailable ? "enabled" : "disabled")}");
}

static (string User, string Assistant) ResolvePromptHandles(
    AppSettings settings,
    PersonalityProfileStore personalityStore,
    string? profilePreferredName = null)
{
    var userAlias = ResolveActiveUserAlias(settings, profilePreferredName);
    var assistantAlias = ResolveActivePersonalityAlias(settings, personalityStore);
    var user = ToTerminalHandle(userAlias, fallback: "user");
    var assistant = ToTerminalHandle(assistantAlias, fallback: "sir-thaddeus");
    return (user, assistant);
}

static string ResolveActiveUserAlias(AppSettings settings, string? profilePreferredName = null)
{
    var activeId = NormalizeUserProfileId(settings.ActiveProfileId);
    var aliases = CopyProfileAliasMap(settings.UserProfile.AliasesByProfile);
    if (aliases.TryGetValue(activeId, out var alias) && !string.IsNullOrWhiteSpace(alias))
        return alias;

    var explicitAlias = (settings.UserProfile.Alias ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(explicitAlias))
        return explicitAlias;

    // Prefer preferred_name from the SQLite profile store (authoritative source
    // shared with the desktop UI) over the stale settings.json displayName.
    if (!string.IsNullOrWhiteSpace(profilePreferredName))
        return profilePreferredName;

    var displayName = (settings.UserProfile.DisplayName ?? "").Trim();
    if (!string.IsNullOrWhiteSpace(displayName))
        return displayName;

    if (!string.Equals(activeId, AppSettings.DefaultLocationProfileKey, StringComparison.OrdinalIgnoreCase))
        return activeId;

    return Environment.UserName;
}

/// <summary>
/// Reads the preferred_name from the active profile in the SQLite memory store.
/// This mirrors the desktop UI's logic in App.xaml.cs so the terminal shows
/// the same identity the user configured through the GUI.
/// </summary>
static string? ResolvePreferredNameFromProfileStore(AppSettings settings)
{
    if (!settings.Memory.Enabled)
        return null;

    var activeProfileId = (settings.ActiveProfileId ?? "").Trim();
    if (string.IsNullOrWhiteSpace(activeProfileId))
        return null;

    try
    {
        var dbPath = RuntimeMcpEnvironmentBuilder.ResolveMemoryDbPath(settings.Memory.DbPath);
        if (!File.Exists(dbPath))
            return null;

        using var store = new SqliteMemoryStore(dbPath);
        var profiles = store.ListProfilesAsync().GetAwaiter().GetResult();
        var active = profiles.FirstOrDefault(p =>
            string.Equals(p.ProfileId, activeProfileId, StringComparison.OrdinalIgnoreCase));

        if (active is null || string.IsNullOrWhiteSpace(active.ProfileJson))
            return active?.DisplayName;

        using var doc = JsonDocument.Parse(active.ProfileJson);
        if (doc.RootElement.TryGetProperty("preferred_name", out var nameEl) &&
            nameEl.ValueKind == JsonValueKind.String)
        {
            var preferred = nameEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(preferred))
                return preferred;
        }

        return active.DisplayName;
    }
    catch
    {
        return null;
    }
}

static string ResolveActivePersonalityAlias(
    AppSettings settings,
    PersonalityProfileStore personalityStore)
{
    var directory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
    var activeId = NormalizePersonalityId(settings.ActivePersonalityId);

    try
    {
        var descriptor = personalityStore.ListProfiles(directory)
            .FirstOrDefault(p => string.Equals(p.Id, activeId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is not null)
        {
            if (!string.IsNullOrWhiteSpace(descriptor.Alias))
                return descriptor.Alias;

            if (!string.IsNullOrWhiteSpace(descriptor.DisplayName))
                return descriptor.DisplayName;
        }
    }
    catch
    {
        // Best-effort alias resolution.
    }

    return "sir-thaddeus";
}

static string ToTerminalHandle(string? value, string fallback)
{
    var raw = (value ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(raw))
        return fallback;

    var chars = raw.Select(ch =>
    {
        if (char.IsLetterOrDigit(ch))
            return ch;
        return ch switch
        {
            ' ' => '-',
            '_' => '-',
            '.' => '-',
            _ => '-'
        };
    }).ToArray();

    var normalized = new string(chars);
    while (normalized.Contains("--", StringComparison.Ordinal))
        normalized = normalized.Replace("--", "-", StringComparison.Ordinal);

    normalized = normalized.Trim('-');
    return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
}

static AppSettings PersistGroupPolicyAsAlways(AppSettings settings, string group)
{
    var perms = settings.Mcp.Permissions;
    var updated = settings with
    {
        Mcp = settings.Mcp with
        {
            Permissions = group switch
            {
                "screen" => perms with { Screen = "always" },
                "files" => perms with { Files = "always" },
                "system" => perms with { System = "always" },
                "web" => perms with { Web = "always" },
                "memoryRead" => perms with { MemoryRead = "always" },
                "memoryWrite" => perms with { MemoryWrite = "always" },
                _ => perms
            }
        }
    };

    SettingsManager.Save(updated);
    return updated;
}

file sealed record HeadlessOptions(
    bool EnableTools,
    string? McpServerPath,
    bool ShowHelp,
    bool ServerMode,
    int ServerPort)
{
    public static HeadlessOptions Parse(string[] args)
    {
        var enableTools = false;
        string? mcpServerPath = null;
        var showHelp = false;
        var serverMode = false;
        var serverPort = 5378;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase))
            {
                showHelp = true;
                continue;
            }

            if (arg.Equals("--tools", StringComparison.OrdinalIgnoreCase))
            {
                enableTools = true;
                continue;
            }

            if (arg.Equals("--mcp-server", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                mcpServerPath = args[++i];
                continue;
            }

            if (arg.Equals("--server", StringComparison.OrdinalIgnoreCase))
            {
                serverMode = true;
                continue;
            }

            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var parsedPort) &&
                    parsedPort is >= 1 and <= 65535)
                {
                    serverPort = parsedPort;
                }
            }
        }

        return new HeadlessOptions(enableTools, mcpServerPath, showHelp, serverMode, serverPort);
    }
}

file sealed class ConsolePermissionGate : IToolPermissionGate
{
    private readonly object _consoleGate = new();
    private readonly IAuditLogger _audit;
    private readonly Action<string> _persistGroupAsAlways;
    private readonly ConcurrentDictionary<(string Group, int Epoch), bool> _sessionGrants = new();
    private volatile PolicySnapshot _snapshot;
    private volatile int _conversationEpoch;

    public ConsolePermissionGate(
        IAuditLogger audit,
        AppSettings initialSettings,
        Action<string> persistGroupAsAlways)
    {
        _audit = audit;
        _persistGroupAsAlways = persistGroupAsAlways;
        _snapshot = ToolGroupPolicy.BuildSnapshot(initialSettings, isDebugBuild: false);
    }

    public void ClearSessionGrants()
    {
        Interlocked.Increment(ref _conversationEpoch);
        _sessionGrants.Clear();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(settings, isDebugBuild: false);
    }

    public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var snapshot = _snapshot;
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        var policy = ToolGroupPolicy.ResolveEffectivePolicy(group, snapshot);

        if (policy == "off")
            return Task.FromResult(ToolPermissionResult.Deny("Disabled in settings"));

        if (policy == "always" || group == "meta")
            return Task.FromResult(ToolPermissionResult.NotRequired());

        var epoch = _conversationEpoch;
        var perCallOnly = ToolGroupPolicy.PerCallOnlyGroups.Contains(group);
        if (!perCallOnly && _sessionGrants.ContainsKey((group, epoch)))
            return Task.FromResult(ToolPermissionResult.NotRequired());

        lock (_consoleGate)
        {
            ct.ThrowIfCancellationRequested();
            var purpose = ToolGroupPolicy.BuildRedactedPurpose(canonical, argumentsJson);
            var riskTier = group switch
            {
                "meta" => "low",
                "memoryRead" => "low",
                "files" => "medium",
                "screen" => "medium",
                _ => "high"
            };

            _audit.Append(new AuditEvent
            {
                Actor = "gate",
                Action = "CONSENT_PROMPT_SHOWN",
                Result = "pending",
                Target = canonical,
                Details = new Dictionary<string, object>
                {
                    ["tool"] = canonical,
                    ["group"] = group,
                    ["risk_tier"] = riskTier
                }
            });

            var border = "+------------------------ Permission Required ------------------------+";
            var borderBottom = "+----------------------------------------------------------------------+";

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(border);
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Tool : {canonical}");
            Console.WriteLine($"  Group: {group}");
            Console.WriteLine($"  Risk : {riskTier}");
            Console.ResetColor();
            Console.WriteLine($"  Why  : {purpose}");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            if (perCallOnly)
            {
                Console.WriteLine("  Allow Once [Enter/A] | Deny [Esc/D]");
                Console.WriteLine("  Note : This tool always requires explicit per-call approval.");
            }
            else
            {
                Console.WriteLine("  Allow Once [Enter/A] | Allow Session [Tab/S] | Allow Always [Shift+Tab/P] | Deny [Esc/D]");
            }
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine(borderBottom);
            Console.ResetColor();
            Console.WriteLine();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.A)
                    return Task.FromResult(ToolPermissionResult.Grant());

                if (!perCallOnly && ((key.Key == ConsoleKey.Tab && key.Modifiers == 0) || key.Key == ConsoleKey.S))
                {
                    _sessionGrants[(group, epoch)] = true;
                    return Task.FromResult(ToolPermissionResult.Grant());
                }

                if (!perCallOnly && ((key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift)) ||
                    key.Key == ConsoleKey.P))
                {
                    _persistGroupAsAlways(group);
                    return Task.FromResult(ToolPermissionResult.NotRequired());
                }

                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.D)
                    return Task.FromResult(ToolPermissionResult.Deny("Denied by user"));
            }
        }
    }
}

