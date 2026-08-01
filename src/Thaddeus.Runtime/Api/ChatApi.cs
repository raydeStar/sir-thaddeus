using System.Text.Json;
using System.Text.Json.Serialization;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.State;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Chat REST endpoints (Phase 3.2). Read/write operations on threads + messages.
/// Streaming assistant turns ride on the existing /ws channel (Phase 3.3).
/// </summary>
public static class ChatApi
{
    /// <summary>Registers <c>/api/threads</c> and <c>/api/threads/{id}/...</c> endpoints.</summary>
    public static void MapChatApi(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/threads", async (IThreadStore store, CancellationToken ct) =>
        {
            var threads = await store.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new ThreadListResponse(threads.Select(ThreadSummary.From).ToArray()),
                ChatJsonContext.Default.ThreadListResponse);
        })
            .WithName("ListThreads");

        app.MapPost("/api/threads", async (CreateThreadRequest? req, IThreadStore store, CancellationToken ct) =>
        {
            var title = req?.Title ?? string.Empty;
            var thread = await store.CreateAsync(title, ct).ConfigureAwait(false);
            return Results.Json(thread, ChatJsonContext.Default.ChatThread, statusCode: StatusCodes.Status201Created);
        })
            .WithName("CreateThread");

        app.MapGet("/api/threads/{id}", async (string id, IThreadStore store, CancellationToken ct) =>
        {
            var thread = await store.GetAsync(id, ct).ConfigureAwait(false);
            return thread is null
                ? Results.NotFound()
                : Results.Json(thread, ChatJsonContext.Default.ChatThread);
        })
            .WithName("GetThread");

        app.MapDelete("/api/threads/{id}", async (string id, IThreadStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return ok ? Results.NoContent() : Results.NotFound();
        })
            .WithName("DeleteThread");

        app.MapPatch("/api/threads/{id}", async (string id, PatchThreadRequest? req, IThreadStore store,
            CancellationToken ct) =>
        {
            if (req is null) return Results.BadRequest(new { error = "body required" });
            if (req.Title is null && !req.Pinned.HasValue)
                return Results.BadRequest(new { error = "patch body must set at least one of title, pinned" });

            ChatThread? current = null;
            if (req.Title is not null)
            {
                current = await store.RenameAsync(id, req.Title, ct).ConfigureAwait(false);
                if (current is null) return Results.NotFound();
            }
            if (req.Pinned.HasValue)
            {
                current = await store.SetPinnedAsync(id, req.Pinned.Value, ct).ConfigureAwait(false);
                if (current is null) return Results.NotFound();
            }
            return Results.Json(current!, ChatJsonContext.Default.ChatThread);
        })
            .WithName("PatchThread");

        app.MapPost("/api/threads/{id}/messages",
            async (string id, AppendMessageRequest? req, IThreadStore store, IAssistant assistant,
                WikiChatContextService wikiContext,
                RuntimeStateMachine machine, IActivityLog activity, ILoggerFactory loggerFactory,
                TurnRunCoordinator runs,
                IHostApplicationLifetime lifetime,
                CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "text is required" });

            var turnLogger = loggerFactory.CreateLogger("ChatApi.AssistantTurn");
            var latencyTrace = RoutingLatencyTrace.Start(id, turnLogger);

            WikiChatContextPrompt prompt;
            SirThaddeus.Agent.Pipeline.WikiMutationTarget? mutationTarget;
            try
            {
                prompt = await wikiContext.BuildAsync(req.Text, req.WikiContext, ct).ConfigureAwait(false);
                mutationTarget = await wikiContext.ResolveMutationTargetAsync(
                    req.WikiMutationTarget,
                    ct).ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }

            var userText = req.Text.Trim();
            var message = new ChatMessage(
                Id: NewMessageId(),
                Role: ChatRole.User,
                Text: userText,
                CreatedAt: DateTimeOffset.UtcNow);
            if (latencyTrace is not null)
            {
                latencyTrace.UserMessageId = message.Id;
                latencyTrace.LocalWikiEvidencePacketActivated = prompt.CompactEvidenceActivated;
            }

            try
            {
                var updated = await store.AppendMessageAsync(id, message, ct).ConfigureAwait(false);
                RoutingLatencyTrace.Mark(turnLogger, latencyTrace, "user_message_persisted");

                // Auto-title: if the thread is still stamped with the placeholder and
                // this is the first user turn, derive a short title from the message.
                // Users can still rename explicitly via PATCH /api/threads/{id}; we
                // only ever overwrite the placeholder, never a user-picked title.
                if (string.Equals(updated.Title, ChatThreadDefaults.UntitledTitle, StringComparison.Ordinal)
                    && updated.Messages.Count(m => m.Role == ChatRole.User) == 1)
                {
                    var derived = ChatThreadDefaults.DeriveTitleFromFirstMessage(message.Text);
                    if (derived.Length > 0)
                    {
                        var renamed = await store.RenameAsync(id, derived, ct).ConfigureAwait(false);
                        if (renamed is not null) updated = renamed;
                    }
                }

                // Project chat lifecycle onto the runtime state machine so the shell's
                // status badge animates Idle -> Thinking -> Idle for typed turns.
                // Illegal transitions (e.g. user sends a second message mid-reply) are
                // logged and discarded by the machine; chat persistence still succeeds.
                machine.TryTransition(StateTrigger.UserTextSubmitted);

                // Record an activity-log entry for this turn so it shows up in the
                // Activity UI immediately. The stub assistant updates it on completion.
                var activityEntry = activity.Append(new ActivityEntry(
                    Id: InMemoryActivityLog.NewId(),
                    Kind: ActivityKind.ChatTurn,
                    Summary: SummariseUserText(message.Text),
                    Status: ActivityStatus.Running,
                    StartedAt: message.CreatedAt,
                    CompletedAt: null,
                    ThreadId: id,
                    Detail: null));

                var plan = WorkPlanBuilder.TryBuild(userText);
                var run = runs.Create(id, message.Id, lifetime.ApplicationStopping, plan);
                RoutingLatencyTrace.Mark(turnLogger, latencyTrace, "assistant_task_queued");
                StartAssistantTurn(
                    id, prompt.Prompt, assistant, machine, activity, loggerFactory,
                    runs, run.RunId, activityEntry,
                    new AssistantTurnOptions(req.EphemeralMemory, mutationTarget),
                    latencyTrace);

                return Results.Json(
                    new AppendMessageResponse(message, updated, run),
                    ChatJsonContext.Default.AppendMessageResponse,
                    statusCode: StatusCodes.Status201Created);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        })
            .WithName("AppendMessage");

        app.MapPost("/api/threads/{id}/messages/retry",
            async (string id, IThreadStore store, IAssistant assistant,
                RuntimeStateMachine machine, IActivityLog activity, ILoggerFactory loggerFactory,
                TurnRunCoordinator runs,
                IHostApplicationLifetime lifetime,
                HttpContext ctx,
                CancellationToken ct) =>
        {
            RetryMessageRequest? retryRequest = null;
            if (ctx.Request.ContentLength is > 0)
            {
                try
                {
                    retryRequest = await JsonSerializer.DeserializeAsync(
                        ctx.Request.Body,
                        ChatJsonContext.Default.RetryMessageRequest,
                        ct).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "invalid retry body" });
                }
                if (retryRequest is null)
                    return Results.BadRequest(new { error = "invalid retry body" });
            }
            var thread = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (thread is null) return Results.NotFound();
            if (thread.Messages.Count == 0)
                return Results.BadRequest(new { error = "no assistant response to retry" });

            var latest = thread.Messages[^1];
            if (latest.Role != ChatRole.Assistant || string.IsNullOrWhiteSpace(latest.Text))
                return Results.BadRequest(new { error = "latest message is not an assistant response" });

            var userMessage = thread.Messages
                .Take(thread.Messages.Count - 1)
                .LastOrDefault(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
            if (userMessage is null)
                return Results.BadRequest(new { error = "no user message to retry from" });

            var updated = await store.RemoveMessageAsync(id, latest.Id, ct).ConfigureAwait(false);
            if (updated is null) return Results.NotFound();

            machine.TryTransition(StateTrigger.UserTextSubmitted);
            var now = DateTimeOffset.UtcNow;
            var activityEntry = activity.Append(new ActivityEntry(
                Id: InMemoryActivityLog.NewId(),
                Kind: ActivityKind.ChatTurn,
                Summary: "Retry: " + SummariseUserText(userMessage.Text),
                Status: ActivityStatus.Running,
                StartedAt: now,
                CompletedAt: null,
                ThreadId: id,
                Detail: null));

            var plan = WorkPlanBuilder.TryBuild(userMessage.Text);
            var run = runs.Create(id, userMessage.Id, lifetime.ApplicationStopping, plan);
            StartAssistantTurn(
                id,
                userMessage.Text,
                assistant,
                machine,
                activity,
                loggerFactory,
                runs,
                run.RunId,
                activityEntry,
                new AssistantTurnOptions(retryRequest?.EphemeralMemory ?? false));

            return Results.Json(
                new RetryAssistantResponse(updated, run),
                ChatJsonContext.Default.RetryAssistantResponse,
                statusCode: StatusCodes.Status202Accepted);
        })
            .WithName("RetryLatestAssistantResponse");

        app.MapGet("/api/runs", (string? threadId, TurnRunCoordinator runs) =>
            Results.Json(
                new TurnRunListResponse(runs.List(threadId)),
                ChatJsonContext.Default.TurnRunListResponse))
            .WithName("ListTurnRuns");

        app.MapGet("/api/runs/{runId}", (string runId, TurnRunCoordinator runs) =>
        {
            var run = runs.Get(runId);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("GetTurnRun");

        app.MapPost("/api/runs/{runId}/pause", (string runId, TurnRunCoordinator runs) =>
        {
            var run = runs.Pause(runId);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("PauseTurnRun");

        app.MapPost("/api/runs/{runId}/resume", (string runId, TurnRunCoordinator runs) =>
        {
            var run = runs.Resume(runId);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("ResumeTurnRun");

        app.MapPost("/api/runs/{runId}/cancel", (string runId, TurnRunCoordinator runs) =>
        {
            var run = runs.Cancel(runId);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("CancelTurnRun");

        app.MapPost("/api/runs/{runId}/take-over", (string runId, TurnRunCoordinator runs) =>
        {
            var run = runs.TakeOver(runId);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("TakeOverTurnRun");

        app.MapPost("/api/runs/{runId}/redirect", (
            string runId,
            RedirectTurnRunRequest? request,
            TurnRunCoordinator runs) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Instruction))
                return Results.BadRequest(new { error = "instruction is required" });
            var run = runs.Redirect(runId, request.Instruction);
            return run is null
                ? Results.NotFound()
                : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
        })
            .WithName("RedirectTurnRun");

        app.MapPost("/api/runs/{runId}/plan/approve", (
            string runId,
            ApproveWorkPlanRequest? request,
            TurnRunCoordinator runs) =>
        {
            if (request is null || request.ExpectedVersion < 1)
                return Results.BadRequest(new { error = "expectedVersion must be positive" });
            try
            {
                var run = runs.ApprovePlan(runId, request.ExpectedVersion);
                return run is null
                    ? Results.NotFound()
                    : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
            }
            catch (TurnRunConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
            .WithName("ApproveWorkPlan");

        app.MapPut("/api/runs/{runId}/plan", (
            string runId,
            EditWorkPlanRequest? request,
            TurnRunCoordinator runs) =>
        {
            if (request is null || request.ExpectedVersion < 1)
                return Results.BadRequest(new { error = "expectedVersion and steps are required" });
            try
            {
                if (!TryParseEditedPlanSteps(request.Steps, out var parsedSteps, out var parseError))
                    return Results.BadRequest(new { error = parseError });
                var run = runs.EditPlan(runId, request.ExpectedVersion, parsedSteps);
                return run is null
                    ? Results.NotFound()
                    : Results.Json(run, ChatJsonContext.Default.TurnRunSnapshot);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (TurnRunConflictException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        })
            .WithName("EditWorkPlan");
    }

    internal static bool TryParseEditedPlanSteps(
        IReadOnlyList<EditWorkPlanStepRequest>? requests,
        out IReadOnlyList<WorkPlanStep> steps,
        out string? error)
    {
        var parsed = new List<WorkPlanStep>();
        if (requests is null)
        {
            steps = parsed;
            error = "steps are required";
            return false;
        }

        foreach (var request in requests)
        {
            if (!Enum.TryParse<WorkPlanCapability>(request.Capability, ignoreCase: true, out var capability) ||
                !Enum.IsDefined(capability))
            {
                steps = parsed;
                error = $"Unsupported plan capability '{request.Capability}'.";
                return false;
            }
            if (!Enum.TryParse<WorkPlanRisk>(request.Risk, ignoreCase: true, out var risk) ||
                !Enum.IsDefined(risk))
            {
                steps = parsed;
                error = $"Unsupported plan risk '{request.Risk}'.";
                return false;
            }

            parsed.Add(new WorkPlanStep(
                request.StepId,
                request.Label,
                capability,
                risk,
                request.RequiresPermission,
                WorkPlanStepStatus.Pending));
        }

        steps = parsed;
        error = null;
        return true;
    }

    private static void StartAssistantTurn(
        string threadId,
        string prompt,
        IAssistant assistant,
        RuntimeStateMachine machine,
        IActivityLog activity,
        ILoggerFactory loggerFactory,
        TurnRunCoordinator runs,
        string runId,
        ActivityEntry activityEntry,
        AssistantTurnOptions turnOptions,
        RoutingLatencyTrace.TraceState? latencyTrace = null)
    {
        _ = Task.Run(async () =>
        {
            var log = loggerFactory.CreateLogger("ChatApi.AssistantTurn");
            using var latencyActivation = RoutingLatencyTrace.Activate(latencyTrace);
            RoutingLatencyTrace.Mark(log, latencyTrace, "assistant_task_start");
            var status = ActivityStatus.Ok;
            string? detail = null;
            var failed = false;
            var bgCt = runs.GetCancellationToken(runId);
            using var runActivation = runs.Activate(runId);
            try
            {
                var approvedPlan = await runs.WaitForApprovalAsync(runId, bgCt).ConfigureAwait(false);
                var effectivePrompt = approvedPlan is null
                    ? prompt
                    : WorkPlanBuilder.ComposeApprovedPrompt(prompt, approvedPlan);
                var reply = await assistant.RespondAsync(threadId, effectivePrompt, turnOptions, bgCt)
                    .ConfigureAwait(false);
                RoutingLatencyTrace.Mark(log, latencyTrace, "assistant_response_complete");
                detail = reply.Text.Length > 280 ? reply.Text[..280] + "…" : reply.Text;
            }
            catch (OperationCanceledException) when (bgCt.IsCancellationRequested)
            {
                status = ActivityStatus.Failed;
                detail = "cancelled";
                log.LogInformation("assistant.cancelled thread={ThreadId} run={RunId}", threadId, runId);
            }
            catch (Exception ex)
            {
                failed = true;
                status = ActivityStatus.Failed;
                detail = ex.Message;
                log.LogWarning(ex, "assistant.respond_failed thread={ThreadId} run={RunId}", threadId, runId);
            }
            finally
            {
                if (failed)
                    runs.Fail(runId, detail ?? "Assistant turn failed");
                else
                    runs.Complete(runId, bgCt.IsCancellationRequested, detail);
                RoutingLatencyTrace.Mark(log, latencyTrace, "ui_completion_event");
                machine.TryTransition(StateTrigger.PlanTextOnly);
                activity.Update(
                    activityEntry.Id,
                    status: status,
                    completedAt: DateTimeOffset.UtcNow,
                    detail: detail);
            }
        });
    }

    private static string NewMessageId() =>
        "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant();

    private static string SummariseUserText(string text)
    {
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length > 140 ? single[..140] : single;
    }
}

/// <summary>Body for POST /api/threads.</summary>
public sealed record CreateThreadRequest(string? Title);

/// <summary>Body for PATCH /api/threads/{id}. Both fields are optional.</summary>
public sealed record PatchThreadRequest(string? Title, bool? Pinned);

/// <summary>Body for POST /api/threads/{id}/messages.</summary>
public sealed record AppendMessageRequest(
    string Text,
    WikiChatContextRequest? WikiContext = null,
    WikiMutationTargetRequest? WikiMutationTarget = null,
    bool EphemeralMemory = false);
public sealed record RetryMessageRequest(bool EphemeralMemory = false);

/// <summary>Response for POST /api/threads/{id}/messages.</summary>
public sealed record AppendMessageResponse(
    ChatMessage Message,
    ChatThread Thread,
    TurnRunSnapshot Run);

/// <summary>Response for POST /api/threads/{id}/messages/retry.</summary>
public sealed record RetryAssistantResponse(ChatThread Thread, TurnRunSnapshot Run);

public sealed record TurnRunListResponse(IReadOnlyList<TurnRunSnapshot> Runs);
public sealed record RedirectTurnRunRequest(string Instruction);
public sealed record ApproveWorkPlanRequest(long ExpectedVersion);
public sealed record EditWorkPlanRequest(
    long ExpectedVersion,
    IReadOnlyList<EditWorkPlanStepRequest> Steps);
public sealed record EditWorkPlanStepRequest(
    string StepId,
    string Label,
    string Capability,
    string Risk,
    bool RequiresPermission);

/// <summary>Compact thread summary for the chat list view.</summary>
public sealed record ThreadSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string? LastMessagePreview,
    bool Pinned)
{
    public static ThreadSummary From(ChatThread t)
    {
        var last = t.Messages.Count > 0 ? t.Messages[^1].Text : null;
        var preview = last is null ? null : (last.Length > 140 ? last[..140] : last);
        return new ThreadSummary(t.Id, t.Title, t.CreatedAt, t.UpdatedAt, t.Messages.Count, preview, t.Pinned);
    }
}

/// <summary>Response wrapper for GET /api/threads.</summary>
public sealed record ThreadListResponse(IReadOnlyList<ThreadSummary> Threads);

/// <summary>Source-generated JSON context for chat payloads.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ChatThread))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(ThreadListResponse))]
[JsonSerializable(typeof(ThreadSummary))]
[JsonSerializable(typeof(PatchThreadRequest))]
[JsonSerializable(typeof(CreateThreadRequest))]
[JsonSerializable(typeof(AppendMessageRequest))]
[JsonSerializable(typeof(RetryMessageRequest))]
[JsonSerializable(typeof(WikiChatContextRequest))]
[JsonSerializable(typeof(WikiMutationTargetRequest))]
[JsonSerializable(typeof(WikiChatContextPrompt))]
[JsonSerializable(typeof(WikiChatContextAttachment))]
[JsonSerializable(typeof(WikiChatEvidenceSource))]
[JsonSerializable(typeof(AppendMessageResponse))]
[JsonSerializable(typeof(RetryAssistantResponse))]
[JsonSerializable(typeof(TurnRunSnapshot))]
[JsonSerializable(typeof(TurnRunListResponse))]
[JsonSerializable(typeof(RedirectTurnRunRequest))]
[JsonSerializable(typeof(ApproveWorkPlanRequest))]
[JsonSerializable(typeof(EditWorkPlanRequest))]
[JsonSerializable(typeof(EditWorkPlanStepRequest))]
[JsonSerializable(typeof(WorkPlan))]
[JsonSerializable(typeof(WorkPlanStep))]
public partial class ChatJsonContext : JsonSerializerContext
{
}
