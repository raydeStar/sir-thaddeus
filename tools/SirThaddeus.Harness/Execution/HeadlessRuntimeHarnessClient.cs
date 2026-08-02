using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// v1-headless adapter. Starts the legacy headless runtime, drives the
/// public /api/chat endpoint, auto-approves permission prompts, and
/// reconstructs tool calls from the JSONL audit log. Reuses one process
/// across every test in a harness run.
///
/// Implements <see cref="IHarnessHostAdapter"/> so the harness orchestrator
/// can swap in the v2 hybrid runtime adapter (see
/// <c>HybridRuntimeHostAdapter</c>) once that path is wired.
/// </summary>
internal sealed class HeadlessRuntimeHarnessClient : IHarnessHostAdapter
{
    private readonly AppSettings _baseSettings;
    private readonly bool _requiresManagedSearch;
    private readonly int _port;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    private Process? _process;
    private HarnessRuntimeSandbox? _sandbox;
    private bool _processSpawnedThisCall;

    // Built once per process to avoid per-test DLL copy races when the
    // previous runtime process still holds file handles after Kill().
    private static bool _runtimeBuilt;
    private static readonly object _buildLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public HeadlessRuntimeHarnessClient(AppSettings settings, bool requiresManagedSearch = true)
    {
        _baseSettings = settings ?? throw new ArgumentNullException(nameof(settings));
        _requiresManagedSearch = requiresManagedSearch;
        _port = GetFreeTcpPort();
        _baseUri = new Uri($"http://127.0.0.1:{_port}/");
        _http = new HttpClient
        {
            BaseAddress = _baseUri,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
    }

    private static void EnsureRuntimeBuilt()
    {
        lock (_buildLock)
        {
            if (_runtimeBuilt)
                return;

            if (IsTrueEnvironmentValue("ST_HARNESS_SKIP_RUNTIME_BUILD"))
            {
                // Campaign wrappers build the harness and runtime once, then
                // launch several isolated harness processes. Validate the DLL
                // before trusting the opt-out so a stale flag fails clearly.
                _ = ResolveHeadlessRuntimeAssembly();
                _runtimeBuilt = true;
                return;
            }

            var project = ResolveHeadlessRuntimeProject();
            var build = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{project}\" -c Debug --no-restore",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };
            build.Start();
            build.WaitForExit();
            if (build.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Headless runtime pre-build failed (exit {build.ExitCode}).");

            _runtimeBuilt = true;
        }
    }

    public async Task<HostExecutionResult> ExecuteAsync(
        HarnessTestCase test,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.StateSetup.WikiRoots.Count > 0 || test.Observations.Count > 0)
        {
            throw new NotSupportedException(
                "State setup and observations require the v2 hybrid harness target.");
        }

        using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        itemCts.CancelAfter(GetItemTimeout());
        var itemToken = itemCts.Token;
        var phase = "runtime_start";

        try
        {
            var totalStopwatch = Stopwatch.StartNew();

            WriteHarnessPhase(test.Id, phase);
            EnsureRuntimeBuilt();
            var warmupStopwatch = Stopwatch.StartNew();
            await EnsureRuntimeProcessAsync(itemToken);
            warmupStopwatch.Stop();
            var warmupSeconds = _processSpawnedThisCall ? warmupStopwatch.Elapsed.TotalSeconds : 0;
            _processSpawnedThisCall = false;

            var resetStopwatch = Stopwatch.StartNew();
            phase = "reset";
            WriteHarnessPhase(test.Id, phase);
            await ApplyHarnessResetAsync(test, itemToken);

            if (!string.IsNullOrWhiteSpace(test.PersonalityId))
            {
                phase = "personality";
                WriteHarnessPhase(test.Id, phase);
                await SetActivePersonalityAsync(test.PersonalityId!, itemToken);
            }

            phase = "audit_baseline";
            WriteHarnessPhase(test.Id, phase);
            var auditBaseline = await GetAuditEntriesAsync(itemToken);
            resetStopwatch.Stop();
            var resetSeconds = resetStopwatch.Elapsed.TotalSeconds;

            var workStopwatch = Stopwatch.StartNew();
            var auditCaptureStart = DateTimeOffset.UtcNow;
            phase = "usage_before";
            WriteHarnessPhase(test.Id, phase);
            var usageBefore = await GetLlmUsageAsync(itemToken);
            phase = "chat_start";
            WriteHarnessPhase(test.Id, phase);
            var startResponse = await PostChatAsync(test.UserMessage, itemToken);
            phase = "chat_events";
            WriteHarnessPhase(test.Id, phase);
            var runOutcome = await ReadRunToCompletionAsync(startResponse.RunId, itemToken);
            phase = "tool_evidence";
            WriteHarnessPhase(test.Id, phase);
            var fullToolEvidence = await GetHarnessToolEvidenceAsync(startResponse.RunId, itemToken);
            phase = "usage_after";
            WriteHarnessPhase(test.Id, phase);
            var usageAfter = await GetLlmUsageAsync(itemToken);
            workStopwatch.Stop();
            var workSeconds = workStopwatch.Elapsed.TotalSeconds;

            phase = "audit_final";
            WriteHarnessPhase(test.Id, phase);
            var auditEntries = FilterNewAuditEntries(
                auditCaptureStart,
                auditBaseline,
                await GetAuditEntriesAsync(itemToken));

            var trace = BuildToolTraceFromAudit(auditEntries);
            var (toolCalls, toolTurns, steps) = ToolEvidenceTraceEnricher.Enrich(trace, fullToolEvidence);

            var finalSteps = steps.ToList();
            finalSteps.Add(new TraceStep
            {
                StepIndex = finalSteps.Count + 1,
                StepType = "final_response",
                StartedAt = DateTimeOffset.UtcNow,
                Content = runOutcome.FinalText
            });

            var response = new AgentResponse
            {
                Text = runOutcome.FinalText,
                Success = runOutcome.Success,
                Error = runOutcome.Success ? null : runOutcome.Error,
                ToolCallsMade = toolCalls,
                LlmRoundTrips = (int)Math.Max(0, usageAfter.RequestCount - usageBefore.RequestCount),
                TokenUsage = new AgentTokenUsage
                {
                    TokensIn = (int)Math.Max(0, usageAfter.PromptTokens - usageBefore.PromptTokens),
                    TokensOut = (int)Math.Max(0, usageAfter.CompletionTokens - usageBefore.CompletionTokens),
                    TotalTokens = (int)Math.Max(0, usageAfter.TotalTokens - usageBefore.TotalTokens),
                    ContextWindowTokens = usageAfter.ContextWindowTokens
                },
                Sources = runOutcome.Sources
            };

            totalStopwatch.Stop();

            return new HostExecutionResult
            {
                Response = response,
                Steps = finalSteps,
                ToolTurns = toolTurns,
                Timing = new HarnessTiming(
                    RuntimeWarmupSeconds: warmupSeconds,
                    ResetSeconds: resetSeconds,
                    TestWorkSeconds: workSeconds,
                    TotalSeconds: totalStopwatch.Elapsed.TotalSeconds)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Harness item '{test.Id}' timed out during phase '{phase}' after {GetItemTimeout().TotalSeconds:0}s.");
        }
    }

    private static void WriteHarnessPhase(string itemId, string phase)
        => Console.Error.WriteLine($"[harness] item={itemId} phase={phase}");

    private static TimeSpan GetItemTimeout()
        => ParseItemTimeout(Environment.GetEnvironmentVariable("ST_HARNESS_ITEM_TIMEOUT_SECONDS"));

    internal static TimeSpan ParseItemTimeout(string? raw)
    {
        return int.TryParse(raw, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 600))
            : TimeSpan.FromSeconds(120);
    }

