using Microsoft.Extensions.Hosting;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Chat;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Automations;

/// <summary>
/// Executes an <see cref="Automation"/> end-to-end:
/// <list type="number">
///   <item>Creates a fresh chat thread titled after the automation.</item>
///   <item>For each step, appends it as a user message and awaits the assistant's reply.</item>
///   <item>Updates an activity-log entry as steps complete, so the Activity UI reflects progress.</item>
/// </list>
///
/// Runs happen on a background task so the HTTP handler can return the created
/// thread id immediately — the UI navigates there and watches the run stream
/// through the existing chat turn pipeline. No new UI or WS event type needed.
///
/// Cancellation uses <see cref="IHostApplicationLifetime.ApplicationStopping"/>
/// rather than the request CT so shutdown drains cleanly. Per-step failures
/// are recorded but don't stop the run — the user can see what worked and
/// what didn't in the resulting thread.
/// </summary>
public sealed class AutomationRunner
{
    private readonly IThreadStore _threads;
    private readonly IAssistant _assistant;
    private readonly IActivityLog _activity;
    private readonly ToolPermissionGate _gate;
    private readonly ChatTurnPublisher _publisher;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AutomationRunner> _logger;

    public AutomationRunner(
        IThreadStore threads,
        IAssistant assistant,
        IActivityLog activity,
        ToolPermissionGate gate,
        ChatTurnPublisher publisher,
        IHostApplicationLifetime lifetime,
        ILogger<AutomationRunner> logger)
    {
        _threads = threads;
        _assistant = assistant;
        _activity = activity;
        _gate = gate;
        _publisher = publisher;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Kicks off a run in the background. Returns the thread id the run will
    /// play into so callers can navigate the UI there while steps execute.
    /// </summary>
    public async Task<AutomationRunStart> StartRunAsync(Automation automation, CancellationToken ct)
    {
        // Create the thread synchronously so we can return its id; everything
        // else (step posting, assistant calls) happens in the background.
        var thread = await _threads.CreateAsync($"Run: {automation.Name}", ct).ConfigureAwait(false);

        var activityEntry = new ActivityEntry(
            Id: InMemoryActivityLog.NewId(),
            Kind: ActivityKind.Automation,
            Summary: automation.Name,
            Status: ActivityStatus.Running,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: null,
            ThreadId: thread.Id,
            Detail: $"Running {automation.Steps.Count} step{(automation.Steps.Count == 1 ? "" : "s")}…");
        _activity.Append(activityEntry);

        // Fire-and-forget the actual execution so the HTTP caller gets an
        // immediate thread id to navigate to.
        _ = Task.Run(() => ExecuteAsync(automation, thread.Id, activityEntry.Id));

        return new AutomationRunStart(thread.Id, activityEntry.Id);
    }

    private async Task ExecuteAsync(Automation automation, string threadId, string activityId)
    {
        var bgCt = _lifetime.ApplicationStopping;
        var completed = 0;
        var failed = 0;
        ActivityStatus finalStatus = ActivityStatus.Ok;
        string? finalDetail = null;

        // Register pre-approved tools for this run's thread. If the model
        // reaches for a tool outside the allowlist, the gate falls through
        // to the normal policy (modal prompts the user).
        using var _allowlistScope = _gate.RegisterThreadAllowlist(
            threadId, automation.AllowedTools ?? Array.Empty<string>());

        try
        {
            for (var i = 0; i < automation.Steps.Count; i++)
            {
                bgCt.ThrowIfCancellationRequested();
                var step = automation.Steps[i];
                if (string.IsNullOrWhiteSpace(step)) continue;

                // Append the step as a user message so the run looks like a
                // real conversation in the chat UI.
                var userMessage = new ChatMessage(
                    Id: NewMessageId(),
                    Role: ChatRole.User,
                    Text: step,
                    CreatedAt: DateTimeOffset.UtcNow);
                await _threads.AppendMessageAsync(threadId, userMessage, bgCt).ConfigureAwait(false);

                // Notify the UI that a user message was appended. Without
                // this broadcast the web chat store never sees steps 2..N —
                // it only has whatever messages were in the thread at the
                // time of the initial fetch, and no WS event tells it a new
                // user bubble arrived.
                await _publisher.PublishUserMessageAppendedAsync(
                    threadId, userMessage.Id, userMessage.Text, userMessage.CreatedAt, bgCt)
                    .ConfigureAwait(false);

                // Let the assistant handle the step. Tool calls, permissions,
                // and streaming flow through the same pipeline as a normal chat.
                try
                {
                    await _assistant.RespondAsync(threadId, step, bgCt).ConfigureAwait(false);
                    completed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "automation.step_failed automation={Name} step={Index}",
                        automation.Name, i);
                    failed++;
                    finalStatus = ActivityStatus.Failed;
                }

                // Live-update the activity row after each step.
                _activity.Update(
                    activityId,
                    status: finalStatus,
                    detail: $"Step {i + 1}/{automation.Steps.Count} · {completed} ok, {failed} failed");
            }

            finalDetail = failed == 0
                ? $"Completed {completed} step{(completed == 1 ? "" : "s")}."
                : $"Completed {completed}, {failed} failed.";
        }
        catch (OperationCanceledException) when (bgCt.IsCancellationRequested)
        {
            finalStatus = ActivityStatus.Cancelled;
            finalDetail = $"Cancelled after {completed} step{(completed == 1 ? "" : "s")}.";
            _logger.LogInformation("automation.cancelled_by_shutdown automation={Name}", automation.Name);
        }
        catch (Exception ex)
        {
            finalStatus = ActivityStatus.Failed;
            finalDetail = $"Run failed: {ex.Message}";
            _logger.LogWarning(ex, "automation.run_failed automation={Name}", automation.Name);
        }
        finally
        {
            _activity.Update(
                activityId,
                status: finalStatus,
                completedAt: DateTimeOffset.UtcNow,
                detail: finalDetail);
        }
    }

    private static string NewMessageId() =>
        "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8)).ToLowerInvariant();
}

/// <summary>Result of <see cref="AutomationRunner.StartRunAsync"/>.</summary>
/// <param name="ThreadId">The chat thread the run is playing into.</param>
/// <param name="ActivityId">Activity-log row tracking the run.</param>
public sealed record AutomationRunStart(string ThreadId, string ActivityId);
