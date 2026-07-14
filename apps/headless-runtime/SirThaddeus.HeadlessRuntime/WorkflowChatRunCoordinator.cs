using System.Diagnostics;
using System.Globalization;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Workflow;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Contracts;
using System.Text.Json;

internal sealed class WorkflowChatRunCoordinator
{
    private const string SourcesJsonDelimiter = "<!-- SOURCES_JSON -->";

    private readonly Func<AppSettings> _getSettings;
    private readonly Func<AppSettings, IHeadlessAgent> _buildOrchestrator;
    private readonly IAuditLogger _audit;
    private readonly ITaskClassifier _classifier = new TaskClassifier();
    private readonly IChecklistPlanner _checklistPlanner = new ChecklistPlanner();
    private readonly IConfidenceEvaluator _confidenceEvaluator = new ConfidenceEvaluator();
    private readonly IRetryPlanner _retryPlanner = new RetryPlanner();
    private readonly IRetryGateEvaluator _retryGateEvaluator = new RetryGateEvaluator();
    private readonly ICompletionReasonResolver _completionReasonResolver = new CompletionReasonResolver();
    private readonly IProgressNarrator _narrator = new ProgressNarrator();

    public WorkflowChatRunCoordinator(
        Func<AppSettings> getSettings,
        Func<AppSettings, IHeadlessAgent> buildOrchestrator,
        IAuditLogger audit)
    {
        _getSettings = getSettings;
        _buildOrchestrator = buildOrchestrator;
        _audit = audit;
    }

