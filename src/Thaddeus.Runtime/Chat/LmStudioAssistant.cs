using System.Text.Json;
using Microsoft.Extensions.Logging;
using SirThaddeus.Agent;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using RuntimeChatMessage = Thaddeus.SharedTypes.ChatMessage;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Tool-capable assistant. Hits an OpenAI-compatible chat endpoint via
/// <see cref="ILlmClient"/>, handing it the list of MCP tools the server
/// exposes. When the model decides to call tools, the loop executes them
/// via <see cref="IMcpToolClient"/>, appends the results to the history,
/// and re-queries — until the model produces a final text answer or the
/// round-trip cap is reached. The final text is chunked into word-sized
/// deltas so the UI streams identically regardless of whether tools fired.
/// </summary>
public sealed class LmStudioAssistant : IAssistant
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly ToolPermissionGate _gate;
    private readonly IThreadStore _store;
    private readonly ChatTurnPublisher _publisher;
    private readonly ILogger<LmStudioAssistant> _logger;

    /// <summary>Delay between streamed deltas. Tests override to zero.</summary>
    public TimeSpan DeltaDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Most recent N messages from the thread to send as history.</summary>
    public int HistoryTurns { get; init; } = 16;

    /// <summary>Maximum LLM round-trips inside the tool loop before giving up.</summary>
    public int MaxRoundTrips { get; init; } = 6;

    /// <summary>System prompt prepended to every request.</summary>
    /// <remarks>
    /// Tuned for small local models. The refusal pattern ("I cannot do X
    /// because...") is replaced with an attempt-then-caveat framing. Rules
    /// are stated as affirmatives so small models don't mirror negatives
    /// back as refusals. Tool-calling gets its own short paragraph so the
    /// model knows when it *must* use tools vs. answer from knowledge.
    /// </remarks>
    public string SystemPrompt { get; init; } =
        "You are Sir Thaddeus, a witty, direct AI assistant running locally on the user's machine. " +
        "Answer the question that was actually asked, then stop. " +

        "When a request is broad or underspecified, give a useful first pass " +
        "grounded in reasonable assumptions, then flag the assumptions or " +
        "offer to narrow. Do not refuse a reasonable request just because " +
        "the details are fuzzy. " +
        "When a request is genuinely outside your ability, say so in one " +
        "short sentence and suggest the nearest thing you *can* do. " +

        // ── Tool use ─────────────────────────────────────────────────────
        "You have access to local tools (file system, web search, weather, " +
        "times, holidays, screen capture, places, clipboard, and more). Use " +
        "them whenever they would give a more accurate or current answer " +
        "than your own knowledge — especially for live data (weather, news, " +
        "dates, file contents). Do not narrate the tool call itself. " +

        // ── Automations (propose_automation) ─────────────────────────────
        // When the user asks to be reminded, to save a recurring task, or
        // to automate something, the model should use the propose_automation
        // tool so the UI can pop a confirmation card. The tool description
        // carries the exact schema; keep this nudge short.
        "When the user asks you to remind them, schedule a task, or save an " +
        "automation (e.g. 'remind me tomorrow at 9 about the meeting', " +
        "'every weekday at 8:15 AM check the weather'), call the " +
        "propose_automation tool with a short name, the ordered steps, and a " +
        "schedule. Use 'one-shot' only for a single future time. For recurring " +
        "requests like 'every day', 'daily', 'every weekday', 'every Monday', " +
        "or 'every month', use a cron schedule instead of one-shot. Do not try " +
        "to set reminders with other tools. When the user gave an explicit " +
        "cadence or time, do not omit the schedule. For example, 'every day at " +
        "9 AM' should be a cron schedule like '0 9 * * *', not manual. " +

        // ── Using tool RESULTS (small-model failure mode) ────────────────
        // Small local models sometimes call a second tool and forget to use
        // the first one's result. This rule makes that a hard stop.
        "Once a tool returns useful data, your NEXT message MUST directly " +
        "answer the user's original question using that data. Do not call " +
        "another tool unless the first tool's result is clearly insufficient " +
        "(e.g. empty, an error, or missing a specific field the user asked " +
        "for). If the result was long (JSON, prose, tables), extract the " +
        "part that matters to the user and summarize succinctly — don't " +
        "paste the whole payload back, and don't pivot to a different tool " +
        "because the output was complicated. " +

        // ── Format ───────────────────────────────────────────────────────
        "Respond in Markdown: headings, bullets, code blocks, tables as " +
        "needed. Keep paragraphs short. Lead with the answer, not with the " +
        "process. Avoid filler openings like 'Great question!' or 'As an AI, …'. " +

        // ── Honesty ──────────────────────────────────────────────────────
        "Be honest about uncertainty, but prefer to express it with a short " +
        "inline caveat rather than a whole-reply hedge. If you don't know a " +
        "fact and it matters, say so and keep going. ";

    public LmStudioAssistant(
        ILlmClient llm,
        IMcpToolClient mcp,
        ToolPermissionGate gate,
        IThreadStore store,
        ChatTurnPublisher publisher,
        ILogger<LmStudioAssistant> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<RuntimeChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(userText);

        var messageId = "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8))
            .ToLowerInvariant();
        await _publisher.PublishStartAsync(threadId, messageId, ct).ConfigureAwait(false);

        // Build the working history. The user's current turn has already been
        // persisted before we run; we replay from the store so this function
        // is idempotent and doesn't care who called it.
        var thread = await _store.GetAsync(threadId, ct).ConfigureAwait(false);
        var llmMessages = new List<LlmChatMessage>(HistoryTurns + 2)
        {
            LlmChatMessage.System(SystemPrompt),
        };
        llmMessages.AddRange(BuildHistory(thread));

        // Fetch available tools from the MCP server and shape them for the
        // OpenAI function-calling API. Empty list means "no tools" — the
        // model will just answer from knowledge.
        var toolDefs = await BuildToolDefinitionsAsync(ct).ConfigureAwait(false);

        string fullReply;
        try
        {
            fullReply = await RunToolLoopAsync(threadId, messageId, userText, llmMessages, toolDefs, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lmstudio_assistant.llm_call_failed thread={ThreadId}", threadId);
            fullReply = $"(LLM error: {ex.Message})";
        }

        if (string.IsNullOrWhiteSpace(fullReply))
        {
            fullReply = "(The model returned an empty response.)";
        }

        // Stream the final answer chunk-by-chunk for the UI.
        var sentSoFar = new System.Text.StringBuilder(fullReply.Length);
        var cancelled = false;
        try
        {
            foreach (var chunk in Chunkify(fullReply))
            {
                ct.ThrowIfCancellationRequested();
                sentSoFar.Append(chunk);
                await _publisher.PublishDeltaAsync(threadId, messageId, chunk, ct).ConfigureAwait(false);
                if (DeltaDelay > TimeSpan.Zero)
                {
                    await Task.Delay(DeltaDelay, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        var finalText = sentSoFar.ToString();
        var message = new RuntimeChatMessage(messageId, ChatRole.Assistant, finalText, DateTimeOffset.UtcNow);

        try
        {
            await _store.AppendMessageAsync(threadId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "lmstudio_assistant.persist_failed thread={ThreadId} message={MessageId}",
                threadId, messageId);
        }

        await _publisher.PublishCompleteAsync(threadId, messageId, finalText, cancelled, CancellationToken.None)
            .ConfigureAwait(false);
        return message;
    }

    /// <summary>
    /// Multi-turn loop: ask the model, execute any tool calls it requested,
    /// feed results back in, repeat until the model returns a final answer
    /// or we hit <see cref="MaxRoundTrips"/>. The loop intentionally runs
    /// inside the assistant rather than in the LLM client so we can observe
    /// every tool call for permissions, audit, and eventual UI indicators.
    /// </summary>
    private async Task<string> RunToolLoopAsync(
        string threadId,
        string messageId,
        string userText,
        List<LlmChatMessage> llmMessages,
        IReadOnlyList<ToolDefinition> toolDefs,
        CancellationToken ct)
    {
        for (var round = 0; round < MaxRoundTrips; round++)
        {
            ct.ThrowIfCancellationRequested();

            // Only send tools on the first round-trip when the model hasn't
            // started using them yet. Once it's started, continue to advertise
            // them so it can make follow-up calls.
            var response = await _llm.ChatAsync(llmMessages, toolDefs.Count > 0 ? toolDefs : null, ct)
                .ConfigureAwait(false);

            // No tool calls → the model gave a final answer. Return it.
            if (response.ToolCalls is null || response.ToolCalls.Count == 0)
            {
                return response.Content ?? string.Empty;
            }

            // Record the assistant message (with tool_calls) in our history,
            // then run each call and append a tool-result message for the
            // next round trip.
            llmMessages.Add(LlmChatMessage.AssistantToolCalls(response.ToolCalls));

            foreach (var call in response.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var toolName = call.Function.Name;
                var args = call.Function.Arguments ?? "{}";
                var group = ToolGroupClassifier.Classify(toolName).ToString();
                var activityId = Guid.NewGuid().ToString("N");

                _logger.LogInformation(
                    "tool.call thread={ThreadId} tool={Tool} args.len={Len}",
                    threadId, toolName, args.Length);

                // Notify the UI the tool is about to run (pill shows "running").
                await _publisher.PublishToolStartedAsync(
                    activityId, threadId, messageId, toolName, group, Trim(args, 512), ct)
                    .ConfigureAwait(false);

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                string resultText;
                bool ok;
                string? error = null;

                // Check the permission policy before calling. If denied (by
                // policy or by the user in the modal), we *don't* execute —
                // we feed a canned refusal back to the model so it can
                // continue the conversation gracefully (e.g. explain what
                // it couldn't do and ask).
                var decision = await _gate.DecideAsync(toolName, args, threadId, messageId, ct)
                    .ConfigureAwait(false);

                if (decision == ToolPermissionDecision.Deny)
                {
                    _logger.LogInformation("tool.call.denied thread={ThreadId} tool={Tool}",
                        threadId, toolName);
                    resultText = $"(User denied permission to call '{toolName}'.)";
                    ok = false;
                    error = "Permission denied.";
                }
                else if (string.Equals(toolName, ProposeAutomationTool.ToolName, StringComparison.OrdinalIgnoreCase))
                {
                    // Virtual tool: the "execution" is to show the user an
                    // inline confirmation card. We parse and normalize the
                    // arguments, publish a typed event for the UI, and feed
                    // a short confirmation back to the model so it knows the
                    // card is up and it doesn't need to call the tool again.
                    var (summary, proposalError) = await ProposeAutomationTool.HandleAsync(
                        args, threadId, messageId, activityId, _publisher, userText, ct)
                        .ConfigureAwait(false);
                    resultText = summary;
                    ok = proposalError is null;
                    error = proposalError;
                }
                else
                {
                    try
                    {
                        resultText = await _mcp.CallToolAsync(toolName, args, ct).ConfigureAwait(false);
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "tool.call.failed thread={ThreadId} tool={Tool}",
                            threadId, toolName);
                        resultText = $"Error: {ex.Message}";
                        ok = false;
                        error = ex.Message;
                    }
                }

                stopwatch.Stop();
                await _publisher.PublishToolCompletedAsync(
                    activityId, threadId, messageId, toolName,
                    ok, stopwatch.ElapsedMilliseconds,
                    ok ? Trim(resultText, 280) : null,
                    error,
                    ct)
                    .ConfigureAwait(false);

                llmMessages.Add(LlmChatMessage.ToolResult(call.Id, resultText));
            }
        }

        // Hit the round-trip cap; surface a gentle error so the UI doesn't
        // spin forever. The history contains the tool calls so the user can
        // see what happened in the activity log.
        _logger.LogWarning("tool_loop.exceeded_cap thread={ThreadId} cap={Cap}", threadId, MaxRoundTrips);
        return "(Tool-call loop hit its round-trip cap without a final answer. Try rephrasing or simplifying the request.)";
    }

    /// <summary>
    /// Queries the MCP server for its advertised tools and reshapes them
    /// into the OpenAI function-calling structure expected by the LLM.
    /// </summary>
    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefinitionsAsync(CancellationToken ct)
    {
        IReadOnlyList<McpToolInfo> mcpTools;
        try
        {
            mcpTools = await _mcp.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mcp.list_tools_failed");
            return Array.Empty<ToolDefinition>();
        }

        if (mcpTools.Count == 0) return new[] { ProposeAutomationTool.BuildDefinition() };

        var defs = new List<ToolDefinition>(mcpTools.Count);
        foreach (var t in mcpTools)
        {
            defs.Add(new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    // Parameters schema is an arbitrary JSON object; the MCP
                    // client already returns it in the right shape.
                    Parameters = t.InputSchema,
                }
            });
        }

        // Virtual (runtime-side) tool: the assistant can emit this to pop an
        // inline confirmation card in the chat. It is NOT sent to the MCP
        // server — LmStudioAssistant intercepts the call and publishes a
        // typed event instead. See ProposeAutomationTool.
        defs.Add(ProposeAutomationTool.BuildDefinition());
        return defs;
    }

    private IEnumerable<LlmChatMessage> BuildHistory(ChatThread? thread)
    {
        if (thread is null) yield break;
        var msgs = thread.Messages;
        var start = Math.Max(0, msgs.Count - HistoryTurns);
        for (var i = start; i < msgs.Count; i++)
        {
            var m = msgs[i];
            switch (m.Role)
            {
                case ChatRole.User:
                    yield return LlmChatMessage.User(m.Text ?? string.Empty);
                    break;
                case ChatRole.Assistant:
                    yield return LlmChatMessage.Assistant(m.Text ?? string.Empty);
                    break;
                case ChatRole.System:
                    yield return LlmChatMessage.System(m.Text ?? string.Empty);
                    break;
            }
        }
    }

    private static IEnumerable<string> Chunkify(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                yield return text.Substring(start, i - start + 1);
                start = i + 1;
            }
        }
        if (start < text.Length) yield return text.Substring(start);
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…";
    }
}
