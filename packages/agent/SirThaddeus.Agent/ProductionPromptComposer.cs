using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;

namespace SirThaddeus.Agent;

/// <summary>
/// Owns deterministic production system/personality prompt composition shared
/// by desktop, headless, and evaluation-only direct calls.
/// </summary>
public static class ProductionPromptComposer
{
    private const string FinalTaskFocusBlock = """

        [TurnFocus:latest_unresolved]
        When the user supplies completed examples, solved items, or reference material followed by an unfinished request, answer only the final unresolved request. Do not re-answer or summarize completed examples unless the user asks. Follow the user's explicit safe output format exactly.
        [/TurnFocus:latest_unresolved]
        """;

    public static string ComposeBaseSystemPrompt(
        string basePrompt,
        DateTimeOffset now,
        string? locationHint = null,
        string? timezone = null,
        string? preferredUnits = null,
        bool offlineMode = false)
    {
        var blocks = new List<string> { BuildDateBlock(now) };
        var locationBlock = BuildLocationBlock(locationHint, timezone, preferredUnits);
        if (!string.IsNullOrEmpty(locationBlock))
            blocks.Add(locationBlock);
        if (offlineMode)
            blocks.Add(BuildOfflineModeBlock());
        blocks.Add(basePrompt ?? string.Empty);
        return string.Join("\n\n", blocks.Where(static block => !string.IsNullOrEmpty(block)));
    }

    public static IReadOnlyList<ChatMessage> ApplyPersonality(
        IReadOnlyList<ChatMessage> source,
        IPersonalityRuntime runtime,
        string? latestUserMessage)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(runtime);

        var messages = source.ToList();
        WrapFirstSystemMessage(messages, runtime);
        if (ExplicitResponseContractDetector.IsNoToolDirectAnswer(latestUserMessage))
            AppendFinalTaskFocus(messages);
        PersonalityFewShotInjector.InjectInPlace(
            messages,
            runtime.Snapshot.Profile.Instructions.FewShotExamples);
        return messages;
    }

    private static string BuildDateBlock(DateTimeOffset today) =>
        $"Today's date is {today:dddd, MMMM d, yyyy} ({today:yyyy-MM-dd}). " +
        "Use this when the user asks about the current date, day of week, " +
        "or relative dates (e.g. \"tomorrow\", \"last week\"). Do not guess " +
        "or rely on your training cutoff.";

    private static string BuildLocationBlock(
        string? locationHint,
        string? timezone,
        string? preferredUnits)
    {
        if (string.IsNullOrWhiteSpace(locationHint))
            return string.Empty;

        var timezoneNote = string.IsNullOrWhiteSpace(timezone)
            ? string.Empty
            : $" Timezone: {timezone.Trim()}.";
        var unitsNote = string.IsNullOrWhiteSpace(preferredUnits)
            ? string.Empty
            : $" Preferred units: {preferredUnits.Trim()}.";
        return
            $"The user's home location is: {locationHint.Trim()}.{timezoneNote}{unitsNote} " +
            "Use this ONLY as the default area when they ask about weather, local " +
            "places, news, or times WITHOUT specifying a location. When the user " +
            "explicitly names a different city (e.g. \"weather in Seattle\"), use " +
            "the city THEY named \u2014 do not ask for clarification or second-guess. " +
            "Pass the location string to weather_geocode and similar location-scoped " +
            "tools verbatim. Do not announce that you know their home location \u2014 " +
            "just use it naturally when they omit one.";
    }

    private static string BuildOfflineModeBlock() =>
        "Offline mode is ON. Do not use web, browser, weather, places, feed, " +
        "holiday, status-check, or other network-backed tools. Work from local " +
        "conversation context, local memory, wiki/files when available, and " +
        "clearly say when a question needs live web access that offline mode is blocking.";

    private static void WrapFirstSystemMessage(List<ChatMessage> messages, IPersonalityRuntime runtime)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (!string.Equals(messages[index].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;
            messages[index] = ChatMessage.System(
                runtime.BuildSystemPrompt(messages[index].Content ?? string.Empty));
            return;
        }
        messages.Insert(0, ChatMessage.System(runtime.BuildSystemPrompt(string.Empty)));
    }

    private static void AppendFinalTaskFocus(List<ChatMessage> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            if (!string.Equals(messages[index].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;
            messages[index] = ChatMessage.System(
                (messages[index].Content ?? string.Empty).TrimEnd() + FinalTaskFocusBlock);
            return;
        }
    }
}
