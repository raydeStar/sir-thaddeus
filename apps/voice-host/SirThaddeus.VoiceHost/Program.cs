using System.Reflection;
using System.Text.Json;
using SirThaddeus.VoiceHost;
using SirThaddeus.VoiceHost.Backends;
using SirThaddeus.VoiceHost.Models;

var options = VoiceHostRuntimeOptions.Parse(args);
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Listen(options.BindIp, options.Port);
});

builder.Services.AddSingleton(options);
builder.Services.AddHttpClient<IAsrBackend, AsrProxyBackend>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(90);
});
builder.Services.AddHttpClient<ITtsBackend, TtsProxyBackend>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddSingleton<VoiceBackendSupervisor>();

var app = builder.Build();
var backendSupervisor = app.Services.GetRequiredService<VoiceBackendSupervisor>();
var backendEnsureGate = new object();
Task<BackendSupervisorResult>? backendEnsureTask = null;
var voiceHostInstanceId = Guid.NewGuid().ToString("N");
var voiceHostVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

Task<BackendSupervisorResult> QueueBackendEnsure()
{
    lock (backendEnsureGate)
    {
        if (backendEnsureTask is null || backendEnsureTask.IsCompleted)
        {
            backendEnsureTask = Task.Run(async () =>
            {
                try
                {
                    using var ensureCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
                    ensureCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(5_000, options.BackendStartupTimeoutMs)));
                    return await backendSupervisor.EnsureRunningAsync(ensureCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return BackendSupervisorResult.Failure(
                        "backend_warming_up",
                        "Voice backend startup is still in progress.");
                }
                catch (Exception ex)
                {
                    return BackendSupervisorResult.Failure(
                        "backend_ensure_failed",
                        $"Voice backend supervision failed: {ex.Message}");
                }
            });
        }

        return backendEnsureTask;
    }
}

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (BackendProxyException ex)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status502BadGateway;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        if (context.Response.HasStarted)
            return;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(new { error = "VoiceHost internal error.", detail = ex.Message });
    }
});

app.MapGet("/health", async (
    IAsrBackend asrBackend,
    ITtsBackend ttsBackend) =>
{
    // Trigger backend supervision in the background so /health remains responsive
    // even when cold-start model warmup takes longer than client probe timeouts.
    var ensureTask = QueueBackendEnsure();
    var backendEnsure = backendSupervisor.LastResult;
    if (ensureTask.IsCompleted)
    {
        backendEnsure = await ensureTask;
    }
    else
    {
        backendEnsure = BackendSupervisorResult.Failure(
            "backend_warming_up",
            "Voice backend startup is still in progress.");
    }

    BackendReadiness asrState;
    BackendReadiness ttsState;
    try
    {
        using var readinessCts = CancellationTokenSource.CreateLinkedTokenSource(app.Lifetime.ApplicationStopping);
        readinessCts.CancelAfter(TimeSpan.FromSeconds(2));

        var asrStateTask = asrBackend.GetReadinessAsync(readinessCts.Token);
        var ttsStateTask = ttsBackend.GetReadinessAsync(readinessCts.Token);
        await Task.WhenAll(asrStateTask, ttsStateTask);

        asrState = asrStateTask.Result;
        ttsState = ttsStateTask.Result;
    }
    catch (Exception ex)
    {
        var detail = string.IsNullOrWhiteSpace(ex.Message)
            ? "Readiness probe failed."
            : ex.Message;
        asrState = BackendReadiness.NotReady(detail);
        ttsState = BackendReadiness.NotReady(detail);
    }

    var asrStatus = EnsureEngineStatus(asrState, "asr");
    var ttsStatus = EnsureEngineStatus(ttsState, "tts");
    var asrReady = asrStatus.Ready;
    var ttsReady = ttsStatus.Ready;
    var ready = asrReady && ttsReady;

    var errorCode = "";
    var message = "";
    if (!ready)
    {
        var readinessDetail = $"ASR: {asrStatus.Details.LastError}; TTS: {ttsStatus.Details.LastError}";
        if (!backendEnsure.Success)
        {
            errorCode = backendEnsure.ErrorCode;
            message = string.IsNullOrWhiteSpace(backendEnsure.Message)
                ? readinessDetail
                : $"{backendEnsure.Message} {readinessDetail}";
        }
        else if (!asrReady && !ttsReady)
        {
            errorCode = "asr_tts_not_ready";
            message = readinessDetail;
        }
        else if (!asrReady)
        {
            errorCode = "asr_not_ready";
            message = $"ASR backend not ready. {asrState.Detail}";
        }
        else
        {
            errorCode = "tts_not_ready";
            message = $"TTS backend not ready. {ttsState.Detail}";
        }
    }

    return Results.Json(new
    {
        schemaVersion = 1,
        instanceId = voiceHostInstanceId,
        timestampUtc = DateTimeOffset.UtcNow.ToString("O"),
        status = ready ? "ok" : "loading",
        ready,
        asrReady,
        ttsReady,
        version = voiceHostVersion,
        errorCode,
        message,
        asr = asrStatus,
        tts = ttsStatus
    });
});

