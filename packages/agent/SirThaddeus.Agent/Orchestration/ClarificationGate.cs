namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// Default implementation of the clarification gate that checks per-intent confidence
/// thresholds and required slots.
/// </summary>
public sealed class ClarificationGate : IClarificationGate
{
    // Thresholds configured per domain requirements
    private const double ChatLowRiskThreshold = 0.45;
    private const double SearchThreshold = 0.55;
    private const double HighRiskThreshold = 0.75;

    public ClarificationResponse? TryClarify(IntentDecisionV2 decision)
    {
        var normalizedIntent = NormalizeIntent(decision.Intent);

        if (decision.RequiresClarification && !string.IsNullOrWhiteSpace(decision.ClarificationQuestion))
        {
            return new ClarificationResponse(decision.ClarificationQuestion);
        }

        var threshold = GetThresholdForIntent(normalizedIntent);
        if (decision.Confidence < threshold)
        {
            return new ClarificationResponse(
                $"I'm not quite sure I understood that correctly. Did you mean to {DescribeIntent(normalizedIntent)}?");
        }

        // Validate slots based on the predicted intent
        if (normalizedIntent is "LookupFact" or "LookupNews" or "LookupDeepDive")
        {
            if (decision.Slots is not IntentSlots.SearchSlots searchSlots || string.IsNullOrWhiteSpace(searchSlots.Query))
            {
                return new ClarificationResponse("I think you're asking me to search, but I didn't catch exactly what to search for. Could you clarify?");
            }
        }
        else if (normalizedIntent is "MemoryWrite")
        {
            if (decision.Slots is not IntentSlots.MemoryWriteSlots memorySlots || string.IsNullOrWhiteSpace(memorySlots.Fact))
            {
                return new ClarificationResponse("I know you want me to remember something, but I missed the exact detail. What should I note down?");
            }
        }
        else if (normalizedIntent is "FileTask" or "ScreenObserve")
        {
            // If they want us to look at a file/entity but didn't give an entity target
            if (decision.Slots is not IntentSlots.OpenEntitySlots entitySlots || string.IsNullOrWhiteSpace(entitySlots.EntityIdOrName))
            {
                // We'll allow ScreenObserve to pass if no slots are present, assuming the 'current screen' is implied
                if (normalizedIntent == "FileTask")
                {
                    return new ClarificationResponse("Which file or folder did you want me to look at?");
                }
            }
        }

        return null;
    }

    private static double GetThresholdForIntent(string intent)
    {
        return intent switch
        {
            "ChatOnly" or "GeneralTool" => ChatLowRiskThreshold,
            "LookupFact" or "LookupNews" or "LookupDeepDive" => SearchThreshold,
            "FileTask" or "MemoryWrite" or "SystemExecute" => HighRiskThreshold,
            _ => 0.60 // Default fallback threshold
        };
    }

    private static string NormalizeIntent(string intent)
    {
        return intent switch
        {
            "chat_only" => "ChatOnly",
            "lookup_fact" => "LookupFact",
            "lookup_news" => "LookupNews",
            "lookup_deep_dive" => "LookupDeepDive",
            "memory_write" => "MemoryWrite",
            "file_task" => "FileTask",
            "screen_observe" => "ScreenObserve",
            "system_task" => "SystemExecute",
            "general_tool" => "GeneralTool",
            _ => intent
        };
    }

    private static string DescribeIntent(string intent)
    {
        return intent switch
        {
            "ChatOnly" => "just chat",
            "LookupFact" => "search for some information",
            "LookupNews" => "check the news",
            "LookupDeepDive" => "do a deep dive into a topic",
            "MemoryWrite" => "have me remember something",
            "FileTask" => "work with a file",
            "ScreenObserve" => "look at your screen",
            "SystemExecute" => "run a system command",
            _ => "do something else"
        };
    }
}
