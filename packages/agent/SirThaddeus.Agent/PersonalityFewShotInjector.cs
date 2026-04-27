using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.Agent;

/// <summary>
/// Splices personality few-shot examples into a chat-message list just
/// after the leading run of system messages. The few-shots sit between
/// "you are X" and the real conversation so the model sees the voice it
/// should imitate before its first turn.
///
/// <para>Both the pipeline's <c>PersonalityInjectionStep</c> and the
/// tool-loop executor call this helper, so if the insertion rule ever
/// needs to change it changes in one place.</para>
/// </summary>
public static class PersonalityFewShotInjector
{
    /// <summary>
    /// Mutates <paramref name="messages"/> in place. Inserts a
    /// <c>user</c> + <c>assistant</c> pair per example, skipping any
    /// entry where either side is blank. No-op when the list is null or
    /// empty.
    /// </summary>
    public static void InjectInPlace(
        List<ChatMessage> messages,
        IReadOnlyList<PersonalityFewShotExample>? examples)
    {
        if (messages is null) throw new ArgumentNullException(nameof(messages));
        if (examples is null || examples.Count == 0) return;

        // Find where the leading system-prompt run ends — few-shots go
        // immediately after that so they don't disturb the system
        // preamble but still precede the real chat history.
        var insertAt = 0;
        while (insertAt < messages.Count &&
               string.Equals(messages[insertAt].Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            insertAt++;
        }

        foreach (var example in examples)
        {
            if (string.IsNullOrWhiteSpace(example.User) ||
                string.IsNullOrWhiteSpace(example.Assistant))
                continue;

            messages.Insert(insertAt++, ChatMessage.User(example.User.Trim()));
            messages.Insert(insertAt++, ChatMessage.Assistant(example.Assistant.Trim()));
        }
    }
}
