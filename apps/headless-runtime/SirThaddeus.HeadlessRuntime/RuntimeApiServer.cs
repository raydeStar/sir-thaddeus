using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static class RuntimeApiServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task RunAsync(
        int port,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        IAuditLogger audit,
        ApiPermissionGate? permissionGate,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();

        var runs = new ConcurrentDictionary<string, RunState>(StringComparer.OrdinalIgnoreCase);

        if (permissionGate is not null)
        {
            permissionGate.Requested += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(RuntimeEventTypes.ToolRequested, payload);
                }
            };

            permissionGate.Resolved += (runId, payload) =>
            {
                if (runs.TryGetValue(runId, out var run))
                {
                    run.Append(
                        payload.Approved ? RuntimeEventTypes.ToolApproved : RuntimeEventTypes.ToolDenied,
                        payload);
                }
            };
        }

        app.MapGet("/api/health", () =>
        {
            return new HealthResponse(
                Status: "ok",
                Version: typeof(RuntimeApiServer).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                Runtime: "headless-runtime",
                UtcNow: DateTimeOffset.UtcNow);
        });

        app.MapGet("/api/audit", async (int? take, CancellationToken ct) =>
        {
            if (audit is not JsonLineAuditLogger logger)
            {
                return Results.Json(Array.Empty<AuditEntryDto>(), JsonOptions);
            }

            var max = Math.Clamp(take ?? 200, 1, 1000);
            var events = await logger.ReadTailAsync(max, ct);
            var dtos = events.Select((evt, index) => new AuditEntryDto(
                Id: $"{evt.Timestamp.ToUnixTimeMilliseconds()}-{index}",
                Category: evt.Action,
                Message: BuildAuditMessage(evt),
                TimestampUtc: evt.Timestamp,
                CorrelationId: evt.PermissionTokenId,
                MetadataJson: evt.Details is null ? null : JsonSerializer.Serialize(evt.Details, JsonOptions)))
                .ToArray();

            return Results.Json(dtos, JsonOptions);
        });

        app.MapPost("/api/chat", (ChatRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return Results.BadRequest("Prompt is required.");
            }

            var runId = $"run_{Guid.NewGuid():N}"[..16];
            var state = new RunState(runId);
            runs[runId] = state;

            _ = Task.Run(async () =>
            {
                using var runContext = RunExecutionContext.Enter(runId);
                try
                {
                    var orchestrator = buildOrchestrator(getSettings());
                    var response = await orchestrator.ProcessAsync(request.Prompt, state.CancellationToken);
                    state.Append(RuntimeEventTypes.TokenDelta, new TokenDeltaPayload(response.Text, 0));
                    state.Append(RuntimeEventTypes.RunCompleted, new RunCompletedPayload(response.Text, 0));
                }
                catch (OperationCanceledException)
                {
                    state.Append(RuntimeEventTypes.RunFailed, new RunFailedPayload("Cancelled", true));
                }
                catch (Exception ex)
                {
                    state.Append(RuntimeEventTypes.RunFailed, new RunFailedPayload(ex.Message, false));
                }
                finally
                {
                    state.Complete();
                }
            }, CancellationToken.None);

            return Results.Json(new ChatStartResponse(runId, DateTimeOffset.UtcNow), JsonOptions);
        });

        app.MapPost("/api/runs/{runId}/cancel", (string runId) =>
        {
            if (!runs.TryGetValue(runId, out var state))
            {
                return Results.NotFound();
            }

            state.Cancel();
            return Results.Json(new CancelRunResponse(runId, true), JsonOptions);
        });

        app.MapPost("/api/permissions/{requestId}/decision", (string requestId, PermissionDecisionRequest request) =>
        {
            if (permissionGate is null)
            {
                return Results.NotFound();
            }

            var applied = permissionGate.TryApplyDecision(requestId, request.Approved);
            return Results.Json(new PermissionDecisionResponse(requestId, applied), JsonOptions);
        });

        app.MapGet("/api/runs/{runId}/events", async (string runId, HttpContext context, CancellationToken ct) =>
        {
            if (!runs.TryGetValue(runId, out var state))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.Connection = "keep-alive";
            context.Response.ContentType = "text/event-stream";

            await foreach (var evt in state.StreamEventsAsync(ct))
            {
                var json = JsonSerializer.Serialize(evt, JsonOptions);
                await context.Response.WriteAsync($"data: {json}\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
            }
        });

        await app.RunAsync(cancellationToken);
    }

    private static string BuildAuditMessage(AuditEvent auditEvent)
    {
        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
        {
            return $"{auditEvent.Action} -> {auditEvent.Target} ({auditEvent.Result})";
        }

        return $"{auditEvent.Action} ({auditEvent.Result})";
    }

    private sealed class RunState
    {
        private readonly object _gate = new();
        private readonly List<RuntimeEventEnvelope> _history = [];
        private readonly List<ChannelWriter<RuntimeEventEnvelope>> _subscribers = [];
        private readonly CancellationTokenSource _cancellation = new();
        private bool _completed;

        public RunState(string runId)
        {
            RunId = runId;
        }

        public string RunId { get; }
        public CancellationToken CancellationToken => _cancellation.Token;

        public void Cancel() => _cancellation.Cancel();

        public void Complete()
        {
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                subscribers = [.. _subscribers];
                _subscribers.Clear();
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryComplete();
            }
        }

        public void Append(string eventType, object payload)
        {
            var envelope = new RuntimeEventEnvelope(eventType, RunId, DateTimeOffset.UtcNow, payload);
            List<ChannelWriter<RuntimeEventEnvelope>> subscribers;
            lock (_gate)
            {
                _history.Add(envelope);
                subscribers = [.. _subscribers];
            }

            foreach (var subscriber in subscribers)
            {
                subscriber.TryWrite(envelope);
            }
        }

        public async IAsyncEnumerable<RuntimeEventEnvelope> StreamEventsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ChannelReader<RuntimeEventEnvelope>? reader = null;
            List<RuntimeEventEnvelope> replay;
            ChannelWriter<RuntimeEventEnvelope>? writer = null;

            lock (_gate)
            {
                replay = [.. _history];
                if (!_completed)
                {
                    var channel = Channel.CreateUnbounded<RuntimeEventEnvelope>();
                    writer = channel.Writer;
                    reader = channel.Reader;
                    _subscribers.Add(writer);
                }
            }

            try
            {
                foreach (var evt in replay)
                {
                    yield return evt;
                }

                if (reader is null)
                {
                    yield break;
                }

                await foreach (var evt in reader.ReadAllAsync(cancellationToken))
                {
                    yield return evt;
                }
            }
            finally
            {
                if (writer is not null)
                {
                    lock (_gate)
                    {
                        _subscribers.Remove(writer);
                    }
                }
            }
        }
    }
}

