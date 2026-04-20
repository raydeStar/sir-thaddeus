using Microsoft.Extensions.Logging;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Phase 3.4 stub assistant. Generates a deterministic reply by echoing the user's
/// text with a polite preamble, streams it through <see cref="ChatTurnPublisher"/>
/// as small word-sized deltas, and persists the final assistant message to the
/// thread store. Real LLM integration replaces this in a later phase.
/// </summary>
public sealed class StubAssistant : IAssistant
{
    private readonly IThreadStore _store;
    private readonly ChatTurnPublisher _publisher;
    private readonly ILogger<StubAssistant> _logger;

    /// <summary>Delay between streamed deltas. Override in tests for fast runs.</summary>
    public TimeSpan DeltaDelay { get; init; } = TimeSpan.FromMilliseconds(40);

    public StubAssistant(IThreadStore store, ChatTurnPublisher publisher, ILogger<StubAssistant> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates and streams an assistant reply for <paramref name="userText"/> in
    /// the given thread. Persists the final message before returning.
    /// </summary>
    public async Task<ChatMessage> RespondAsync(string threadId, string userText, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(userText);

        var messageId = "msg_" + Convert.ToHexString(Guid.NewGuid().ToByteArray().AsSpan(0, 8))
            .ToLowerInvariant();
        await _publisher.PublishStartAsync(threadId, messageId, ct).ConfigureAwait(false);

        var fullReply = BuildReply(userText);
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
        var message = new ChatMessage(messageId, ChatRole.Assistant, finalText, DateTimeOffset.UtcNow);

        try
        {
            await _store.AppendMessageAsync(threadId, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "stub_assistant.persist_failed thread={ThreadId} message={MessageId}",
                threadId, messageId);
        }

        await _publisher.PublishCompleteAsync(threadId, messageId, finalText, cancelled, CancellationToken.None)
            .ConfigureAwait(false);
        return message;
    }

    private static string BuildReply(string userText)
    {
        var trimmed = userText.Trim();
        if (trimmed.Length > 200) trimmed = trimmed[..200] + "...";
        return $"You said: \"{trimmed}\". (This is a stubbed reply. The real assistant arrives in a later phase.)";
    }

    private static IEnumerable<string> Chunkify(string text)
    {
        // Stream word-by-word with the trailing space attached, so the UI can
        // simply append each delta without inserting whitespace itself.
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