    public async Task ExecuteAsync(ChatRequest request, RuntimeApiServer.RunState runState)
    {
        var settings = _getSettings();

        var orchestrator = _buildOrchestrator(settings);
        if (request.Messages is { Count: > 0 })
        {
            orchestrator.SeedHistory(request.Messages.Select(m => (m.Role, m.Content)));
        }

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? request.SessionId
            : request.ConversationId;

        var workflowState = await InitializeWorkflowStateAsync(request.Prompt, settings, runState.CancellationToken);
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
        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 1, ChecklistItemState.InProgress, "Understanding request");
            PublishChecklist(runState, workflowState);
        }

        await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.TaskStarted, runState.CancellationToken);

        var workflowDecorator = new TimeBudgetedAgentOrchestrator(orchestrator);
        IAgentOrchestrator effectiveOrchestrator = workflowDecorator;

        var stopwatch = Stopwatch.StartNew();
        workflowDecorator.SetRunBudget(workflowState.Envelope.TimeBudget, stopwatch);

        var firstResponse = await effectiveOrchestrator.ProcessAsync(
            request.Prompt,
            conversationId,
            runState.CancellationToken);

        var selectedResponse = firstResponse;
        var totalRoundTrips = firstResponse.LlmRoundTrips;
        var totalToolCallsUsed = firstResponse.ToolCallsMade.Count;

        CaptureEvidence(workflowState, firstResponse, "primary");
        workflowState.DraftAnswer = firstResponse.Text;
        workflowState.ToolCallsUsed = totalToolCallsUsed;

        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 1, ChecklistItemState.Completed, "Request understood");
            UpdateChecklistStep(workflowState, 2, ChecklistItemState.InProgress, "Gathering evidence");
            workflowState.Checklist.CurrentPhase = "Gathering evidence";
            PublishChecklist(runState, workflowState);
        }

        var firstConfidence = _confidenceEvaluator.Evaluate(workflowState);
        if (ExplicitWebNoResultsContractNormalizer.ShouldPreserveResponse(
                request.Prompt,
                firstResponse.Text,
                firstResponse.ToolCallsMade))
        {
            firstConfidence = new ConfidenceSnapshot
            {
                Score = Math.Max(firstConfidence.Score, 0.85),
                Band = "High",
                Summary = "Deterministic explicit web no-results contract response preserved without retry.",
                ShouldRetry = false
            };
        }
        else if (IsSuccessfulPlacesDiscoverAnswer(firstResponse))
        {
            firstConfidence = new ConfidenceSnapshot
            {
                Score = Math.Max(firstConfidence.Score, 0.86),
                Band = "High",
                Summary = "Deterministic places_discover answer used successful local-business evidence.",
                ShouldRetry = false
            };
        }
        workflowState.LatestConfidence = firstConfidence;
        var selectedConfidence = firstConfidence;

        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 2, ChecklistItemState.Completed, "Evidence captured");
            UpdateChecklistStep(workflowState, 3, ChecklistItemState.InProgress, "Comparing findings");
            workflowState.Checklist.CurrentPhase = "Comparing evidence";
            PublishChecklist(runState, workflowState);
        }

        var retryGate = _retryGateEvaluator.Evaluate(workflowState, firstConfidence, stopwatch.Elapsed);
        workflowState.LastRetryGateDecision = retryGate;

        if (retryGate.IsAllowed)
        {
            var retryPlan = await _retryPlanner.BuildRetryPlanAsync(workflowState, runState.CancellationToken);
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
            var retryResponse = await effectiveOrchestrator.ProcessAsync(
                retryPrompt,
                conversationId,
                runState.CancellationToken);

            totalRoundTrips += retryResponse.LlmRoundTrips;
            totalToolCallsUsed += retryResponse.ToolCallsMade.Count;
            workflowState.ToolCallsUsed = totalToolCallsUsed;

            var retryState = new TaskRunState
            {
                Envelope = workflowState.Envelope,
                Checklist = workflowState.Checklist,
                ToolCallsUsed = totalToolCallsUsed,
                RetriesUsed = workflowState.RetriesUsed,
                DraftAnswer = retryResponse.Text,
                RuntimeState = TaskLifecycleState.Retrying
            };
            retryState.Evidence.AddRange(workflowState.Evidence);
            CaptureEvidence(retryState, retryResponse, "retry");

            // Judge the retry on its OWN evidence, not the accumulated
            // first+retry set. firstConfidence was computed on the first
            // attempt's evidence alone, so the comparison must be
            // like-for-like. Folding the first attempt's failed tool calls
            // into the retry's score charges them a second time as
            // contradictions — which means a retry that recovers from a
            // tool error (fail → fix → correct answer) can never out-score
            // the first attempt, and its correct answer gets discarded.
            var retryOnlyState = new TaskRunState
            {
                Envelope = workflowState.Envelope,
                DraftAnswer = retryResponse.Text,
                RuntimeState = TaskLifecycleState.Retrying
            };
            CaptureEvidence(retryOnlyState, retryResponse, "retry");

            var retryConfidence = _confidenceEvaluator.Evaluate(retryOnlyState);
            if (retryConfidence.Score >= firstConfidence.Score)
            {
                selectedResponse = retryResponse;
                selectedConfidence = retryConfidence;
                workflowState.DraftAnswer = retryResponse.Text;
            }

            workflowState.LatestConfidence = selectedConfidence;
            workflowState.Evidence.Clear();
            workflowState.Evidence.AddRange(retryState.Evidence);
        }
        else
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
                    ["remainingTimeMs"] = retryGate.RemainingTimeMs.ToString(),
                    ["confidenceBand"] = firstConfidence.Band,
                    ["confidenceScore"] = firstConfidence.Score.ToString("0.000", CultureInfo.InvariantCulture)
                });
        }

        var guardedSelectedText = ToolBackedResponseQualityGuards.Apply(
            selectedResponse.Text,
            request.Prompt,
            selectedResponse.ToolCallsMade);
        if (!string.Equals(guardedSelectedText, selectedResponse.Text, StringComparison.Ordinal))
        {
            selectedResponse = selectedResponse with { Text = guardedSelectedText };
            workflowState.DraftAnswer = guardedSelectedText;
        }

        var normalizedExplicitLookupContract = ExplicitWebNoResultsContractNormalizer.TryBuildResponse(
            request.Prompt,
            selectedResponse.ToolCallsMade);
        normalizedExplicitLookupContract ??= ExplicitWebNoResultsContractNormalizer.TryBuildResponseFromFailureText(
            request.Prompt,
            selectedResponse.Text);
        if (!string.IsNullOrWhiteSpace(normalizedExplicitLookupContract) &&
            string.Equals(
                IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(request.Prompt.Trim().ToLowerInvariant()),
                Intents.LookupSearch,
                StringComparison.OrdinalIgnoreCase))
        {
            selectedResponse = selectedResponse with { Text = normalizedExplicitLookupContract };
            workflowState.DraftAnswer = normalizedExplicitLookupContract;
        }

        var completionReason = _completionReasonResolver.Resolve(
            selectedResponse,
            workflowState,
            selectedConfidence,
            stopwatch.Elapsed);

        workflowState.CompletionReason = completionReason;
        workflowState.RuntimeState = TaskLifecycleState.Finalizing;

        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 3, ChecklistItemState.Completed, "Comparison complete");
            UpdateChecklistStep(workflowState, 4, ChecklistItemState.InProgress, "Preparing answer");
            workflowState.Checklist.CurrentPhase = "Preparing answer";
            PublishChecklist(runState, workflowState);
        }

        await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.Finalizing, runState.CancellationToken);

        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 4, ChecklistItemState.Completed, "Answer prepared");
            UpdateChecklistStep(workflowState, 5, ChecklistItemState.InProgress, "Delivering response");
            workflowState.Checklist.CurrentPhase = "Delivering response";
            PublishChecklist(runState, workflowState);
        }

        runState.Append(RuntimeEventTypes.TokenDelta, new TokenDeltaPayload(selectedResponse.Text, 0));
        runState.Append(
            RuntimeEventTypes.RunCompleted,
            new RunCompletedPayload(
                selectedResponse.Text,
                totalRoundTrips,
                totalToolCallsUsed,
                null,
                completionReason.ToString(),
                selectedConfidence?.Band,
                workflowState.LastRetryGateDecision?.IsAllowed,
                workflowState.LastRetryGateDecision?.ReasonCode,
                ExtractAssistantSourceCards(selectedResponse),
                selectedResponse.SuppressSourceCardsUi));

        if (workflowState.Envelope.ShowChecklist)
        {
            UpdateChecklistStep(workflowState, 5, ChecklistItemState.Completed, "Finalized");
            workflowState.Checklist.CurrentPhase = "Done";
            PublishChecklist(runState, workflowState);
        }

        await PublishNarrationIfAnyAsync(runState, workflowState, ProgressTrigger.Completed, runState.CancellationToken);
        WriteWorkflowAuditSnapshot(runState.RunId, workflowState, selectedConfidence, selectedResponse);
    }

    private async Task<TaskRunState> InitializeWorkflowStateAsync(string prompt, AppSettings settings, CancellationToken ct)
    {
        var envelope = await _classifier.ClassifyAsync(prompt, ct);
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

        var checklist = await _checklistPlanner.BuildChecklistAsync(envelope, ct);
        return new TaskRunState
        {
            Envelope = envelope,
            Checklist = checklist,
            RuntimeState = TaskLifecycleState.Planning
        };
    }

    private void WriteWorkflowAuditSnapshot(
        string runId,
        TaskRunState workflowState,
        ConfidenceSnapshot? confidence,
        AgentResponse response)
    {
        _audit.Append(new AuditEvent
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
                ["retry_gate_allowed"] = workflowState.LastRetryGateDecision?.IsAllowed is bool allowed ? allowed : "n/a",
                ["retry_gate_reason"] = workflowState.LastRetryGateDecision?.ReasonCode ?? "n/a",
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

    private static void CaptureEvidence(TaskRunState state, AgentResponse response, string sourceType)
    {
        foreach (var call in response.ToolCallsMade)
        {
            var resultLength = call.Result?.Length ?? 0;
            var trust = call.Success ? TrustScoreForTool(call.ToolName) : 0.28;
            if (call.Success && resultLength < 20)
                trust = Math.Min(trust, 0.40);

            var relevance = call.Success && resultLength > 500 ? 0.72 : 0.62;

            state.Evidence.Add(new EvidenceRecord
            {
                SourceType = sourceType,
                Title = call.ToolName,
                Summary = call.Success
                    ? $"Tool succeeded ({resultLength} chars)"
                    : "Tool call failed",
                TrustScore = trust,
                RelevanceScore = relevance,
                SupportsCandidateAnswer = call.Success,
                ContradictsCandidateAnswer = !call.Success
            });
        }

        if (response.ToolCallsMade.Count == 0)
        {
            var textLength = response.Text?.Length ?? 0;
            var llmTrust = textLength > 200 ? 0.50 : 0.40;

            state.Evidence.Add(new EvidenceRecord
            {
                SourceType = sourceType,
                Title = "llm_response",
                Summary = "Answer produced without explicit tool evidence.",
                TrustScore = llmTrust,
                RelevanceScore = 0.60,
                SupportsCandidateAnswer = true,
                ContradictsCandidateAnswer = false
            });
        }
    }

    private static bool IsSuccessfulPlacesDiscoverAnswer(AgentResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Text) ||
            !response.Text.Contains("places_discover/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return response.ToolCallsMade.Any(call =>
            call.Success &&
            (string.Equals(call.ToolName, ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(call.ToolName, ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)));
    }

    private static double TrustScoreForTool(string toolName)
    {
        var lower = (toolName ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("read") || lower.Contains("doc") || lower.Contains("file"))
            return 0.82;
        if (lower.Contains("search") || lower.Contains("web") || lower.Contains("fetch") || lower.Contains("browse"))
            return 0.76;
        if (lower.Contains("memory") || lower.Contains("recall") || lower.Contains("remember"))
            return 0.62;
        return 0.70;
    }

    private static IReadOnlyList<AssistantSourceCardPayload> ExtractAssistantSourceCards(AgentResponse response)
    {
        if (response.SuppressSourceCardsUi)
        {
            return [];
        }

        var cards = new List<AssistantSourceCardPayload>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in response.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Url) || !seenUrls.Add(source.Url))
            {
                continue;
            }

            cards.Add(new AssistantSourceCardPayload(
                Title: source.Title ?? source.Url,
                Url: source.Url,
                Domain: source.Domain ?? "",
                Excerpt: source.Excerpt ?? "",
                Favicon: source.Favicon ?? "",
                Thumbnail: source.Thumbnail ?? "",
                PublishedAt: source.PublishedAt));
            if (cards.Count >= 8)
            {
                return cards;
            }
        }

        if (response.ToolCallsMade.Count == 0)
        {
            return cards;
        }

        for (var i = response.ToolCallsMade.Count - 1; i >= 0; i--)
        {
            var toolCall = response.ToolCallsMade[i];
            if (!toolCall.Success || string.IsNullOrWhiteSpace(toolCall.Result))
            {
                continue;
            }

            foreach (var card in ParseAssistantSourceCards(toolCall.Result))
            {
                if (!seenUrls.Add(card.Url))
                {
                    continue;
                }

                cards.Add(card);
                if (cards.Count >= 8)
                {
                    return cards;
                }
            }
        }

        return cards;
    }

    private static IReadOnlyList<AssistantSourceCardPayload> ParseAssistantSourceCards(string toolResult)
    {
        var cards = new List<AssistantSourceCardPayload>();
        if (string.IsNullOrWhiteSpace(toolResult))
        {
            return cards;
        }

        var delimiterIndex = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimiterIndex < 0)
        {
            return cards;
        }

        var json = toolResult[(delimiterIndex + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            return cards;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement itemsElement;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = doc.RootElement;
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("sources", out var sourcesElement) &&
                     sourcesElement.ValueKind == JsonValueKind.Array)
            {
                itemsElement = sourcesElement;
            }
            else
            {
                return cards;
            }

            foreach (var item in itemsElement.EnumerateArray())
            {
                var url = TryReadString(item, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                cards.Add(new AssistantSourceCardPayload(
                    TryReadString(item, "title") ?? string.Empty,
                    url,
                    TryReadString(item, "domain") ?? string.Empty,
                    TryReadString(item, "excerpt") ?? string.Empty,
                    TryReadString(item, "favicon") ?? string.Empty,
                    TryReadString(item, "thumbnail") ?? string.Empty,
                    TryReadString(item, "publishedAt")));
            }
        }
        catch
        {
            // Source cards are best-effort only.
        }

        return cards;
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string BuildRetryPrompt(string originalPrompt, string firstAnswer, PlannedAction? retryAction)
    {
        if (retryAction is not null && !string.IsNullOrWhiteSpace(retryAction.Instruction))
            return retryAction.Instruction;

        return $"{originalPrompt}\n\n" +
               "The previous answer may be low-confidence. Re-check with stronger evidence and explicit caveats.\n\n" +
               $"Previous answer for verification:\n{firstAnswer}";
    }

    private async Task PublishNarrationIfAnyAsync(
        RuntimeApiServer.RunState runState,
        TaskRunState workflowState,
        ProgressTrigger trigger,
        CancellationToken ct)
    {
        var message = await _narrator.BuildUpdateAsync(workflowState, trigger, ct);
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (string.Equals(message, workflowState.LastPublishedNarration, StringComparison.Ordinal))
            return;

        workflowState.LastPublishedNarration = message;
        runState.Append(
            RuntimeEventTypes.NarrationUpdated,
            new NarrationUpdatedPayload(message, workflowState.Checklist.CurrentPhase));
    }

    private static void PublishChecklist(RuntimeApiServer.RunState runState, TaskRunState workflowState)
    {
        var stamp = string.Join("|", workflowState.Checklist.Items
            .OrderBy(i => i.Order)
            .Select(i => $"{i.Order}:{i.State}:{i.StatusNote}"));

        if (string.Equals(stamp, workflowState.LastPublishedChecklistStamp, StringComparison.Ordinal))
            return;

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
        RuntimeApiServer.RunState runState,
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

    private static void UpdateChecklistStep(TaskRunState workflowState, int order, ChecklistItemState state, string? note = null)
    {
        var item = workflowState.Checklist.Items.FirstOrDefault(i => i.Order == order);
        if (item is null)
            return;

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
