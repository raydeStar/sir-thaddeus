using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;
using Thaddeus.SharedTypes;

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// v2 hybrid-runtime adapter. Drives <c>Thaddeus.Runtime</c> via its
/// thread-based REST + WebSocket surface (the same endpoints the React
/// UI uses).
///
/// <para>End-to-end test flow per <see cref="ExecuteAsync"/>:</para>
/// <list type="number">
///   <item>Spawn the runtime on first call: <c>dotnet exec Thaddeus.Runtime.dll
///         --lock-file=&lt;sandbox&gt;/runtime.lock --test-mode --parent-pid=&lt;harness pid&gt;</c></item>
///   <item>Poll the lock file until the runtime writes its bound port + bearer token.</item>
///   <item>Open a single WebSocket to <c>/ws</c> with the bearer token; a reader
///         loop drains events into an in-memory channel for the lifetime of the adapter.</item>
///   <item>Per test: POST <c>/api/harness/reset</c>, POST <c>/api/threads</c>,
///         POST <c>/api/threads/{id}/messages</c>.</item>
///   <item>Auto-approve any <c>permission.request</c> events the WS pushes during
///         the turn (decision = "session" so the same group sticks for the rest
///         of the test; <c>HarnessApi</c>'s <c>ClearSessionGrants</c> drops it
///         again on the next reset).</item>
///   <item>Wait for <c>chat.turn.complete</c> matching our threadId; build the
///         tool trace from the <c>chat.tool.started</c>/<c>chat.tool.completed</c>
///         events captured during the turn.</item>
/// </list>
///
/// <para>Sandbox isolation comes free with v2: passing <c>--lock-file</c> makes
/// the runtime derive every other path (settings, memos, routines, wiki) from
/// the lock file's directory, so each harness run gets a clean tree.</para>
/// </summary>
internal sealed class HybridRuntimeHostAdapter : IHarnessHostAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    // Built once per harness process to avoid per-test DLL copy races.
    private static bool _runtimeBuilt;
    private static readonly object _buildLock = new();

    private readonly AppSettings _baseSettings;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    private string? _sandboxRoot;
    private string? _lockFilePath;
    private string? _bearerToken;
    private Uri? _baseUri;
    private HttpClient? _http;
    private ClientWebSocket? _ws;
    private Task? _wsReader;
    private CancellationTokenSource? _wsCts;
    private Process? _process;
    private bool _processSpawnedThisCall;

    // Per-turn event capture. ExecuteAsync sets these before posting the
    // chat message; the WS reader publishes into them as events arrive.
    private Channel<JsonDocument>? _events;

    public HybridRuntimeHostAdapter(AppSettings settings)
    {
        _baseSettings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<HostExecutionResult> ExecuteAsync(
        HarnessTestCase test,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);

        var totalStopwatch = Stopwatch.StartNew();

        EnsureRuntimeBuilt();
        var warmupStopwatch = Stopwatch.StartNew();
        await EnsureRuntimeProcessAsync(cancellationToken).ConfigureAwait(false);
        warmupStopwatch.Stop();
        var warmupSeconds = _processSpawnedThisCall ? warmupStopwatch.Elapsed.TotalSeconds : 0;
        _processSpawnedThisCall = false;

        var resetStopwatch = Stopwatch.StartNew();
        await ApplyHarnessResetAsync(test, cancellationToken).ConfigureAwait(false);
        await ApplyStateSetupAsync(test.StateSetup, cancellationToken).ConfigureAwait(false);
        resetStopwatch.Stop();
        var resetSeconds = resetStopwatch.Elapsed.TotalSeconds;

        // Capture the audit-file mark so we only count tool calls from this
        // turn — the file accumulates across the whole harness run.
        var auditCaptureStart = DateTimeOffset.UtcNow;

        var workStopwatch = Stopwatch.StartNew();
        var usageBefore = await GetLlmUsageAsync(cancellationToken).ConfigureAwait(false);
        var (finalText, success, error) =
            await RunChatAsync(test.UserMessage, cancellationToken).ConfigureAwait(false);
        var usageAfter = await GetLlmUsageAsync(cancellationToken).ConfigureAwait(false);
        workStopwatch.Stop();
        var workSeconds = workStopwatch.Elapsed.TotalSeconds;

        // Tool trace comes from the JSONL audit file v2 writes alongside the
        // lock file. WS-borne ChatToolCompleted events carry only a 280-char
        // snippet, which the scorer can't use to detect token incorporation;
        // the audit file has the full input/output. Same canonical path as v1.
        var auditFile = ResolveAuditFilePath();
        var (toolCalls, toolTurns, steps) =
            AuditTraceBuilder.BuildFromAuditFile(auditFile, auditCaptureStart);

        var finalSteps = steps.ToList();
        finalSteps.Add(new TraceStep
        {
            StepIndex = finalSteps.Count + 1,
            StepType = "final_response",
            StartedAt = DateTimeOffset.UtcNow,
            Content = finalText
        });

        var response = new AgentResponse
        {
            Text = finalText,
            Success = success,
            Error = success ? null : error,
            ToolCallsMade = toolCalls,
            LlmRoundTrips = (int)Math.Max(0, usageAfter.RequestCount - usageBefore.RequestCount),
            TokenUsage = new AgentTokenUsage
            {
                TokensIn = (int)Math.Max(0, usageAfter.PromptTokens - usageBefore.PromptTokens),
                TokensOut = (int)Math.Max(0, usageAfter.CompletionTokens - usageBefore.CompletionTokens),
                TotalTokens = (int)Math.Max(0, usageAfter.TotalTokens - usageBefore.TotalTokens),
                ContextWindowTokens = usageAfter.ContextWindowTokens
            }
        };

        totalStopwatch.Stop();
        var observedState = await CaptureObservedStateAsync(test.Observations, cancellationToken)
            .ConfigureAwait(false);

        return new HostExecutionResult
        {
            Response = response,
            Steps = finalSteps,
            ToolTurns = toolTurns,
            ObservedState = observedState,
            Timing = new HarnessTiming(
                RuntimeWarmupSeconds: warmupSeconds,
                ResetSeconds: resetSeconds,
                TestWorkSeconds: workSeconds,
                TotalSeconds: totalStopwatch.Elapsed.TotalSeconds)
        };
    }

    private static void EnsureRuntimeBuilt()
    {
        lock (_buildLock)
        {
            if (_runtimeBuilt) return;

            var project = ResolveHybridRuntimeProject();
            var build = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = $"build \"{project}\" -c Debug --no-restore",
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    UseShellExecute = false,
                    CreateNoWindow = false
                }
            };
            build.Start();
            build.WaitForExit();
            if (build.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Hybrid runtime pre-build failed (exit {build.ExitCode}).");

            _runtimeBuilt = true;
        }
    }

    private async Task EnsureRuntimeProcessAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _http is not null && _ws is { State: WebSocketState.Open })
            return;

        DisposeProcessAndConnections();
        _processSpawnedThisCall = true;

        // Sandbox: just a fresh dir + lock file path. v2 derives memos /
        // routines / settings paths from the lock file's directory, so a
        // clean dir means a clean state.
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "SirThaddeus.Harness",
            $"hybrid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_sandboxRoot);
        _lockFilePath = Path.Combine(_sandboxRoot, "runtime.lock");

        // Pre-write a settings file pointing at the user's configured LLM
        // so v2 hits the same LM Studio the v1 harness uses. Otherwise it
        // falls back to the v2 defaults (also LM Studio at :1234, but the
        // user may have customised theirs).
        WriteHarnessSettingsFile(_sandboxRoot);

        var runtimeDll = ResolveHybridRuntimeAssembly();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"exec \"{runtimeDll}\" --lock-file=\"{_lockFilePath}\" --test-mode " +
                        $"--parent-pid={Environment.ProcessId}",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.Environment["ST_HARNESS_RUN_ACTIVE"] = "true";

        lock (_stdout) _stdout.Clear();
        lock (_stderr) _stderr.Clear();

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_stdout) _stdout.Add(e.Data);
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                lock (_stderr) _stderr.Add(e.Data);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        var lockFile = await WaitForLockFileAsync(_lockFilePath, cancellationToken).ConfigureAwait(false);
        _bearerToken = lockFile.Token;
        _baseUri = new Uri($"http://127.0.0.1:{lockFile.Port}/");

        _http = new HttpClient { BaseAddress = _baseUri, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add("X-Thaddeus-Token", _bearerToken);

        await WaitForHealthyAsync(cancellationToken).ConfigureAwait(false);
        await OpenWebSocketAsync(cancellationToken).ConfigureAwait(false);
        await PrewarmAgentPipelineAsync(cancellationToken).ConfigureAwait(false);
    }

    internal void WriteHarnessSettingsFile(string sandboxRoot)
    {
        // v2 reads runtime-settings.json from the lock file's directory by
        // default. Start from the complete production defaults, then override
        // the evaluator's frozen provider settings so LM Studio expectations
        // match v1's. Permissions stay "ask" by default — the harness
        // auto-approves via the WS permission flow.
        var llm = _baseSettings.Llm;
        var baseUrl = llm.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl) && !baseUrl.EndsWith("/v1"))
            baseUrl = baseUrl.TrimEnd('/') + "/v1";

        var defaults = SettingsDocument.Defaults();
        var doc = defaults with
        {
            Llm = defaults.Llm with
            {
                Provider = "lmstudio",
                ModelId = string.IsNullOrWhiteSpace(llm.Model) ? "auto" : llm.Model,
                BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://127.0.0.1:1234/v1" : baseUrl,
                ApiKey = null,
                MaxTokens = llm.MaxTokens,
                ContextWindowTokens = llm.ContextWindowTokens,
                Temperature = llm.Temperature,
                GatekeeperBaseUrl = string.IsNullOrWhiteSpace(llm.GatekeeperBaseUrl)
                    ? baseUrl
                    : llm.GatekeeperBaseUrl,
                GatekeeperModelId = string.IsNullOrWhiteSpace(llm.GatekeeperModelId)
                    ? llm.Model
                    : llm.GatekeeperModelId,
                ReusePrimaryForGatekeeperOnSharedEndpoint = llm.ReusePrimaryModelForGatekeeperOnSharedEndpoint,
                EnableStartupWarmup = false,
                EnableKeepWarm = false,
            }
        };

        var settingsPath = Path.Combine(sandboxRoot, "runtime-settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        }));
    }

    private static async Task<HybridLockFile> WaitForLockFileAsync(
        string lockFilePath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(lockFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(lockFilePath, cancellationToken)
                        .ConfigureAwait(false);
                    var parsed = JsonSerializer.Deserialize<HybridLockFile>(json, JsonOptions);
                    if (parsed is { Port: > 0 } && !string.IsNullOrWhiteSpace(parsed.Token))
                        return parsed;
                }
                catch
                {
                    // File was being written when we read; retry.
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Hybrid runtime did not write lock file '{lockFilePath}' within 60s.");
    }

    private async Task WaitForHealthyAsync(CancellationToken cancellationToken)
    {
        Debug.Assert(_http is not null);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process is { HasExited: true })
                throw new InvalidOperationException(
                    $"Hybrid runtime exited before becoming healthy. stderr={string.Join(" | ", _stderr.TakeLast(10))}");

            try
            {
                using var resp = await _http!.GetAsync("api/health", cancellationToken)
                    .ConfigureAwait(false);
                if (resp.IsSuccessStatusCode) return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Timed out waiting for hybrid runtime health. Last error: {lastError?.Message}");
    }

    private async Task OpenWebSocketAsync(CancellationToken cancellationToken)
    {
        Debug.Assert(_baseUri is not null);
        Debug.Assert(_bearerToken is not null);

        _wsCts = new CancellationTokenSource();
        var wsUri = new Uri(
            $"ws://{_baseUri!.Host}:{_baseUri.Port}/ws?access_token={Uri.EscapeDataString(_bearerToken!)}");
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("X-Thaddeus-Token", _bearerToken!);
        await _ws.ConnectAsync(wsUri, cancellationToken).ConfigureAwait(false);
        _wsReader = Task.Run(() => ReadWebSocketAsync(_wsCts.Token));
    }

    private async Task ReadWebSocketAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (_ws is { State: WebSocketState.Open } && !cancellationToken.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                ms.Position = 0;
                JsonDocument doc;
                try
                {
                    doc = await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (JsonException) { continue; }

                var type = doc.RootElement.TryGetProperty("type", out var typeEl)
                    ? typeEl.GetString()
                    : null;

                // Auto-approve permission requests inline (fire-and-forget).
                // Extract the id BEFORE spawning the task so we never race
                // against the consumer disposing `doc` via `using var _ = doc;`.
                if (string.Equals(type, "permission.request", StringComparison.Ordinal) &&
                    doc.RootElement.TryGetProperty("payload", out var permPayload) &&
                    permPayload.TryGetProperty("id", out var idEl) &&
                    idEl.GetString() is { Length: > 0 } pendingId)
                {
                    _ = Task.Run(() => RespondPermissionAsync(pendingId), cancellationToken);
                }

                // Always push to the per-turn event channel if one is active.
                _events?.Writer.TryWrite(doc);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (WebSocketException) { /* connection lost */ }
    }

    private async Task RespondPermissionAsync(string pendingId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pendingId) || _http is null) return;

            using var resp = await _http.PostAsJsonAsync(
                "api/permissions/respond",
                new { id = pendingId, decision = "session" },
                JsonOptions);
            // Ignore failures; the runtime will time out the request and
            // the test will surface that as a failure organically.
        }
        catch
        {
            // Best-effort: a permission-respond failure shouldn't crash
            // the WS reader.
        }
    }

    private async Task ApplyHarnessResetAsync(HarnessTestCase test, CancellationToken cancellationToken)
    {
        Debug.Assert(_http is not null);

        var allowedTools = test.Assertions.AllowedToolsOnly
            ? (test.AllowedTools.Count == 0 ? "__none__" : string.Join(",", test.AllowedTools))
            : string.Empty;

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

        using var response = await _http!.PostAsJsonAsync(
            "api/harness/reset", request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task ApplyStateSetupAsync(
        HarnessStateSetup setup,
        CancellationToken cancellationToken)
    {
        if (setup.WikiRoots.Count == 0)
            return;

        Debug.Assert(_http is not null);
        foreach (var root in setup.WikiRoots)
        {
            using var rootResponse = await _http!.PostAsJsonAsync(
                "api/wiki/roots",
                new { name = root.Name, path = (string?)null },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            rootResponse.EnsureSuccessStatusCode();
            using var rootDocument = JsonDocument.Parse(
                await rootResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var rootId = rootDocument.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Wiki setup root response did not include an id.");

            foreach (var page in root.Pages)
            {
                using var pageResponse = await _http.PostAsJsonAsync(
                    $"api/wiki/roots/{Uri.EscapeDataString(rootId)}/pages",
                    new { title = page.Title, folderId = (string?)null, markdown = page.Markdown },
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                pageResponse.EnsureSuccessStatusCode();
            }
        }
    }

    private async Task<LlmUsageSnapshot> GetLlmUsageAsync(CancellationToken cancellationToken)
    {
        Debug.Assert(_http is not null);
        using var response = await _http!.GetAsync("api/harness/llm-usage", cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LlmUsageSnapshot>(JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? new LlmUsageSnapshot();
    }

    private async Task<JsonElement?> CaptureObservedStateAsync(
        IReadOnlyList<HarnessObservationRequest> requests,
        CancellationToken cancellationToken)
    {
        var rootNames = requests
            .Where(request => string.Equals(request.Type, "wiki", StringComparison.OrdinalIgnoreCase))
            .SelectMany(request => request.RootNames)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        if (rootNames.Count == 0)
            return null;

        Debug.Assert(_http is not null);
        using var rootsResponse = await _http!.GetAsync("api/wiki/roots", cancellationToken)
            .ConfigureAwait(false);
        rootsResponse.EnsureSuccessStatusCode();
        using var rootsDocument = JsonDocument.Parse(
            await rootsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

        var snapshots = new List<ObservedWikiRoot>();
        foreach (var root in rootsDocument.RootElement.GetProperty("roots").EnumerateArray())
        {
            var name = root.GetProperty("name").GetString() ?? string.Empty;
            if (!rootNames.Contains(name))
                continue;

            var rootId = root.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Observed wiki root did not include an id.");
            using var treeResponse = await _http.GetAsync(
                $"api/wiki/roots/{Uri.EscapeDataString(rootId)}/tree",
                cancellationToken).ConfigureAwait(false);
            treeResponse.EnsureSuccessStatusCode();
            using var treeDocument = JsonDocument.Parse(
                await treeResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));

            var pages = new List<ObservedWikiPage>();
            foreach (var page in treeDocument.RootElement.GetProperty("pages").EnumerateArray())
            {
                var pageId = page.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Observed wiki page did not include an id.");
                using var pageResponse = await _http.GetAsync(
                    $"api/wiki/pages/{Uri.EscapeDataString(pageId)}",
                    cancellationToken).ConfigureAwait(false);
                pageResponse.EnsureSuccessStatusCode();
                using var pageDocument = JsonDocument.Parse(
                    await pageResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                pages.Add(new ObservedWikiPage(
                    pageDocument.RootElement.GetProperty("page").GetProperty("title").GetString()
                        ?? string.Empty,
                    pageDocument.RootElement.GetProperty("markdown").GetString() ?? string.Empty));
            }

            snapshots.Add(new ObservedWikiRoot(
                name,
                pages.OrderBy(page => page.Title, StringComparer.Ordinal).ToArray()));
        }

        return JsonSerializer.SerializeToElement(
            new ObservedState(
                new ObservedWikiState(
                    snapshots.OrderBy(root => root.Name, StringComparer.Ordinal).ToArray())),
            JsonOptions);
    }

    private async Task<(string FinalText, bool Success, string? Error)>
        RunChatAsync(string userMessage, CancellationToken cancellationToken)
    {
        Debug.Assert(_http is not null);

        // Fresh per-turn event channel so events from previous turns don't
        // leak into this one. Unbounded — we drain as we go.
        _events = Channel.CreateUnbounded<JsonDocument>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // Create a thread, then post the message.
        using var threadResp = await _http!.PostAsJsonAsync(
            "api/threads", new { title = "" }, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
        threadResp.EnsureSuccessStatusCode();
        using var threadDoc = JsonDocument.Parse(
            await threadResp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var threadId = threadDoc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("thread.id missing in response");

        using var msgResp = await _http.PostAsJsonAsync(
            $"api/threads/{threadId}/messages",
            new { text = userMessage },
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        msgResp.EnsureSuccessStatusCode();

        // Drain WS events until chat.turn.complete arrives for our thread.
        // Permission auto-approve happens in ReadWebSocketAsync inline.
        var finalText = string.Empty;
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var doc))
            {
                using var _ = doc;
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(type)) continue;
                if (!root.TryGetProperty("payload", out var payload)) continue;

                if (!string.Equals(type, "chat.turn.complete", StringComparison.Ordinal))
                    continue;

                if (payload.TryGetProperty("threadId", out var tidEl))
                {
                    var tid = tidEl.GetString();
                    if (!string.IsNullOrWhiteSpace(tid) &&
                        !string.Equals(tid, threadId, StringComparison.Ordinal))
                        continue;
                }

                finalText = payload.TryGetProperty("finalText", out var ft)
                    ? ft.GetString() ?? string.Empty
                    : string.Empty;
                var cancelled = payload.TryGetProperty("cancelled", out var c) && c.GetBoolean();
                return (finalText, !cancelled, cancelled ? "cancelled" : null);
            }
        }
        return (finalText, false, "WebSocket closed before turn completed");
    }

    private string ResolveAuditFilePath()
    {
        if (string.IsNullOrWhiteSpace(_sandboxRoot))
            throw new InvalidOperationException("Sandbox not initialised — EnsureRuntimeProcessAsync must run first.");
        return Path.Combine(_sandboxRoot, "logs", "audit.jsonl");
    }

    /// <summary>
    /// Pre-warm: drives one trivial chat through the full pipeline so the
    /// agent JIT/lazy-init cost lands in <c>runtime_warmup</c> instead of
    /// the first real test. Mirrors the v1 adapter's behaviour.
    /// </summary>
    private async Task PrewarmAgentPipelineAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Reset first so allowed_tools is "__none__" — the prewarm
            // chat shouldn't spawn tools.
            var resetRequest = new HarnessResetRequest(
                AllowedTools: "__none__",
                StubOverrides: null,
                ClearMemoryData: true,
                ClearChatHistory: true);
            using var resetResp = await _http!.PostAsJsonAsync(
                "api/harness/reset", resetRequest, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            resetResp.EnsureSuccessStatusCode();

            await RunChatAsync("Reply with only the word ok.", cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort.
        }
    }

    private static string ResolveHybridRuntimeProject()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src", "Thaddeus.Runtime", "Thaddeus.Runtime.csproj");
        if (!File.Exists(path))
            throw new FileNotFoundException("Hybrid runtime project not found.", path);
        return path;
    }

    private static string ResolveHybridRuntimeAssembly()
    {
        var path = Path.Combine(
            Directory.GetCurrentDirectory(),
            "src", "Thaddeus.Runtime", "bin", "Debug", "net10.0", "Thaddeus.Runtime.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException(
                "Hybrid runtime assembly not found — EnsureRuntimeBuilt should run first.",
                path);
        return path;
    }

    private void DisposeProcessAndConnections()
    {
        try
        {
            _wsCts?.Cancel();
            try { _wsReader?.Wait(2_000); } catch { /* ignore */ }
            _ws?.Dispose();
            _http?.Dispose();
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
            _process?.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
        finally
        {
            _ws = null;
            _http = null;
            _process = null;
            _wsCts = null;
            _wsReader = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        DisposeProcessAndConnections();
        try
        {
            if (_sandboxRoot is not null && Directory.Exists(_sandboxRoot))
                Directory.Delete(_sandboxRoot, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
        return ValueTask.CompletedTask;
    }

    private sealed record ObservedState(ObservedWikiState Wiki);
    private sealed record ObservedWikiState(IReadOnlyList<ObservedWikiRoot> Roots);
    private sealed record ObservedWikiRoot(string Name, IReadOnlyList<ObservedWikiPage> Pages);
    private sealed record ObservedWikiPage(string Title, string Markdown);
    private sealed record HybridLockFile(int Port, string Token);
}