    /// <summary>
    /// Spawns the headless runtime exactly once per harness run and reuses
    /// it for every test. The first call pays the dotnet startup, health
    /// probe, and SearxNG warm-up costs (~10–45s); subsequent tests only
    /// pay a sub-second reset round-trip.
    /// </summary>
    private async Task EnsureRuntimeProcessAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false })
            return;

        DisposeRuntimeProcess();
        _processSpawnedThisCall = true;
        _sandbox ??= HarnessRuntimeSandbox.CreateShared(_baseSettings, _requiresManagedSearch);

        lock (_stdout)
            _stdout.Clear();
        lock (_stderr)
            _stderr.Clear();

        var runtimeDll = ResolveHeadlessRuntimeAssembly();
        var startInfo = new ProcessStartInfo
        {
            // Direct DLL invocation skips ~0.5s of `dotnet run` project
            // resolution and dependency-graph loading. EnsureRuntimeBuilt
            // already produced the assembly, so this is safe.
            FileName = "dotnet",
            Arguments = $"exec \"{runtimeDll}\" --server --tools --port {_port}",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in _sandbox.Environment)
            startInfo.Environment[pair.Key] = pair.Value;
        // Marker so the runtime exposes /api/harness/reset only when the
        // harness is the parent process.
        startInfo.Environment["ST_HARNESS_RUN_ACTIVE"] = "true";

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (_stdout)
                    _stdout.Add(e.Data);
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                lock (_stderr)
                    _stderr.Add(e.Data);
            }
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitForHealthyAsync(cancellationToken);
        if (_requiresManagedSearch)
        {
            await WaitForSearxngReadyAsync(cancellationToken);
        }
        else
        {
            Console.Error.WriteLine("[harness] Managed search startup skipped: every selected test has an explicit non-web tool boundary.");
        }
        await PrewarmAgentPipelineAsync(cancellationToken);
    }

    /// <summary>
    /// Pays the agent-pipeline / MCP-client / HTTP-client lazy-init cost
    /// during the runtime warmup phase instead of letting it land on the
    /// first real test. Sends a tiny chat with no tools allowed so the LLM
    /// composes a one-shot reply and the pipeline JITs end-to-end. Reset
    /// runs after this to clear the warmup conversation from history.
    /// </summary>
    private async Task PrewarmAgentPipelineAsync(CancellationToken cancellationToken)
    {
        if (ShouldSkipPrewarm())
        {
            Console.Error.WriteLine("[harness] Headless runtime prewarm skipped by ST_HARNESS_SKIP_PREWARM.");
            return;
        }

        using var warmupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        warmupCts.CancelAfter(GetPrewarmTimeout());

        try
        {
            var resetRequest = new HarnessResetRequest(
                AllowedTools: "__none__",
                StubOverrides: null,
                ClearMemoryData: true,
                ClearChatHistory: true);
            using (var resetResponse = await _http.PostAsJsonAsync(
                "api/harness/reset", resetRequest, JsonOptions, warmupCts.Token))
            {
                resetResponse.EnsureSuccessStatusCode();
            }

            // Short, literal prompt the LLM can answer in <= 1 token. Cuts
            // generation time during warmup vs an open-ended "ping" that
            // might produce a paragraph-long preamble.
            var startResponse = await PostChatAsync("Reply with only the word ok.", warmupCts.Token);
            await ReadRunToCompletionAsync(startResponse.RunId, warmupCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[harness] Headless runtime prewarm timed out after {GetPrewarmTimeout().TotalSeconds:0}s; proceeding.");
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[harness] Headless runtime prewarm failed: {ex.Message}");
            // Warmup is best-effort. If it fails, the first real test will
            // simply pay the cold-start cost — not worth aborting the run.
        }
    }

    private static bool ShouldSkipPrewarm()
        => IsTrueEnvironmentValue("ST_HARNESS_SKIP_PREWARM");

    private static bool IsTrueEnvironmentValue(string name)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return raw is not null &&
               (raw.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                raw.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static TimeSpan GetPrewarmTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_PREWARM_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds)
            ? TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60))
            : TimeSpan.FromSeconds(10);
    }

    private async Task ApplyHarnessResetAsync(HarnessTestCase test, CancellationToken cancellationToken)
    {
        var allowedTools = test.Assertions.AllowedToolsOnly
            ? (test.AllowedTools.Count == 0 ? "__none__" : string.Join(",", test.AllowedTools))
            : string.Empty; // empty string clears the override server-side

        Dictionary<string, string?>? stubOverrides = null;
        if (string.Equals(test.Mode, "stub", StringComparison.OrdinalIgnoreCase) &&
            test.Stub.PerToolFailures.Count > 0)
        {
            stubOverrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (toolName, failure) in test.Stub.PerToolFailures)
                stubOverrides[toolName] = failure;
        }

        var request = new HarnessResetRequest(
            AllowedTools: allowedTools,
            StubOverrides: stubOverrides,
            ClearMemoryData: true,
            ClearChatHistory: true);

        using var response = await _http.PostAsJsonAsync(
            "api/harness/reset",
            request,
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static IReadOnlyList<AuditEntryDto> FilterNewAuditEntries(
        DateTimeOffset captureStart,
        IReadOnlyList<AuditEntryDto> baselineEntries,
        IReadOnlyList<AuditEntryDto> currentEntries)
    {
        if (currentEntries.Count == 0)
            return currentEntries;

        var knownEntries = baselineEntries
            .Select(GetAuditSignature)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return currentEntries
            .Where(entry => entry.TimestampUtc >= captureStart)
            .Where(entry => !knownEntries.Contains(GetAuditSignature(entry)))
            .ToArray();
    }

    private async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("api/session/clear", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SetActivePersonalityAsync(string personalityId, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/personalities/active",
            new SetActivePersonalityRequest(personalityId),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<ChatStartResponse> PostChatAsync(string prompt, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/chat",
            new ChatRequest(prompt),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<ChatStartResponse>(JsonOptions, cancellationToken);
        return payload ?? throw new InvalidOperationException("Headless runtime returned an empty chat start response.");
    }

    private async Task<LlmUsageSnapshot> GetLlmUsageAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/diagnostics/llm-usage", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LlmUsageSnapshot>(JsonOptions, cancellationToken)
               ?? new LlmUsageSnapshot();
    }

    private async Task<IReadOnlyList<ToolCallRecord>> GetHarnessToolEvidenceAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(
            $"api/harness/runs/{Uri.EscapeDataString(runId)}/tool-evidence",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ToolCallRecord[]>(JsonOptions, cancellationToken) ?? [];
    }

    private async Task<(bool Success, string FinalText, string? Error, IReadOnlyList<AgentSource> Sources)> ReadRunToCompletionAsync(
        string runId,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"api/runs/{runId}/events");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? finalText = null;
        string? error = null;
        IReadOnlyList<AgentSource> sources = [];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
                continue;

            using var doc = JsonDocument.Parse(line[6..]);
            var root = doc.RootElement;
            var eventType = root.TryGetProperty("eventType", out var eventTypeProp)
                ? eventTypeProp.GetString()
                : null;

            if (!root.TryGetProperty("payload", out var payload))
                continue;

            switch (eventType)
            {
                case RuntimeEventTypes.ToolRequested:
                {
                    var requestId = payload.TryGetProperty("requestId", out var requestIdProp)
                        ? requestIdProp.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(requestId))
                    {
                        using var decisionResponse = await _http.PostAsJsonAsync(
                            $"api/permissions/{requestId}/decision",
                            new PermissionDecisionRequest(true, false, false),
                            JsonOptions,
                            cancellationToken);
                        decisionResponse.EnsureSuccessStatusCode();
                    }
                    break;
                }
                case RuntimeEventTypes.RunCompleted:
                    finalText = payload.TryGetProperty("finalText", out var finalTextProp)
                        ? finalTextProp.GetString()
                        : null;
                    sources = TryReadSourceCards(payload);
                    return (true, finalText ?? string.Empty, null, sources);
                case RuntimeEventTypes.RunFailed:
                    error = payload.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString()
                        : "Unknown headless runtime failure.";
                    return (false, error ?? "Unknown headless runtime failure.", error, sources);
            }
        }

        return (false, finalText ?? "Headless runtime event stream ended before completion.", "event_stream_incomplete", sources);
    }

    private static IReadOnlyList<AgentSource> TryReadSourceCards(JsonElement payload)
    {
        if (!payload.TryGetProperty("sourceCards", out var sourceCards) ||
            sourceCards.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var sources = new List<AgentSource>();
        foreach (var item in sourceCards.EnumerateArray())
        {
            var url = TryReadString(item, "url");
            if (string.IsNullOrWhiteSpace(url))
                continue;

            sources.Add(new AgentSource
            {
                Url = url!,
                Title = TryReadString(item, "title"),
                Domain = TryReadString(item, "domain"),
                Excerpt = TryReadString(item, "excerpt"),
                Favicon = TryReadString(item, "favicon"),
                Thumbnail = TryReadString(item, "thumbnail"),
                PublishedAt = TryReadString(item, "publishedAt")
            });
        }

        return sources;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<IReadOnlyList<AuditEntryDto>> GetAuditEntriesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/audit?take=500", cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuditEntryDto[]>(JsonOptions, cancellationToken);
        return payload ?? [];
    }

    private static (IReadOnlyList<ToolCallRecord> ToolCalls, IReadOnlyList<RecordedToolTurn> ToolTurns, IReadOnlyList<TraceStep> Steps)
        BuildToolTraceFromAudit(IReadOnlyList<AuditEntryDto> auditEntries)
        => AuditTraceBuilder.BuildFromAuditEntries(auditEntries);

    private async Task WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        Exception? lastError = null;

        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(90))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"Headless runtime exited before becoming healthy. stderr={string.Join(" | ", _stderr.TakeLast(10))}");
            }

            try
            {
                using var response = await _http.GetAsync("api/health", cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            await Task.Delay(100, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for headless runtime health. Last error: {lastError?.Message}");
    }

    /// <summary>
    /// Waits for SearXNG to become available via the /api/search/status
    /// endpoint. This prevents the race condition where the first web_search
    /// call probes SearXNG before it finishes booting, caches "unavailable"
    /// for 5 minutes, and forces all searches to the GoogleNews fallback.
    /// </summary>
    private async Task WaitForSearxngReadyAsync(CancellationToken cancellationToken)
    {
        const int maxWaitSeconds = 45;
        var start = DateTime.UtcNow;

        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(maxWaitSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await _http.GetAsync("api/search/status", cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("liveSearchAvailable", out var live) &&
                        live.GetBoolean())
                    {
                        return;
                    }

                    // Check if SearXNG is explicitly disabled/skipped — don't wait forever.
                    if (doc.RootElement.TryGetProperty("searxng", out var sxng) &&
                        sxng.TryGetProperty("status", out var status))
                    {
                        var statusText = status.GetString() ?? "";
                        if (statusText.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                            statusText.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            Console.Error.WriteLine("[harness] SearXNG is disabled/skipped — proceeding with fallback providers.");
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Endpoint not ready yet, keep retrying.
            }

            await Task.Delay(500, cancellationToken);
        }

        Console.Error.WriteLine(
            $"[harness] WARNING: SearXNG did not become available within {maxWaitSeconds}s. " +
            "Tests will proceed but searches may fall back to GoogleNews.");
    }

    private static string ResolveHeadlessRuntimeProject()
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var projectPath = Path.Combine(
            repoRoot,
            "apps",
            "headless-runtime",
            "SirThaddeus.HeadlessRuntime",
            "SirThaddeus.HeadlessRuntime.csproj");

        if (!File.Exists(projectPath))
            throw new FileNotFoundException("Headless runtime project not found.", projectPath);

        return projectPath;
    }

    private static string ResolveHeadlessRuntimeAssembly()
    {
        var repoRoot = Directory.GetCurrentDirectory();
        var assemblyPath = Path.Combine(
            repoRoot,
            "apps",
            "headless-runtime",
            "SirThaddeus.HeadlessRuntime",
            "bin",
            "Debug",
            "net10.0",
            "SirThaddeus.HeadlessRuntime.dll");

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException(
                "Headless runtime assembly not found — EnsureRuntimeBuilt should run first.",
                assemblyPath);

        return assemblyPath;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GetAuditSignature(AuditEntryDto entry)
        => string.Join(
            "|",
            entry.TimestampUtc.ToUnixTimeMilliseconds().ToString(),
            entry.Category,
            entry.Message,
            entry.CorrelationId ?? string.Empty,
            entry.MetadataJson ?? string.Empty);

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        DisposeRuntimeProcess();
        _sandbox?.Dispose();
        _sandbox = null;

        return ValueTask.CompletedTask;
    }

    private void DisposeRuntimeProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }
}
