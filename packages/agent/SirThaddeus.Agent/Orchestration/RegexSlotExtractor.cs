using System.Text.RegularExpressions;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// Uses regular expressions and heuristic rules to extract slots for standard intents,
/// bypassing the need for a full LLM call when the query fits known patterns.
/// </summary>
public static partial class RegexSlotExtractor
{
    public static IntentSlots.SearchSlots? TryExtractSearchSlots(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        // If it looks like a local business discovery ("show me a bakery nearby"),
        // the query is essentially the whole message minus action words.
        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower))
        {
            return new IntentSlots.SearchSlots
            {
                Query = userMessage.Trim(),
                Location = IntentFeatureExtractor.HasLocalBusinessProximitySignals(lower) ? "nearby" : null
            };
        }

        // Deep dive checks
        if (IntentFeatureExtractor.LooksLikeDeepDiveLookup(lower))
        {
            var match = DeepDiveRegex().Match(lower);
            if (match.Success)
            {
                return new IntentSlots.SearchSlots
                {
                    Query = match.Groups[1].Value.Trim()
                };
            }
        }

        return null;
    }

    public static IntentSlots.OpenEntitySlots? TryExtractFileSlots(string userMessage)
    {
        var lower = userMessage.Trim().ToLowerInvariant();

        if (IntentFeatureExtractor.LooksLikeFileRequest(lower))
        {
            var match = FileOpenRegex().Match(lower);
            if (match.Success)
            {
                return new IntentSlots.OpenEntitySlots
                {
                    EntityType = "file",
                    EntityIdOrName = match.Groups["file"].Value.Trim()
                };
            }
        }

        return null;
    }

    [GeneratedRegex(@"(?:tell me about|deep dive (?:on|into)|research|explain)\s+(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeepDiveRegex();

    [GeneratedRegex(@"(?:open|read|look at|check)\s+(?:the\s+)?(?<file>[\w\.\-\/\\]+)\s*(?:file|folder|directory)?", RegexOptions.IgnoreCase)]
    private static partial Regex FileOpenRegex();
}
