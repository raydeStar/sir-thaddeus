using Microsoft.Extensions.Logging;
using SirThaddeus.LlmClient;
using Thaddeus.SharedTypes;
using RuntimeChatMessage = Thaddeus.SharedTypes.ChatMessage;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Phase 9 real assistant. Loads the thread's recent history, hits an
/// OpenAI-compatible chat endpoint via <see cref="ILlmClient"/>, then chunks
/// the response into word-sized deltas and pushes them through
/// <see cref="ChatTurnPublisher"/> so the UI sees the same streaming UX as
/// the stub. Final assistant message is persisted to the store before
/// returning.
///
/// History bound: only the most recent <see cref="HistoryTurns"/> messages
/// from the thread are sent; we keep this small for v1 to stay well below
/// any local model's context window without doing summarisation.
/// </summary>
public sealed class LmStudioAssistant : IAssistant
{
    private readonly ILlmClient _llm;
    private readonly IThreadStore _store;
    private readonly ChatTurnPublisher _publisher;
    private readonly ILogger<LmStudioAssistant> _logger;

    /// <summary>Delay between streamed deltas. Tests override to zero.</summary>
    public TimeSpan DeltaDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>Most recent N messages from the thread to send as history.</summary>
    public int HistoryTurns { get; init; } = 16;

    /// <summary>System prompt prepended to every request.</summary>
    public string SystemPrompt { get; init; } =
        "You are Sir Thaddeus, a polite, helpful local AI butler running on the user's own machine. " +
        "Be concise and direct. If you do not know something, say so plainly.";

    public LmStudioAssistant(
        ILlmClient llm,
        IThreadStore store,
        ChatTurnPublisher publisher,
        ILogger<LmStudioAssistant> logger)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
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

        // Build the LLM request from recent thread history. The user's current
        // turn has already been persisted by ChatApi before we run, so the tail
        // of the thread already includes it.
        var thread = await _store.GetAsync(threadId, ct).ConfigureAwait(false);
        var history = BuildHistory(thread);

        var llmMessages = new List<LlmChatMessage>
        {
            LlmChatMessage.System(SystemPrompt),
        };
        llmMessages.AddRange(history);

        string fullReply;
        try
        {
            var response = await _llm.ChatAsync(llmMessages, tools: null, ct).ConfigureAwait(false);
            fullReply = response.Content ?? string.Empty;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException)
        {
            // Transport failure — let the router decide whether to fall back
            // to the stub. Emit a cancelled-style completion so the UI's
            // streaming spinner clears.
            await _publisher.PublishCompleteAsync(threadId, messageId, string.Empty, cancelled: true,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            // In-protocol failure (model returned 4xx/5xx with body, parse
            // error, etc.) — render as a visible chat message rather than
            // throwing, because retrying won't help.
            _logger.LogWarning(ex, "lmstudio_assistant.llm_call_failed thread={ThreadId}", threadId);
            fullReply = $"(LLM error: {ex.Message})";
        }

        if (string.IsNullOrWhiteSpace(fullReply))
        {
            fullReply = "(The model returned an empty response.)";
        }

        // Stream the reply chunk-by-chunk so the UI animates in. Cancellation
        // mid-stream is reported as cancelled=true on the completion event;
        // whatever was sent so far is still persisted.
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
}
