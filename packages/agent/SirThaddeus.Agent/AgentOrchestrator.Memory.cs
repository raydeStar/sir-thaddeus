using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{

    /// <summary>
    /// Mutates history[0] in-place to append the memory pack text.
    /// Used for the tool loop where the same history list is reused
    /// across multiple LLM round-trips.
    /// </summary>
    private static void InjectMemoryIntoHistoryInPlace(
        List<ChatMessage> history, string memoryPackText)
    {
        if (string.IsNullOrWhiteSpace(memoryPackText))
            return;

        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role == "system")
            {
                history[i] = ChatMessage.System(
                    (history[i].Content ?? "") + memoryPackText);
                return;
            }
        }
    }

    private static void InjectPersonalityAnchorIntoHistoryInPlace(
        List<ChatMessage> history,
        string anchorText,
        string turnTag)
    {
        if (string.IsNullOrWhiteSpace(anchorText) || string.IsNullOrWhiteSpace(turnTag))
            return;

        var marker = $"system:personality_anchor:v1:{turnTag}";

        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Role != "system")
                continue;

            var content = history[i].Content ?? "";
            if (content.Contains(marker, StringComparison.Ordinal))
                return; // idempotent for this turn

            content = StripPersonalityAnchors(content);
            var updated = string.IsNullOrWhiteSpace(content)
                ? anchorText.Trim()
                : $"{content.TrimEnd()}\n\n{anchorText.Trim()}";

            history[i] = ChatMessage.System(updated);
            return;
        }

        history.Insert(0, ChatMessage.System(anchorText.Trim()));
    }

    internal static void InjectFewShotExamplesInPlace(
        List<ChatMessage> history,
        IReadOnlyList<PersonalityEngine.Profiles.PersonalityFewShotExample>? examples)
    {
        if (examples is null || examples.Count == 0)
            return;

        // Find where the system prompt ends. It's usually history[0].
        var insertionIndex = 0;
        while (insertionIndex < history.Count && history[insertionIndex].Role == "system")
        {
            insertionIndex++;
        }

        foreach (var example in examples)
        {
            if (string.IsNullOrWhiteSpace(example.User) || string.IsNullOrWhiteSpace(example.Assistant))
                continue;

            history.Insert(insertionIndex, ChatMessage.User(example.User.Trim()));
            insertionIndex++;
            history.Insert(insertionIndex, ChatMessage.Assistant(example.Assistant.Trim()));
            insertionIndex++;
        }
    }

    private static string StripPersonalityAnchors(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return content;

        const string openToken = "[PERSONALITY_ANCHOR ";
        const string closeToken = "[/PERSONALITY_ANCHOR]";

        var output = content;
        while (true)
        {
            var start = output.IndexOf(openToken, StringComparison.Ordinal);
            if (start < 0)
                break;

            var end = output.IndexOf(closeToken, start, StringComparison.Ordinal);
            if (end < 0)
                break;

            end += closeToken.Length;
            output = output.Remove(start, end - start);
        }

        return output.Trim();
    }

    private static List<ChatMessage> InjectModeIntoSystemPrompt(
        List<ChatMessage> history, string modeSuffix)
    {
        var copy = new List<ChatMessage>(history.Count);
        var injected = false;

        foreach (var msg in history)
        {
            if (!injected && msg.Role == "system")
            {
                copy.Add(ChatMessage.System((msg.Content ?? "") + modeSuffix));
                injected = true;
            }
            else
            {
                copy.Add(msg);
            }
        }

        // If there was no system message (shouldn't happen), prepend one
        if (!injected)
            copy.Insert(0, ChatMessage.System(modeSuffix));

        return copy;
    }

    private static bool IsErrorResponse(string? result) =>
        Agent.Tools.ToolAliasResolver.IsErrorResponse(result);

    private static bool IsUnknownToolError(string payload, string requestedTool) =>
        Agent.Tools.ToolAliasResolver.IsUnknownToolError(payload, requestedTool);
}
