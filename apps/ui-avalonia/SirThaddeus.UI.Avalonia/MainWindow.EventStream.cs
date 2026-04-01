using Avalonia.Threading;
using SirThaddeus.Contracts;
using SirThaddeus.UI.Avalonia.ViewModels;
using System.Text;
using System.Text.Json;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private void StartEventStream(string runId)
    {
        _eventStreamCancellation?.Cancel();
        _eventStreamCancellation?.Dispose();
        _eventStreamCancellation = new CancellationTokenSource();
        _ = Task.Run(() => StreamEventsAsync(runId, _eventStreamCancellation.Token));
    }

    private async Task StreamEventsAsync(string runId, CancellationToken cancellationToken)
    {
        if (_runtimeApiClient is null)
        {
            return;
        }

        try
        {
            await foreach (var envelope in _runtimeApiClient.StreamRunEventsAsync(runId, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await Dispatcher.UIThread.InvokeAsync(() => HandleEvent(envelope));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _currentSession.ClearPendingAssistantMessage();
                _assistantBuffersByRunId.Remove(runId);
                _activeRunId = null;
                AppendTranscript($"[error] Event stream failed: {ex.Message}");
                UpdateComposerState();
            });
        }
    }

    private void HandleEvent(RuntimeEventEnvelope envelope)
    {
        switch (envelope.EventType)
        {
            case RuntimeEventTypes.TokenDelta:
                var token = ReadPayload<TokenDeltaPayload>(envelope.Payload);
                if (token is not null)
                {
                    if (string.IsNullOrEmpty(token.Delta))
                    {
                        break;
                    }

                    if (!_assistantBuffersByRunId.TryGetValue(envelope.RunId, out var buffer))
                    {
                        buffer = new StringBuilder();
                        _assistantBuffersByRunId[envelope.RunId] = buffer;
                    }

                    buffer.Append(token.Delta);
                    AppendTranscript($"[assistant] {token.Delta}");
                }
                break;
            case RuntimeEventTypes.RunCompleted:
                var completed = ReadPayload<RunCompletedPayload>(envelope.Payload);
                if (_assistantBuffersByRunId.TryGetValue(envelope.RunId, out var completedBuffer))
                {
                    var streamedText = completedBuffer.ToString();
                    if (string.IsNullOrWhiteSpace(streamedText) &&
                        !string.IsNullOrWhiteSpace(completed?.FinalText))
                    {
                        _lastAssistantMessage = completed.FinalText;
                        AppendTranscript($"[assistant] {completed.FinalText}");
                    }
                    else
                    {
                        _lastAssistantMessage = streamedText;
                    }

                    _assistantBuffersByRunId.Remove(envelope.RunId);
                }
                else if (!string.IsNullOrWhiteSpace(completed?.FinalText))
                {
                    _lastAssistantMessage = completed.FinalText;
                    AppendTranscript($"[assistant] {completed.FinalText}");
                }

                var assistantSourceCards = completed?.SuppressSourceCardsUi == true
                    ? Array.Empty<ChatSourceCardItem>()
                    : CreateAssistantSourceCards(completed?.SourceCards);
                _lastAssistantSources = assistantSourceCards.Count > 0
                    ? assistantSourceCards.Select(card => card.Url).ToArray()
                    : BuildAssistantSourceList(_lastAssistantMessage ?? string.Empty, completed?.Briefing);
                if (completed?.Briefing is not null)
                {
                    DisplayBriefing(completed.Briefing, recordHistory: true, activateTab: true);
                }

                if (!string.IsNullOrWhiteSpace(completed?.ConfidenceBand) ||
                    !string.IsNullOrWhiteSpace(completed?.CompletionReason))
                {
                    var confidenceText = string.IsNullOrWhiteSpace(completed?.ConfidenceBand)
                        ? "n/a"
                        : completed!.ConfidenceBand;
                    var reasonText = FormatCompletionReasonForDisplay(completed?.CompletionReason);
                    _workflowConfidenceBand = confidenceText;
                    UpdateWorkflowToolStrip();
                }

                if (!string.IsNullOrWhiteSpace(_lastAssistantMessage))
                {
                    var parts = ParseAssistantDisplayParts(_lastAssistantMessage);
                    var lastMsg = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant");
                    if (lastMsg is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(parts.ThinkingText))
                        {
                            lastMsg.ThoughtContent = parts.ThinkingText;
                            lastMsg.Content = parts.DisplayText;
                        }
                        else
                        {
                            lastMsg.Content = parts.DisplayText;
                        }

                        if (!string.IsNullOrWhiteSpace(_lastUserPrompt))
                        {
                            lastMsg.RetryPrompt = _lastUserPrompt;
                        }

                        if (!string.IsNullOrWhiteSpace(completed?.PlanSummary))
                        {
                            lastMsg.PlanContent = completed!.PlanSummary;
                        }

                        lastMsg.SetSourceCards(assistantSourceCards);
                    }
                }

                if (_toolCallsInCurrentRun.Count > 0)
                {
                    var toolNames = string.Join(", ", _toolCallsInCurrentRun.Distinct());
                    var iterations = completed?.ToolLoopIterations ?? 0;
                    var summary = $"\u21B3 tools called: {_toolCallsInCurrentRun.Count} ({toolNames}) \u00B7 {iterations} round-trip(s)";

                    var assistantMsg = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant");
                    if (assistantMsg is not null)
                    {
                        assistantMsg.ToolSummary = summary;
                    }

                    UpdateWorkflowToolStrip();

                    _toolCallsInCurrentRun.Clear();
                }

                _currentSession.ClearPendingAssistantMessage();
                ScrollChatToBottom();

                var shouldAutoSpeak = _voiceInitiatedRun;
                _voiceInitiatedRun = false;
                _activeRunId = null;
                UpdateComposerState();

                if (ActionDrawer.IsVisible)
                {
                    _ = RefreshActionDrawerAsync();
                }

                if (_workflowChecklistItems.Count == 0)
                {
                    _ = AutoCollapseProgressDrawerAsync();
                }

                if (shouldAutoSpeak && !string.IsNullOrWhiteSpace(_lastAssistantMessage))
                {
                    _ = AutoSpeakResponseAsync(_lastAssistantMessage);
                }
                else if (shouldAutoSpeak)
                {
                    SetVoiceChatStatus("Hold");
                }
                break;
            case RuntimeEventTypes.RunFailed:
                _currentSession.ClearPendingAssistantMessage();
                _assistantBuffersByRunId.Remove(envelope.RunId);
                _toolCallsInCurrentRun.Clear();
                UpdateWorkflowToolStrip();
                _voiceInitiatedRun = false;
                SetVoiceChatStatus("Hold");
                var failure = ReadPayload<RunFailedPayload>(envelope.Payload);
                AppendTranscript($"[system] Run failed: {failure?.Error ?? "unknown"}");
                _activeRunId = null;
                HideProgressDrawer();
                UpdateComposerState();
                if (ActionDrawer.IsVisible)
                {
                    _ = RefreshActionDrawerAsync();
                }
                break;
            case RuntimeEventTypes.ToolRequested:
                var request = ReadPayload<ToolRequestedPayload>(envelope.Payload);
                if (request is not null)
                {
                    _pendingPermissionRequestId = request.RequestId;
                    _pendingPermissionAudit[request.RequestId] = new PendingPermissionAuditContext(
                        request.ToolName,
                        SummarizeToolRequest(request.ToolName, request.Reason, request.ArgumentsJson),
                        request.ArgumentsJson,
                        envelope.TimestampUtc.LocalDateTime.ToString("g"));
                    _toolCallsInCurrentRun.Add(request.ToolName);
                    UpdateWorkflowToolStrip();
                    ShowPermissionRequest(request);
                    AddRecentActivity(
                        GetToolActivityIcon(request.ToolName),
                        $"{FormatToolDisplayName(request.ToolName)} requested",
                        SummarizeToolRequest(request.ToolName, request.Reason, request.ArgumentsJson),
                        "Awaiting approval",
                        "Explicit approval required",
                        BuildToolRequestAuditPreview(request),
                        request.ToolName);

                    if (_uiSettings.AutoSwitchToPermissions)
                    {
                        SetActiveView(SettingsTabButton);
                        SettingsTabControl.SelectedItem = PermissionsTabItem;
                    }
                }
                break;
            case RuntimeEventTypes.ToolApproved:
            case RuntimeEventTypes.ToolDenied:
                var decision = ReadPayload<ToolDecisionPayload>(envelope.Payload);
                _pendingPermissionRequestId = null;
                ResetPermissionRequestUi();
                if (decision is not null)
                {
                    _pendingPermissionAudit.TryGetValue(decision.RequestId, out var pendingAudit);
                    AddRecentActivity(
                        GetToolActivityIcon(decision.ToolName),
                        $"{FormatToolDisplayName(decision.ToolName)} {(decision.Approved ? "approved" : "denied")}",
                        pendingAudit?.Purpose ?? $"{FormatToolDisplayName(decision.ToolName)} permission request resolved.",
                        decision.Approved ? "Authorized" : "Denied",
                        pendingAudit?.DecisionSummary ?? (decision.Approved ? "Explicit approval recorded" : "Denied by operator"),
                        BuildToolDecisionAuditPreview(decision, pendingAudit),
                        decision.ToolName);
                    _pendingPermissionAudit.Remove(decision.RequestId);
                }
                break;
            case RuntimeEventTypes.NarrationUpdated:
                var narration = ReadPayload<NarrationUpdatedPayload>(envelope.Payload);
                if (!string.IsNullOrWhiteSpace(narration?.Message) &&
                    !string.Equals(_lastWorkflowNarration, narration.Message, StringComparison.Ordinal))
                {
                    _lastWorkflowNarration = narration.Message;
                    WorkflowNarrationText.Text = narration.Message;
                    ShowProgressDrawer();
                }
                break;
            case RuntimeEventTypes.ChecklistUpdated:
                var checklist = ReadPayload<ChecklistUpdatedPayload>(envelope.Payload);
                if (checklist is not null)
                {
                    var stamp = string.Join("|", checklist.Items.Select(i => $"{i.Order}:{i.State}"));
                    if (!string.Equals(_lastWorkflowChecklistStamp, stamp, StringComparison.Ordinal))
                    {
                        _lastWorkflowChecklistStamp = stamp;
                        _workflowChecklistItems.Clear();
                        foreach (var item in checklist.Items.OrderBy(i => i.Order))
                        {
                            var stateIcon = item.State switch
                            {
                                "Completed"  => "\u2713",
                                "InProgress" => "\u25CF",
                                "Failed"     => "\u2717",
                                "Blocked"    => "\u2014",
                                "Skipped"    => "\u203A",
                                _            => "\u25CB"
                            };
                            var titleText = (item.Title ?? "").Trim();
                            var noteText = (item.StatusNote ?? "").Trim();
                            var label = string.IsNullOrWhiteSpace(noteText)
                                ? $"{stateIcon} {titleText}"
                                : $"{stateIcon} {titleText} \u2014 {noteText}";
                            _workflowChecklistItems.Add(new WorkflowChecklistItemViewModel
                            {
                                Id = item.Id,
                                Order = item.Order,
                                State = item.State,
                                Label = label,
                                StateIcon = stateIcon,
                                Title = titleText,
                                StatusNote = noteText
                            });
                        }

                        if (_workflowChecklistItems.Count > 0)
                        {
                            ShowProgressDrawer();

                            if (string.Equals(checklist.CurrentPhase, "Done", StringComparison.OrdinalIgnoreCase))
                            {
                                _ = AutoCollapseProgressDrawerAsync();
                            }
                        }
                    }
                }
                break;
            case RuntimeEventTypes.ProgressEvent:
                var progressEvent = ReadPayload<ProgressEventPayload>(envelope.Payload);
                if (progressEvent?.UserVisible == true &&
                    !string.IsNullOrWhiteSpace(progressEvent.Message))
                {
                    if (string.Equals(progressEvent.EventType, "retry.started", StringComparison.OrdinalIgnoreCase))
                    {
                        _workflowRetryCount++;
                        WorkflowNarrationText.Text = "Retrying with alternate verification strategy\u2026";
                        ShowProgressDrawer();
                        UpdateWorkflowToolStrip();
                    }
                    else if (string.Equals(progressEvent.EventType, "retry.skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        var band = progressEvent.Metadata?.TryGetValue("confidenceBand", out var b) == true ? b : null;
                        if (!string.IsNullOrWhiteSpace(band))
                        {
                            _workflowConfidenceBand = band;
                        }

                        var reason = progressEvent.Metadata?.TryGetValue("reason", out var r) == true ? r : null;
                        var skipLabel = reason switch
                        {
                            "confidence_not_retry" => "Confidence is sufficient \u2014 no retry needed.",
                            "retry_budget_exhausted" => "Retry budget exhausted \u2014 finalizing with current evidence.",
                            "tool_budget_exhausted" => "Tool budget exhausted \u2014 finalizing with current evidence.",
                            "time_budget_exhausted" => "Time budget exhausted \u2014 finalizing with current evidence.",
                            _ => "Retry skipped \u2014 finalizing with current evidence."
                        };

                        WorkflowNarrationText.Text = skipLabel;
                        ShowProgressDrawer();
                        UpdateWorkflowToolStrip();
                    }
                    else if (string.Equals(progressEvent.EventType, "task.started", StringComparison.OrdinalIgnoreCase))
                    {
                        var complexity = progressEvent.Metadata?.TryGetValue("complexity", out var c) == true ? c : null;
                        if (!string.IsNullOrWhiteSpace(complexity))
                        {
                            var label = complexity switch
                            {
                                "Trivial" => "Simple request \u2014 answering directly.",
                                "SimpleLookup" => "Gathering information\u2026",
                                "MultiStepResearch" => "Multi-step research \u2014 building checklist\u2026",
                                _ => "Processing request\u2026"
                            };
                            WorkflowNarrationText.Text = label;
                            ShowProgressDrawer();
                        }
                    }
                }
                break;
            default:
                break;
        }

        UpdateActionDrawerSummary();
    }

    private static T? ReadPayload<T>(object payload)
    {
        if (payload is T typed)
        {
            return typed;
        }

        if (payload is JsonElement jsonElement)
        {
            return jsonElement.Deserialize<T>(PayloadJsonOptions);
        }

        return default;
    }
}