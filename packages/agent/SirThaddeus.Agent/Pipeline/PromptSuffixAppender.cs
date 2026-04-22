using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// Shared helper for pipeline steps that need to append a text block to
/// the system message without duplicating the "find-or-insert" logic in
/// each step.
///
/// <para>Behavior: the helper walks the messages list looking for the
/// first entry whose role is <c>system</c>. If found, its content is
/// replaced with <c>oldContent + suffix</c>. If no system message exists,
/// a new one is prepended containing just the suffix (trimmed).</para>
///
/// <para>The returned list is always a new array — the input is never
/// mutated, matching the record/immutable contract of
/// <see cref="TurnContext"/>.</para>
/// </summary>
internal static class PromptSuffixAppender
{
    public static IReadOnlyList<ChatMessage> Append(
        IReadOnlyList<ChatMessage> messages,
        string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return messages;

        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var combined = (messages[i].Content ?? string.Empty) + suffix;
            var next = messages.ToArray();
            next[i] = ChatMessage.System(combined);
            return next;
        }

        var inserted = new List<ChatMessage>(messages.Count + 1) { ChatMessage.System(suffix.TrimStart()) };
        inserted.AddRange(messages);
        return inserted;
    }
}