app.MapPost("/asr", async (
    HttpRequest request,
    HttpContext httpContext,
    IAsrBackend asrBackend,
    VoiceBackendSupervisor backendSupervisor,
    CancellationToken cancellationToken) =>
{
    var requestId = ResolveRequestId(request.Headers["X-Request-Id"].ToString(), "");
    httpContext.Response.Headers["X-Request-Id"] = requestId;

    var ensure = await backendSupervisor.EnsureRunningAsync(cancellationToken);
    if (!ensure.Success)
    {
        return Results.Json(new
        {
            error = "Voice backend unavailable.",
            errorCode = ensure.ErrorCode,
            message = ensure.Message,
            requestId
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (!request.HasFormContentType)
        return Results.BadRequest(new
        {
            error = "Expected multipart/form-data with 'audio' file.",
            requestId
        });

    var form = await request.ReadFormAsync(cancellationToken);
    var audioFile = form.Files.GetFile("audio");
    if (audioFile is null || audioFile.Length == 0)
        return Results.BadRequest(new
        {
            error = "Missing required multipart field 'audio'.",
            requestId
        });

    var sessionId = form.TryGetValue("sessionId", out var values)
        ? values.ToString()
        : null;
    if (form.TryGetValue("requestId", out var requestIdValues))
        requestId = ResolveRequestId(requestIdValues.ToString(), requestId);
    httpContext.Response.Headers["X-Request-Id"] = requestId;

    var transcript = await asrBackend.TranscribeAsync(audioFile, sessionId, requestId, cancellationToken);
    return Results.Json(new { text = transcript, requestId });
});

app.MapPost("/tts", async (
    VoiceHostTtsRequest payload,
    HttpContext httpContext,
    ITtsBackend ttsBackend,
    VoiceBackendSupervisor backendSupervisor,
    CancellationToken cancellationToken) =>
{
    var requestId = ResolveRequestId(payload.RequestId, httpContext.Request.Headers["X-Request-Id"].ToString());
    httpContext.Response.Headers["X-Request-Id"] = requestId;

    var ensure = await backendSupervisor.EnsureRunningAsync(cancellationToken);
    if (!ensure.Success)
    {
        return Results.Json(new
        {
            error = "Voice backend unavailable.",
            errorCode = ensure.ErrorCode,
            message = ensure.Message,
            requestId
        }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    if (string.IsNullOrWhiteSpace(payload.Text))
        return Results.BadRequest(new
        {
            error = "Field 'text' is required.",
            requestId
        });

    var normalizedPayload = payload with
    {
        RequestId = requestId,
        Engine = string.IsNullOrWhiteSpace(payload.Engine) ? options.TtsEngine : payload.Engine.Trim(),
        ModelId = string.IsNullOrWhiteSpace(payload.ModelId) ? options.TtsModelId : payload.ModelId.Trim(),
        VoiceId = ResolveVoiceId(payload, options.TtsVoiceId),
        Voice = string.IsNullOrWhiteSpace(payload.Voice) ? "default" : payload.Voice.Trim(),
        Format = string.IsNullOrWhiteSpace(payload.Format) ? "pcm_s16le" : payload.Format.Trim(),
        SampleRate = payload.SampleRate <= 0 ? 24_000 : payload.SampleRate
    };

    if (string.Equals(normalizedPayload.Engine, "kokoro", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrWhiteSpace(normalizedPayload.VoiceId))
    {
        return Results.Json(new
        {
            error = "Kokoro requires a non-empty voiceId.",
            errorCode = "tts_voice_missing",
            requestId
        }, statusCode: StatusCodes.Status400BadRequest);
    }

    await ttsBackend.StreamSynthesisAsync(normalizedPayload, httpContext.Response, cancellationToken);
    return Results.Empty;
});

static BackendEngineStatus EnsureEngineStatus(BackendReadiness readiness, string fallbackEngine)
{
    if (readiness.EngineStatus is not null)
        return readiness.EngineStatus;

    var missing = string.IsNullOrWhiteSpace(readiness.Detail)
        ? Array.Empty<string>()
        : new[] { readiness.Detail };

    return new BackendEngineStatus(
        SchemaVersion: 1,
        Ready: readiness.Ready,
        Engine: fallbackEngine,
        EngineVersion: "",
        ModelId: "",
        InstanceId: "",
        TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
        Details: new BackendEngineStatusDetails(
            Installed: readiness.Ready,
            Missing: missing,
            LastError: readiness.Ready ? "" : readiness.Detail));
}

static string ResolveRequestId(params string[] candidates)
{
    foreach (var candidate in candidates)
    {
        if (!string.IsNullOrWhiteSpace(candidate))
            return candidate.Trim();
    }

    return Guid.NewGuid().ToString("N");
}

static string ResolveVoiceId(VoiceHostTtsRequest payload, string configuredVoiceId)
{
    if (!string.IsNullOrWhiteSpace(payload.VoiceId))
        return payload.VoiceId.Trim();

    if (!string.IsNullOrWhiteSpace(payload.Voice) &&
        !string.Equals(payload.Voice, "default", StringComparison.OrdinalIgnoreCase))
    {
        return payload.Voice.Trim();
    }

    return string.IsNullOrWhiteSpace(configuredVoiceId)
        ? ""
        : configuredVoiceId.Trim();
}

app.MapGet("/", () => Results.Json(new
{
    service = "voicehost",
    mode = options.BackendMode,
    bind = options.BindIp.ToString(),
    port = options.Port
}));

app.Run();