internal sealed class ApiPermissionGate : IToolPermissionGate
{
    private readonly Func<string?> _currentRunIdAccessor;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private volatile PolicySnapshot _snapshot;

    public ApiPermissionGate(AppSettings initialSettings, Func<string?> currentRunIdAccessor)
    {
        _snapshot = ToolGroupPolicy.BuildSnapshot(initialSettings, isDebugBuild: false);
        _currentRunIdAccessor = currentRunIdAccessor;
    }

    public event Action<string, ToolRequestedPayload>? Requested;
    public event Action<string, ToolDecisionPayload>? Resolved;

    public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
    {
        var canonical = AuditedMcpToolClient.Canonicalize(toolName);
        var group = ToolGroupPolicy.ResolveGroup(canonical);
        var policy = ToolGroupPolicy.ResolveEffectivePolicy(group, _snapshot);

        if (policy == "off")
        {
            return Task.FromResult(ToolPermissionResult.Deny("Disabled in settings"));
        }

        if (policy == "always" || group == "meta")
        {
            return Task.FromResult(ToolPermissionResult.NotRequired());
        }

        return WaitForDecisionAsync(canonical, argumentsJson, ct);
    }

    public bool TryApplyDecision(string requestId, bool approved)
    {
        if (_pending.TryRemove(requestId, out var tcs))
        {
            tcs.TrySetResult(approved);
            return true;
        }

        return false;
    }

    private async Task<ToolPermissionResult> WaitForDecisionAsync(
        string canonicalToolName,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var runId = _currentRunIdAccessor() ?? "unknown";
        var reason = ToolGroupPolicy.BuildRedactedPurpose(canonicalToolName, argumentsJson);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        Requested?.Invoke(runId, new ToolRequestedPayload(
            RequestId: requestId,
            ToolName: canonicalToolName,
            Reason: reason,
            ArgumentsJson: argumentsJson));

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

        bool approved;
        try
        {
            approved = await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(requestId, out _);
            Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, false));
            return ToolPermissionResult.Deny("Cancelled");
        }

        Resolved?.Invoke(runId, new ToolDecisionPayload(requestId, canonicalToolName, approved));
        return approved
            ? ToolPermissionResult.Grant()
            : ToolPermissionResult.Deny("Denied by user");
    }
}

internal static class RunExecutionContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? CurrentRunId => Current.Value;

    public static IDisposable Enter(string runId)
    {
        var previous = Current.Value;
        Current.Value = runId;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;

        public Scope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            Current.Value = _previous;
        }
    }
}
