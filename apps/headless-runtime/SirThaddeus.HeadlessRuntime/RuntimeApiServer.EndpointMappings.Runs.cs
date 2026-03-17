using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Workflow;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;

internal static partial class RuntimeApiServer
{
    private static readonly ITaskClassifier WorkflowClassifier = new TaskClassifier();
    private static readonly IChecklistPlanner WorkflowChecklistPlanner = new ChecklistPlanner();
    private static readonly IConfidenceEvaluator WorkflowConfidenceEvaluator = new ConfidenceEvaluator();
    private static readonly IRetryPlanner WorkflowRetryPlanner = new RetryPlanner();
    private static readonly IRetryGateEvaluator WorkflowRetryGateEvaluator = new RetryGateEvaluator();
    private static readonly ICompletionReasonResolver WorkflowCompletionReasonResolver = new CompletionReasonResolver();
    private static readonly IProgressNarrator WorkflowNarrator = new ProgressNarrator();

    private static void MapRunEndpoints(
        WebApplication app,
        ConcurrentDictionary<string, RunState> runs,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        ApiPermissionGate? permissionGate,
        Action<AppSettings> persistSettings,
        IAuditLogger audit)
    {
        app.MapPost("/api/session/clear", () =>
        {
            permissionGate?.ClearSessionGrants();
            return Results.Ok();
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
                    await ExecuteChatRunAsync(
                        request,
                        state,
                        buildOrchestrator,
                        getSettings,
                        audit);
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

            var applied = permissionGate.TryApplyDecision(requestId, request.Approved, request.RememberForSession, request.PersistAsAlways);

            if (applied && request.Approved && request.PersistAsAlways)
            {
                var toolGroup = permissionGate.GetLastResolvedGroup(requestId);
                if (toolGroup is not null)
                {
                    var currentSettings = getSettings();
                    var perms = currentSettings.Mcp.Permissions;
                    var updatedPerms = toolGroup switch
                    {
                        "screen" => perms with { Screen = "always" },
                        "files" => perms with { Files = "always" },
                        "system" => perms with { System = "always" },
                        "web" => perms with { Web = "always" },
                        "memoryRead" => perms with { MemoryRead = "always" },
                        "memoryWrite" => perms with { MemoryWrite = "always" },
                        _ => perms
                    };
                    if (!ReferenceEquals(perms, updatedPerms))
                    {
                        var updatedSettings = currentSettings with
                        {
                            Mcp = currentSettings.Mcp with { Permissions = updatedPerms }
                        };
                        persistSettings(updatedSettings);
                    }
                }
            }

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
    }

    private static async Task ExecuteChatRunAsync(
        ChatRequest request,
        RunState runState,
        Func<AppSettings, AgentOrchestrator> buildOrchestrator,
        Func<AppSettings> getSettings,
        IAuditLogger audit)
    {
        var settings = getSettings();
        var features = settings.WorkflowFeatures;
        var workflowEnabled = features.ChecklistProgressUiEnabled ||
                              features.ConfidenceScoringEnabled ||
                              features.ConstrainedRetryEnabled ||
                              features.TaskRunAuditSnapshotsEnabled;

        var orchestrator = buildOrchestrator(settings);
        if (request.Messages is { Count: > 0 })
        {
            orchestrator.SeedHistory(request.Messages.Select(m => (m.Role, m.Content)));
        }

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? request.SessionId
            : request.ConversationId;

        TaskRunState? workflowState = null;
        if (workflowEnabled)
        {
            workflowState = await InitializeWorkflowStateAsync(request.Prompt, settings, runState.CancellationToken);
            PublishProgressEvent(
                runState,
                "task.started",
                "Workflow run started.",
                true,
                null,
                new Dictionary<string, string>
                {
                    ["complexity"] = workflowState.Envelope.Complexity.ToString(),
                    ["showChecklist"] = workflowState.Envelope.ShowChecklist.ToString()
                });
            if (features.ChecklistProgressUiEnabled &&
                workflowState.Envelope.ShowChecklist)
            {
                UpdateChecklistStep(workflowState, 1, ChecklistItemState.InProgress, "Understanding request");
                PublishChecklist(runState, workflowState);
            }

            await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.TaskStarted, runState.CancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var firstResponse = await orchestrator.ProcessAsync(
            request.Prompt,
            conversationId,
            runState.CancellationToken);

        var selectedResponse = firstResponse;
        var totalRoundTrips = firstResponse.LlmRoundTrips;
        ConfidenceSnapshot? firstConfidence = null;
        ConfidenceSnapshot? selectedConfidence = null;
        CompletionReason? completionReason = null;

        if (workflowState is not null)
        {
            CaptureEvidence(workflowState, firstResponse, "primary");
            workflowState.DraftAnswer = firstResponse.Text;
            workflowState.ToolCallsUsed = firstResponse.ToolCallsMade.Count;

            if (features.ChecklistProgressUiEnabled && workflowState.Envelope.ShowChecklist)
            {
                UpdateChecklistStep(workflowState, 1, ChecklistItemState.Completed, "Request understood");
                UpdateChecklistStep(workflowState, 2, ChecklistItemState.InProgress, "Gathering evidence");
                workflowState.Checklist.CurrentPhase = "Gathering evidence";
                PublishChecklist(runState, workflowState);
            }

            firstConfidence = features.ConfidenceScoringEnabled
                ? WorkflowConfidenceEvaluator.Evaluate(workflowState)
                : new ConfidenceSnapshot { Score = 0.7, Band = "Medium", Summary = "Confidence scoring disabled.", ShouldRetry = false };

            workflowState.LatestConfidence = firstConfidence;
            selectedConfidence = firstConfidence;

            if (features.ChecklistProgressUiEnabled && workflowState.Envelope.ShowChecklist)
            {
                UpdateChecklistStep(workflowState, 2, ChecklistItemState.Completed, "Evidence captured");
                UpdateChecklistStep(workflowState, 3, ChecklistItemState.InProgress, "Comparing findings");
                workflowState.Checklist.CurrentPhase = "Comparing evidence";
                PublishChecklist(runState, workflowState);
            }

            var retryGate = WorkflowRetryGateEvaluator.Evaluate(workflowState, firstConfidence, stopwatch.Elapsed);
            workflowState.LastRetryGateDecision = retryGate;

            if (features.ConstrainedRetryEnabled && retryGate.IsAllowed)
            {
                var retryPlan = await WorkflowRetryPlanner.BuildRetryPlanAsync(workflowState, runState.CancellationToken);
                var retryAction = retryPlan.FirstOrDefault();
                var retryStrategy = retryAction?.RetryStrategy ?? "fallback_retry";

                workflowState.RetriesUsed += 1;
                workflowState.RuntimeState = TaskLifecycleState.Retrying;
                PublishProgressEvent(
                    runState,
                    "retry.started",
                    "Confidence below threshold, starting alternate verification strategy.",
                    true,
                    workflowState.Checklist.Items.FirstOrDefault(i => i.Order == 3)?.Id,
                    new Dictionary<string, string>
                    {
                        ["retry"] = workflowState.RetriesUsed.ToString(),
                        ["reason"] = "confidence_below_threshold",
                        ["strategy"] = retryStrategy
                    });
                await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.RetryStarted, runState.CancellationToken);

                var retryPrompt = BuildRetryPrompt(request.Prompt, firstResponse.Text, retryAction);
                var retryResponse = await orchestrator.ProcessAsync(
                    retryPrompt,
                    conversationId,
                    runState.CancellationToken);

                totalRoundTrips += retryResponse.LlmRoundTrips;

                var retryState = new TaskRunState
                {
                    Envelope = workflowState.Envelope,
                    Checklist = workflowState.Checklist,
                    ToolCallsUsed = workflowState.ToolCallsUsed + retryResponse.ToolCallsMade.Count,
                    RetriesUsed = workflowState.RetriesUsed,
                    DraftAnswer = retryResponse.Text,
                    RuntimeState = TaskLifecycleState.Retrying
                };
                retryState.Evidence.AddRange(workflowState.Evidence);
                CaptureEvidence(retryState, retryResponse, "retry");

                var retryConfidence = features.ConfidenceScoringEnabled
                    ? WorkflowConfidenceEvaluator.Evaluate(retryState)
                    : firstConfidence;

                if (retryConfidence.Score >= firstConfidence.Score)
                {
                    selectedResponse = retryResponse;
                    selectedConfidence = retryConfidence;
                    workflowState.DraftAnswer = retryResponse.Text;
                    workflowState.ToolCallsUsed = retryState.ToolCallsUsed;
                }

                workflowState.LatestConfidence = selectedConfidence;
                workflowState.Evidence.Clear();
                workflowState.Evidence.AddRange(retryState.Evidence);
            }
            else if (features.ConstrainedRetryEnabled && firstConfidence.ShouldRetry)
            {
                PublishProgressEvent(
                    runState,
                    "retry.skipped",
                    retryGate.ReasonMessage,
                    true,
                    workflowState.Checklist.Items.FirstOrDefault(i => i.Order == 3)?.Id,
                    new Dictionary<string, string>
                    {
                        ["reason"] = retryGate.ReasonCode,
                        ["remainingRetries"] = retryGate.RemainingRetries.ToString(),
                        ["remainingToolCalls"] = retryGate.RemainingToolCalls.ToString(),
                        ["remainingTimeMs"] = retryGate.RemainingTimeMs.ToString()
                    });
            }

            completionReason = ResolveCompletionReason(
                selectedResponse,
                workflowState,
                selectedConfidence,
                stopwatch.Elapsed);

            workflowState.CompletionReason = completionReason;
            workflowState.RuntimeState = TaskLifecycleState.Finalizing;
            await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.Finalizing, runState.CancellationToken);

            if (features.ChecklistProgressUiEnabled && workflowState.Envelope.ShowChecklist)
            {
                UpdateChecklistStep(workflowState, 3, ChecklistItemState.Completed, "Comparison complete");
                UpdateChecklistStep(workflowState, 4, ChecklistItemState.Completed, "Answer prepared");
                UpdateChecklistStep(workflowState, 5, ChecklistItemState.Completed, "Finalized");
                workflowState.Checklist.CurrentPhase = "Done";
                PublishChecklist(runState, workflowState);
            }

            await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.Completed, runState.CancellationToken);

            if (features.TaskRunAuditSnapshotsEnabled)
            {
                WriteWorkflowAuditSnapshot(audit, runState.RunId, workflowState, selectedConfidence, selectedResponse);
            }
        }
        else
        {
            completionReason = selectedResponse.Success ? CompletionReason.SuccessMediumConfidence : CompletionReason.Failed;
        }

        runState.Append(RuntimeEventTypes.TokenDelta, new TokenDeltaPayload(selectedResponse.Text, 0));
        runState.Append(
            RuntimeEventTypes.RunCompleted,
            new RunCompletedPayload(
                selectedResponse.Text,
                totalRoundTrips,
                ToBriefingDto(selectedResponse.DeepDiveBriefing),
                completionReason?.ToString(),
                selectedConfidence?.Band));
    }

