using System.Collections.Frozen;

namespace SirThaddeus.Agent.Orchestration;

/// <summary>
/// A collection of examples to use for nearest-neighbor similarity intent routing.
/// </summary>
public static class IntentExemplarBank
{
    public static readonly FrozenDictionary<string, string[]> ExemplarsByIntent = new Dictionary<string, string[]>
    {
        ["LookupFact"] = [
            "what is the capital of france",
            "how tall is the eiffel tower",
            "who wrote to kill a mockingbird",
            "when did apollo 11 land",
            "find me a bakery nearby",
            "show me some open restaurants",
            "is the grocery store open right now",
            "how many ounces in a cup",
            "convert 100 fahrenheit to celsius"
        ],
        ["LookupNews"] = [
            "what is the latest news",
            "give me the news about AI",
            "what happened in the stock market today",
            "any recent articles on space exploration",
            "tell me the news"
        ],
        ["LookupDeepDive"] = [
            "tell me everything you can about quantum physics",
            "research the history of the roman empire in depth",
            "do a deep dive on how mRNA vaccines work",
            "give me a comprehensive briefing on Left Bank Pastry"
        ],
        ["MemoryWrite"] = [
            "remember that my favorite color is blue",
            "note down that I have a meeting at 3pm",
            "save this for later: project X is delayed",
            "always call me sir",
            "never use emojis when talking to me"
        ],
        ["FileTask"] = [
            "open the config.json file",
            "read the contents of the log folder",
            "what's inside my downloads directory",
            "can you check this script for errors"
        ],
        ["ScreenObserve"] = [
            "what is on my screen right now",
            "look at the active window",
            "can you read the error message on my monitor",
            "what am I looking at"
        ],
        ["SystemExecute"] = [
            "run npm install",
            "execute the build script",
            "open calculator",
            "start the web server"
        ],
        ["ChatOnly"] = [
            "hello",
            "how are you doing today",
            "that's a good point",
            "thanks for the help",
            "you are a very smart AI",
            "a man walks into a bar...",
            "tell me a joke"
        ]
    }.ToFrozenDictionary();
}
