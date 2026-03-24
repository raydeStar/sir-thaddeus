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

namespace SirThaddeus.Harness.Execution;

/// <summary>
/// Black-box harness client that starts the real headless runtime server,
/// drives the public /api/chat endpoint, auto-approves permission prompts,
/// and reconstructs tool calls from runtime audit events.
///
/// This is the closest test path to the actual UI/runtime behavior.
/// </summary>
internal sealed class HeadlessRuntimeHarnessClient : IAsyncDisposable
{
    private readonly AppSettings _baseSettings;
    private readonly int _port;
    private readonly Uri _baseUri;
    private readonly HttpClient _http;
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    private Process? _process;
    private HarnessRuntimeSandbox? _sandbox;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public HeadlessRuntimeHarnessClient(AppSettings settings)
    {
        _baseSettings = settings ?? throw new ArgumentNullException(nameof(settings));
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

    internal async Task<HeadlessExecutionResult> ExecuteAsync(
        HarnessTestCase test,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(test);

        await EnsureFreshRuntimeAsync(test, cancellationToken);

        var runtimeProject = ResolveHeadlessRuntimeProject();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{runtimeProject}\" -- --server --tools --port {_port}",
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in _sandbox!.Environment)
            startInfo.Environment[pair.Key] = pair.Value;

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
        await WaitForSearxngReadyAsync(cancellationToken);

        await ClearSessionAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(test.PersonalityId))
        {
            await SetActivePersonalityAsync(test.PersonalityId!, cancellationToken);
        }

        var auditBaseline = await GetAuditEntriesAsync(cancellationToken);
        var auditCaptureStart = DateTimeOffset.UtcNow;
        var startResponse = await PostChatAsync(test.UserMessage, cancellationToken);
        var runOutcome = await ReadRunToCompletionAsync(startResponse.RunId, cancellationToken);
        var auditEntries = FilterNewAuditEntries(
            auditCaptureStart,
            auditBaseline,
            await GetAuditEntriesAsync(cancellationToken));

        var (toolCalls, toolTurns, steps) = BuildToolTraceFromAudit(auditEntries);

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
            LlmRoundTrips = 0
        };

        return new HeadlessExecutionResult
        {
            Response = response,
            Steps = finalSteps,
            ToolTurns = toolTurns
        };
    }

    private async Task EnsureFreshRuntimeAsync(HarnessTestCase test, CancellationToken cancellationToken)
    {
        DisposeRuntimeProcess();
        _sandbox?.Dispose();
        _sandbox = HarnessRuntimeSandbox.Create(_baseSettings, test);

        lock (_stdout)
            _stdout.Clear();
        lock (_stderr)
            _stderr.Clear();

        await InitializeAsync(cancellationToken);
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

    private async Task<(bool Success, string FinalText, string? Error)> ReadRunToCompletionAsync(
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
                    return (true, finalText ?? string.Empty, null);
                case RuntimeEventTypes.RunFailed:
                    error = payload.TryGetProperty("error", out var errorProp)
                        ? errorProp.GetString()
                        : "Unknown headless runtime failure.";
                    return (false, error ?? "Unknown headless runtime failure.", error);
            }
        }

        return (false, finalText ?? "Headless runtime event stream ended before completion.", "event_stream_incomplete");
    }

    private async Task<IReadOnlyList<AuditEntryDto>> GetAuditEntriesAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync("api/audit?take=500", cancellationToken);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<AuditEntryDto[]>(JsonOptions, cancellationToken);
        return payload ?? [];
    }

    private static (IReadOnlyList<ToolCallRecord> ToolCalls, IReadOnlyList<RecordedToolTurn> ToolTurns, IReadOnlyList<TraceStep> Steps)
        BuildToolTraceFromAudit(IReadOnlyList<AuditEntryDto> auditEntries)
    {
        var starts = new Dictionary<string, (string ToolName, string Arguments, DateTimeOffset Timestamp)>(StringComparer.OrdinalIgnoreCase);
        var toolCalls = new List<ToolCallRecord>();
        var toolTurns = new List<RecordedToolTurn>();
        var steps = new List<TraceStep>();
        var stepIndex = 0;
        var toolTurnIndex = 0;

        foreach (var entry in auditEntries.OrderBy(e => e.TimestampUtc))
        {
            if (string.Equals(entry.Category, "MCP_TOOL_CALL_START", StringComparison.OrdinalIgnoreCase))
            {
                var meta = ParseMetadata(entry.MetadataJson);
                var requestId = GetString(meta, "request_id");
                var toolName = GetString(meta, "tool_name_canonical") ?? "unknown";
                var arguments = GetString(meta, "input_summary") ?? "{}";
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    starts[requestId] = (toolName, arguments, entry.TimestampUtc);
                }

                steps.Add(new TraceStep
                {
                    StepIndex = ++stepIndex,
                    StepType = "tool_call",
                    CallId = requestId,
                    ToolName = toolName,
                    Arguments = arguments,
                    StartedAt = entry.TimestampUtc
                });
            }
            else if (string.Equals(entry.Category, "MCP_TOOL_CALL_END", StringComparison.OrdinalIgnoreCase))
            {
                var meta = ParseMetadata(entry.MetadataJson);
                var requestId = GetString(meta, "request_id");
                var errorMessage = GetString(meta, "error_message");
                var outputSummary = GetString(meta, "output_summary") ?? string.Empty;
                var success = entry.Message.Contains("(ok)", StringComparison.OrdinalIgnoreCase);

                starts.TryGetValue(requestId ?? string.Empty, out var start);
                var toolName = start.ToolName ?? GetString(meta, "tool_name_canonical") ?? "unknown";
                var arguments = start.Arguments ?? GetString(meta, "input_summary") ?? "{}";
                var resultText = success ? outputSummary : (errorMessage ?? outputSummary);

                toolCalls.Add(new ToolCallRecord
                {
                    ToolName = toolName,
                    Arguments = arguments,
                    Result = resultText,
                    Success = success
                });

                toolTurns.Add(new RecordedToolTurn
                {
                    Index = toolTurnIndex++,
                    ToolName = toolName,
                    ArgumentsJson = arguments,
                    ResultText = resultText,
                    Success = success
                });

                steps.Add(new TraceStep
                {
                    StepIndex = ++stepIndex,
                    StepType = "tool_result",
                    CallId = requestId,
                    ToolName = toolName,
                    Arguments = arguments,
                    StartedAt = start.Timestamp == default ? entry.TimestampUtc : start.Timestamp,
                    EndedAt = entry.TimestampUtc,
                    DurationMs = Math.Max(0, (long)(entry.TimestampUtc - (start.Timestamp == default ? entry.TimestampUtc : start.Timestamp)).TotalMilliseconds),
                    Result = success
                        ? ToolResultPayloads.BuildSuccess(resultText)
                        : ToolResultPayloads.BuildErrorJson("tool_error", resultText, false),
                    Error = success
                        ? null
                        : new TraceError
                        {
                            Code = "tool_error",
                            Message = resultText,
                            Retriable = false
                        }
                });
            }
        }

        return (toolCalls, toolTurns, steps);
    }

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

            await Task.Delay(250, cancellationToken);
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

    private static JsonElement? ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement? element, string propertyName)
    {
        if (element is not JsonElement root || root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => prop.GetRawText()
        };
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