    private static void WriteWorkflowAuditSnapshot(
        IAuditLogger audit,
        string runId,
        TaskRunState workflowState,
        ConfidenceSnapshot? confidence,
        AgentResponse response)
    {
        audit.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "WORKFLOW_RUN_SNAPSHOT",
            Target = runId,
            Result = response.Success ? "ok" : "error",
            Details = new Dictionary<string, object>
            {
                ["task_id"] = workflowState.Envelope.TaskId,
                ["complexity"] = workflowState.Envelope.Complexity.ToString(),
                ["runtime_state"] = workflowState.RuntimeState.ToString(),
                ["completion_reason"] = workflowState.CompletionReason?.ToString() ?? "unknown",
                ["confidence_band"] = confidence?.Band ?? "n/a",
                ["confidence_score"] = confidence?.Score ?? 0.0,
                ["retries_used"] = workflowState.RetriesUsed,
                ["tool_calls_used"] = workflowState.ToolCallsUsed,
                ["checklist_phase"] = workflowState.Checklist.CurrentPhase,
                ["checklist_items"] = workflowState.Checklist.Items
                    .OrderBy(i => i.Order)
                    .Select(i => new Dictionary<string, object>
                    {
                        ["order"] = i.Order,
                        ["title"] = i.Title,
                        ["state"] = i.State.ToString(),
                        ["note"] = i.StatusNote ?? ""
                    })
                    .ToArray(),
                ["event_count"] = workflowState.Events.Count,
                ["evidence_count"] = workflowState.Evidence.Count
            }
        });
    }

    private static async Task<TaskRunState> InitializeWorkflowStateAsync(
        string prompt,
        AppSettings settings,
        CancellationToken ct)
    {
        var envelope = await WorkflowClassifier.ClassifyAsync(prompt, ct);
        envelope = new TaskEnvelope
        {
            TaskId = envelope.TaskId,
            UserRequest = envelope.UserRequest,
            Intent = envelope.Intent,
            Complexity = envelope.Complexity,
            NeedsTools = envelope.NeedsTools,
            ShowChecklist = envelope.ShowChecklist,
            TimeBudget = envelope.TimeBudget,
            MaxRetries = envelope.MaxRetries,
            MaxToolCalls = settings.ToolBudgets.MaxToolCallsPerTurn
        };

        var checklist = await WorkflowChecklistPlanner.BuildChecklistAsync(envelope, ct);
        var state = new TaskRunState
        {
            Envelope = envelope,
            Checklist = checklist,
            RuntimeState = TaskLifecycleState.Planning
        };

        return state;
    }

    private static void CaptureEvidence(TaskRunState state, AgentResponse response, string sourceType)
    {
        foreach (var call in response.ToolCallsMade)
        {
            state.Evidence.Add(new EvidenceRecord
            {
                SourceType = sourceType,
                Title = call.ToolName,
                Summary = call.Success ? "Tool call succeeded" : "Tool call failed",
                TrustScore = call.Success ? 0.70 : 0.30,
                RelevanceScore = 0.65,
                SupportsCandidateAnswer = call.Success,
                ContradictsCandidateAnswer = !call.Success
            });
        }

        if (response.ToolCallsMade.Count == 0)
        {
            state.Evidence.Add(new EvidenceRecord
            {
                SourceType = sourceType,
                Title = "llm_response",
                Summary = "Answer produced without explicit tool evidence.",
                TrustScore = 0.45,
                RelevanceScore = 0.60,
                SupportsCandidateAnswer = true,
                ContradictsCandidateAnswer = false
            });
        }
    }

    private static CompletionReason ResolveCompletionReason(
        AgentResponse response,
        TaskRunState workflowState,
        ConfidenceSnapshot? confidence,
        TimeSpan elapsed)
    {
        return WorkflowCompletionReasonResolver.Resolve(response, workflowState, confidence, elapsed);
    }

    private static string BuildRetryPrompt(string originalPrompt, string firstAnswer, PlannedAction? retryAction)
    {
        if (retryAction is not null && !string.IsNullOrWhiteSpace(retryAction.Instruction))
        {
            return retryAction.Instruction;
        }

        return $"{originalPrompt}\n\n" +
               "The previous answer may be low-confidence. Re-check with stronger evidence and explicit caveats.\n\n" +
               $"Previous answer for verification:\n{firstAnswer}";
    }

    private static void PublishChecklist(RunState runState, TaskRunState workflowState)
    {
        var stamp = string.Join("|", workflowState.Checklist.Items
            .OrderBy(i => i.Order)
            .Select(i => $"{i.Order}:{i.State}:{i.StatusNote}"));

        if (string.Equals(stamp, workflowState.LastPublishedChecklistStamp, StringComparison.Ordinal))
        {
            return;
        }

        workflowState.LastPublishedChecklistStamp = stamp;

        runState.Append(
            RuntimeEventTypes.ChecklistUpdated,
            new ChecklistUpdatedPayload(
                workflowState.Checklist.TaskId,
                workflowState.Checklist.CurrentPhase,
                workflowState.Checklist.Items
                    .OrderBy(i => i.Order)
                    .Select(i => new ChecklistItemPayload(
                        i.Id,
                        i.Title,
                        i.State.ToString(),
                        i.Order,
                        i.StatusNote))
                    .ToArray()));
    }

    private static void PublishProgressEvent(
        RunState runState,
        string eventType,
        string message,
        bool userVisible,
        string? relatedStepId,
        Dictionary<string, string>? metadata = null)
    {
        runState.Append(
            RuntimeEventTypes.ProgressEvent,
            new ProgressEventPayload(eventType, message, userVisible, relatedStepId, metadata));
    }

    private static async Task PublishNarrationIfAnyAsync(
        RunState runState,
        TaskRunState workflowState,
        ProgressTrigger trigger,
        CancellationToken ct)
    {
        var message = await WorkflowNarrator.BuildUpdateAsync(workflowState, trigger, ct);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (string.Equals(message, workflowState.LastPublishedNarration, StringComparison.Ordinal))
        {
            return;
        }

        workflowState.LastPublishedNarration = message;

        runState.Append(
            RuntimeEventTypes.NarrationUpdated,
            new NarrationUpdatedPayload(message, workflowState.Checklist.CurrentPhase));
    }

    private static void UpdateChecklistStep(TaskRunState workflowState, int order, ChecklistItemState state, string? note = null)
    {
        var item = workflowState.Checklist.Items.FirstOrDefault(i => i.Order == order);
        if (item is null)
        {
            return;
        }

        item.State = state;
        item.StatusNote = note;
        var now = DateTimeOffset.UtcNow;
        if (state == ChecklistItemState.InProgress)
        {
            item.StartedAt ??= now;
        }
        else if (state is ChecklistItemState.Completed or ChecklistItemState.Failed or ChecklistItemState.Skipped or ChecklistItemState.Blocked)
        {
            item.CompletedAt ??= now;
        }
    }
}
