using System.Collections.Concurrent;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.LlmClient;
using SirThaddeus.RuntimeHost;

var options = HeadlessOptions.Parse(args);
if (options.ShowHelp)
{
    PrintHelp();
    return;
}

var load = SettingsManager.LoadWithDiagnostics();
var settings = load.Settings;

using var audit = JsonLineAuditLogger.CreateDefault();
using var llm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildPrimary(settings));

var mcp = await CreateMcpClientAsync(options, settings, audit, CancellationToken.None);
await using var mcpScope = mcp.Scope;
ConsolePermissionGate? permissionGate = null;

IMcpToolClient agentMcp = mcp.Client;
if (options.EnableTools)
{
    permissionGate = new ConsolePermissionGate(
        audit,
        settings,
        persistGroupAsAlways: group =>
        {
            settings = PersistGroupPolicyAsAlways(settings, group);
            permissionGate?.UpdateSettings(settings);
        });

    agentMcp = new AuditedMcpToolClient(
        mcp.Client,
        audit,
        permissionGate,
        sessionId: Guid.NewGuid().ToString("N")[..12],
        runtimeControls: () => RuntimeControlState.FromSettings(settings));
}

var orchestrator = new AgentOrchestrator(
    llm,
    agentMcp,
    audit,
    settings.Llm.SystemPrompt,
    activePersonalityId: settings.ActivePersonalityId,
    personalityProfilesDirectory: SettingsManager.ResolvePersonalityProfilesDirectory(settings))
{
    ActiveProfileId = settings.ActiveProfileId,
    MemoryEnabled = options.EnableTools && settings.Memory.Enabled,
    UserLocationHint = settings.GetEffectiveUserLocation(settings.ActiveProfileId).GetResolvedLabel(),
    UserTimezone = settings.GetEffectiveUserLocation(settings.ActiveProfileId).GetResolvedTimezone(),
    PreferredUnits = settings.Weather.GetNormalizedUnitSystem()
};

PrintBanner(settings, options);
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
    Console.Write("you> ");
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

    try
    {
        var response = await orchestrator.ProcessAsync(input, cancellation.Token);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("thaddeus> ");
        Console.ResetColor();
        Console.WriteLine(response.Text);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Cancelled.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
    }
}

static async Task<(IMcpToolClient Client, IAsyncDisposable Scope)> CreateMcpClientAsync(
    HeadlessOptions options,
    AppSettings settings,
    IAuditLogger audit,
    CancellationToken cancellationToken)
{
    if (!options.EnableTools)
        return (new NoToolsMcpClient(), AsyncNoop.Instance);

    var serverPath = string.IsNullOrWhiteSpace(options.McpServerPath)
        ? RuntimePathResolver.ResolveMcpServerPath(settings.Mcp.ServerPath, Directory.GetCurrentDirectory())
        : Path.GetFullPath(options.McpServerPath.Trim());
    var env = RuntimeMcpEnvironmentBuilder.Build(settings);
    var client = new StdioMcpToolClient(serverPath, env, "HeadlessRuntime", "0.1.0", audit);
    await client.StartAsync(cancellationToken);
    return (client, client);
}

static void PrintBanner(AppSettings settings, HeadlessOptions options)
{
    Console.WriteLine("  ____  _        _____ _               _     _                 ");
    Console.WriteLine(" / ___|(_)_ __  |_   _| |__   __ _  __| | __| | ___ _   _ ___ ");
    Console.WriteLine(" \\___ \\| | '__|   | | | '_ \\ / _` |/ _` |/ _` |/ _ \\ | | / __|");
    Console.WriteLine("  ___) | | |      | | | | | | (_| | (_| | (_| |  __/ |_| \\__ \\");
    Console.WriteLine(" |____/|_|_|      |_| |_| |_|\\__,_|\\__,_|\\__,_|\\___|\\__,_|___/");
    Console.WriteLine();
    Console.WriteLine($"Model: {settings.Llm.Model}");
    Console.WriteLine($"LLM:   {settings.Llm.BaseUrl}");
    Console.WriteLine($"Tools: {(options.EnableTools ? "enabled" : "disabled")}");
    Console.WriteLine();
}

static void PrintHelpHint()
{
    Console.WriteLine("Type a message, or /help for commands.");
}

static void PrintHelp()
{
    Console.WriteLine("Headless Runtime Commands");
    Console.WriteLine("  /help   Show help");
    Console.WriteLine("  /reset  Clear conversation state");
    Console.WriteLine("  /tools  Show detected MCP tool count");
    Console.WriteLine("  /exit   Quit");
    Console.WriteLine();
    Console.WriteLine("CLI options");
    Console.WriteLine("  --tools                 Enable MCP tool calls");
    Console.WriteLine("  --mcp-server <path>     MCP server executable path");
    Console.WriteLine("  --help                  Show this help");
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

file sealed record HeadlessOptions(bool EnableTools, string? McpServerPath, bool ShowHelp)
{
    public static HeadlessOptions Parse(string[] args)
    {
        var enableTools = false;
        string? mcpServerPath = null;
        var showHelp = false;

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
            }
        }

        return new HeadlessOptions(enableTools, mcpServerPath, showHelp);
    }
}

file sealed class NoToolsMcpClient : IMcpToolClient
{
    public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpToolInfo>>([]);

    public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
        => Task.FromResult($"Error: Tool '{toolName}' is unavailable in no-tools mode.");
}

file sealed class AsyncNoop : IAsyncDisposable
{
    public static readonly AsyncNoop Instance = new();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        if (_sessionGrants.ContainsKey((group, epoch)))
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
            Console.WriteLine("  Allow Once [Enter/A] | Allow Session [Tab/S] | Allow Always [Shift+Tab/P] | Deny [Esc/D]");
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

                if ((key.Key == ConsoleKey.Tab && key.Modifiers == 0) || key.Key == ConsoleKey.S)
                {
                    _sessionGrants[(group, epoch)] = true;
                    return Task.FromResult(ToolPermissionResult.Grant());
                }

                if ((key.Key == ConsoleKey.Tab && key.Modifiers.HasFlag(ConsoleModifiers.Shift)) ||
                    key.Key == ConsoleKey.P)
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
