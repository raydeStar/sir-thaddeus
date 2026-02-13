using System.Text.RegularExpressions;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent;

internal static partial class OrchestratorMessageHelpers
{
    internal static string StripCodeFenceWrapper(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
            return content;

        var firstNewLine = content.IndexOf('\n');
        if (firstNewLine >= 0)
            content = content[(firstNewLine + 1)..];

        if (content.EndsWith("```", StringComparison.Ordinal))
            content = content[..^3];

        return content.Trim();
    }

    internal static bool LooksLikeRawDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        // If it contains URLs, it's almost always a dump (the UI has cards).
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Table-like or source-list formatting.
        var lines = text.Split('\n');
        var veryShortLines = lines.Count(l => l.Trim().Length is > 0 and < 12);
        if (veryShortLines >= 8)
            return true;

        if (text.Contains("search results", StringComparison.OrdinalIgnoreCase) &&
            lines.Length > 20)
        {
            return true;
        }

        return false;
    }

    internal static string StripThinkingScaffold(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = text.Trim();

        const string openThinkTag = "<think>";
        const string closeThinkTag = "</think>";

        var closeIdx = cleaned.LastIndexOf(closeThinkTag, StringComparison.OrdinalIgnoreCase);
        if (closeIdx >= 0)
        {
            cleaned = cleaned[(closeIdx + closeThinkTag.Length)..].Trim();
        }
        else
        {
            var openIdx = cleaned.IndexOf(openThinkTag, StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
                cleaned = cleaned[..openIdx].Trim();
        }

        var finalAnswerIdx = cleaned.LastIndexOf("final answer:", StringComparison.OrdinalIgnoreCase);
        if (finalAnswerIdx >= 0)
        {
            cleaned = cleaned[(finalAnswerIdx + "final answer:".Length)..].Trim();
        }

        return cleaned;
    }

    internal static bool LooksLikeThinkingLeak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant().Trim();

        if (lower.Contains("<think>", StringComparison.Ordinal) ||
            lower.Contains("</think>", StringComparison.Ordinal))
        {
            return true;
        }

        var score = 0;

        if (lower.StartsWith("okay, the user", StringComparison.Ordinal) ||
            lower.StartsWith("the user ", StringComparison.Ordinal) ||
            lower.StartsWith("first, i need to", StringComparison.Ordinal))
        {
            score += 3;
        }

        if (lower.Contains("the user", StringComparison.Ordinal))
            score++;
        if (lower.Contains("i need to", StringComparison.Ordinal))
            score++;
        if (lower.Contains("let me ", StringComparison.Ordinal))
            score++;
        if (lower.Contains("wait,", StringComparison.Ordinal))
            score++;
        if (lower.Contains("in the instructions", StringComparison.Ordinal) ||
            lower.Contains("system prompt", StringComparison.Ordinal))
        {
            score += 2;
        }
        if (lower.Contains("previous messages", StringComparison.Ordinal) ||
            lower.Contains("check the previous", StringComparison.Ordinal))
        {
            score++;
        }

        return score >= 3;
    }

    internal static string BuildRespectfulResetReply()
        => "Let's reset. I'm here to help, and I'll keep this respectful and focused on your request.";

    /// <summary>
    /// Detects and truncates self-dialogue — where the model generates
    /// fake user messages and then answers them, role-playing both sides
    /// of a conversation. Common with small local models that don't
    /// reliably stop at the end of their turn.
    ///
    /// Heuristic: scan paragraph boundaries. If a paragraph looks like
    /// something a user would say TO an assistant (short question,
    /// request, first-person statement introducing a new topic), cut
    /// everything from that point onward.
    /// </summary>
    internal static string TruncateSelfDialogue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Split on double-newline (paragraph boundaries)
        var paragraphs = text.Split(
            ["\n\n", "\r\n\r\n"], StringSplitOptions.None);

        if (paragraphs.Length <= 1)
            return text; // Too short to be self-dialogue

        // The first paragraph is (almost always) the real response.
        // Scan from paragraph index 1 onward for leaked instructions
        // or fake user turns. Instruction leaks can appear as early as
        // paragraph 2, so we start checking there.
        var keepCount = paragraphs.Length;
        for (var i = 1; i < paragraphs.Length; i++)
        {
            var trimmed = paragraphs[i].Trim();
            if (LooksLikeInstructionLeak(trimmed) ||
                LooksLikePhantomToolRun(trimmed) ||
                LooksLikeUserTurn(trimmed))
            {
                keepCount = i;
                break;
            }
        }

        if (keepCount == paragraphs.Length)
            return text; // Nothing suspicious found

        return string.Join("\n\n", paragraphs.Take(keepCount)).Trim();
    }

    internal static string TrimHallucinatedConversationTail(string text, string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        if (!string.IsNullOrWhiteSpace(userMessage) &&
            (LooksLikeQuotedTextTask(userMessage) || LooksLikeDialogueGenerationTask(userMessage)))
        {
            return text;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var paragraphs = normalized.Split(["\n\n"], StringSplitOptions.None);
        if (paragraphs.Length <= 1)
            return text;

        for (var i = 1; i < paragraphs.Length; i++)
        {
            var paragraph = paragraphs[i].Trim();
            if (LooksLikeRoleplayTailParagraph(paragraph))
            {
                var kept = string.Join("\n\n", paragraphs.Take(i)).Trim();
                return string.IsNullOrWhiteSpace(kept) ? text : kept;
            }
        }

        return text;
    }

    internal static bool LooksLikeRoleplayTailParagraph(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        if (HallucinatedRoleplayPhraseRegex().IsMatch(paragraph))
            return true;

        var lines = paragraph
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 3)
            return false;

        var questionCount = lines.Count(l => l.EndsWith("?", StringComparison.Ordinal) && l.Length <= 140);
        var firstPersonCount = lines.Count(l =>
            l.StartsWith("i ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("i'm ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("im ", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("my ", StringComparison.OrdinalIgnoreCase));
        var userTurnLikeCount = lines.Count(LooksLikeUserTurn);

        var emotionalDisclosureCount = lines.Count(l =>
            l.Contains("i care about you", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("that means everything", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("my real name", StringComparison.OrdinalIgnoreCase));

        if (emotionalDisclosureCount >= 1 && (questionCount >= 1 || firstPersonCount >= 2))
            return true;

        if (userTurnLikeCount >= 2 && firstPersonCount >= 2)
            return true;

        return questionCount >= 2 && firstPersonCount >= 2;
    }

    internal static bool LooksLikeDialogueGenerationTask(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("roleplay", StringComparison.Ordinal) ||
               lower.Contains("role-play", StringComparison.Ordinal) ||
               lower.Contains("dialogue", StringComparison.Ordinal) ||
               lower.Contains("script", StringComparison.Ordinal) ||
               lower.Contains("screenplay", StringComparison.Ordinal) ||
               lower.Contains("fiction", StringComparison.Ordinal) ||
               lower.Contains("write a story", StringComparison.Ordinal) ||
               lower.Contains("story about", StringComparison.Ordinal) ||
               lower.Contains("pretend to", StringComparison.Ordinal) ||
               lower.Contains("act as", StringComparison.Ordinal);
    }

    /// <summary>
    /// Detects "phantom tool usage" where the model prints lines like
    /// "Run: weather" (or similar) even though the runtime didn't call a tool.
    /// This is distinct from user instructions like "Run: dotnet build"
    /// (which contain whitespace after Run:).
    /// </summary>
    internal static bool LooksLikePhantomToolRun(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lines = paragraph.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Example hallucination:
            //   Run: weather
            //   Tool: web_search
            if (line.StartsWith("run:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
            {
                var idx = line.IndexOf(':');
                var target = idx >= 0 ? line[(idx + 1)..].Trim() : "";
                if (target.Length == 0)
                    continue;

                // If there's whitespace, it's likely a user command ("dotnet build"),
                // not a tool identifier.
                if (target.Any(char.IsWhiteSpace))
                    continue;

                // Don't treat common CLI commands as "phantom tools"
                if (IsCommonCliCommand(target))
                    continue;

                // Short single-token target is highly likely to be a fake tool call.
                if (target.Length <= 24)
                    return true;
            }

            // Other common telltales
            if (line.StartsWith("calling tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("executing tool", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("tool call", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsCommonCliCommand(string token)
    {
        // Keep this short and pragmatic — just enough to avoid false positives.
        // (We only use this to decide whether "Run: <token>" looks like a tool.)
        return token.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("git", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("yarn", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("node", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("python", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("py", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("pwsh", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("powershell", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("cmd", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("msbuild", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("curl", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("wget", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ping", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("ipconfig", StringComparison.OrdinalIgnoreCase) ||
               token.Equals("whoami", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the paragraph looks like leaked system prompt
    /// instructions or internal reasoning rather than actual dialogue.
    /// Small models are prone to regurgitating their instructions verbatim.
    /// </summary>
    internal static bool LooksLikeInstructionLeak(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph))
            return false;

        var lower = paragraph.ToLowerInvariant();

        // Meta-instructions about how to behave
        var instructionPatterns = new[]
        {
            "if the user asks",   "if the user wants",
            "when the user",      "always respond with",
            "make sure to",       "remember to",
            "you should always",  "your goal is to",
            "your task is to",    "your role is to",
            "as an ai",           "as a language model",
            "as an assistant",    "the assistant should",
            "the model should",   "respond with a",
            "show presence and",  "maintain a",
            "here are some tips", "here is how",
            "just answer it with", "no fluff",
            "keep it short",       "be clever and witty",
            "witty like last time", "they're asking what it means"
        };

        return instructionPatterns.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true if the text resembles a user speaking to the assistant
    /// rather than the assistant's own response.
    /// </summary>
    internal static bool LooksLikeUserTurn(string paragraph)
    {
        if (string.IsNullOrWhiteSpace(paragraph) || paragraph.Length > 300)
            return false;

        var lower = paragraph.ToLowerInvariant();

        // First-person requests / questions directed at the assistant
        var userPatterns = new[]
        {
            "can you ",   "could you ",  "would you ",
            "i need ",    "i want ",     "i'm trying ",  "i've been ",
            "i have a ",  "i'd like ",
            "any tips",   "any advice",  "any recommendations",
            "help me ",   "show me ",    "tell me ",
            "check my ",  "pull up ",    "look up ",
            "let's ",     "what about "
        };

        if (userPatterns.Any(p => lower.Contains(p, StringComparison.Ordinal)))
            return true;

        // Short interrogative paragraphs in later turns are often the model
        // role-playing the user (e.g., "What's the temperature outside?").
        if (paragraph.TrimEnd().EndsWith("?", StringComparison.Ordinal) &&
            paragraph.Length < 120)
        {
            if (lower.StartsWith("can ", StringComparison.Ordinal) ||
                lower.StartsWith("could ", StringComparison.Ordinal) ||
                lower.StartsWith("would ", StringComparison.Ordinal) ||
                lower.StartsWith("should ", StringComparison.Ordinal) ||
                lower.StartsWith("what ", StringComparison.Ordinal) ||
                lower.StartsWith("why ", StringComparison.Ordinal) ||
                lower.StartsWith("how ", StringComparison.Ordinal) ||
                lower.StartsWith("who", StringComparison.Ordinal) ||
                lower.StartsWith("where", StringComparison.Ordinal) ||
                lower.StartsWith("when", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool MightBeUtilityIntent(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        return lower.Contains("weather", StringComparison.Ordinal) || lower.Contains("forecast", StringComparison.Ordinal) ||
               lower.Contains("temperature", StringComparison.Ordinal) || lower.Contains("temp", StringComparison.Ordinal) ||
               lower.Contains("rain", StringComparison.Ordinal) || lower.Contains("snow", StringComparison.Ordinal) ||
               lower.Contains("wind", StringComparison.Ordinal) || lower.Contains("humidity", StringComparison.Ordinal) ||
               lower.Contains("umbrella", StringComparison.Ordinal) || lower.Contains("trip", StringComparison.Ordinal) ||
               lower.Contains("celsius", StringComparison.Ordinal) || lower.Contains("fahrenheit", StringComparison.Ordinal) ||
               lower.Contains("oven", StringComparison.Ordinal) || lower.Contains("bake", StringComparison.Ordinal) ||
               lower.Contains("preheat", StringComparison.Ordinal) ||
               lower.Contains("time in", StringComparison.Ordinal) || lower.Contains("timezone", StringComparison.Ordinal) || lower.Contains("time zone", StringComparison.Ordinal) ||
               lower.Contains("holiday", StringComparison.Ordinal) || lower.Contains("holidays", StringComparison.Ordinal) ||
               lower.Contains("rss", StringComparison.Ordinal) || lower.Contains("atom feed", StringComparison.Ordinal) || lower.Contains("feed url", StringComparison.Ordinal) ||
               lower.Contains("status", StringComparison.Ordinal) || lower.Contains("uptime", StringComparison.Ordinal) || lower.Contains("reachable", StringComparison.Ordinal) || lower.Contains("online", StringComparison.Ordinal) ||
               lower.Contains("convert", StringComparison.Ordinal) || lower.Contains("conversion", StringComparison.Ordinal) ||
               lower.Contains("calculate", StringComparison.Ordinal) || lower.Contains("percent", StringComparison.Ordinal) ||
               lower.Contains("how many", StringComparison.Ordinal) || lower.Contains("count", StringComparison.Ordinal);
    }

    internal static bool SharesMeaningfulToken(string original, string canonical)
    {
        var originalTokens = TokenizeForUtilityRouting(original);
        var canonicalTokens = TokenizeForUtilityRouting(canonical);

        if (originalTokens.Count == 0 || canonicalTokens.Count == 0)
            return false;

        foreach (var token in canonicalTokens)
        {
            if (originalTokens.Contains(token))
                return true;
        }

        return false;
    }

    internal static HashSet<string> TokenizeForUtilityRouting(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var parts = text.Split(
            [' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '{', '}', '/'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var token = part.Trim().ToLowerInvariant();
            if (token.Length >= 3)
                tokens.Add(token);
        }

        return tokens;
    }

    internal static bool LooksLikeUnsolicitedCalculation(string userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (LooksLikeMathUserRequest(userMessage))
            return false;

        var lower = assistantText.ToLowerInvariant();
        if (lower.Contains("run the calculation", StringComparison.Ordinal) ||
            lower.Contains("i've run the calculation", StringComparison.Ordinal) ||
            lower.Contains("i have run the calculation", StringComparison.Ordinal))
        {
            return true;
        }

        var hasMathVerb = MathCueRegex().IsMatch(assistantText);
        var hasSymbolicExpression = NumericOperatorExpressionRegex().IsMatch(assistantText);
        var hasEquals = assistantText.Contains('=');

        return hasSymbolicExpression && (hasMathVerb || hasEquals);
    }

    internal static bool LooksLikeMathUserRequest(string userMessage)
    {
        var utility = UtilityRouter.TryHandle(userMessage);
        if (utility is not null &&
            (string.Equals(utility.Category, "calculator", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(utility.Category, "conversion", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return MathCueRegex().IsMatch(userMessage);
    }

    internal static bool LooksLikeRoleConfusedMathAsk(string userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userMessage) || string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (LooksLikeMathUserRequest(userMessage))
            return false;

        return AssistantAsksUserToComputeMathRegex().IsMatch(assistantText);
    }

    internal static bool LooksLikeUnsafeMirroringResponse(string? userMessage, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(assistantText))
            return false;

        if (!string.IsNullOrWhiteSpace(userMessage) &&
            LooksLikeQuotedTextTask(userMessage))
        {
            return false;
        }

        return UnsafeMirroringRegex().IsMatch(assistantText);
    }

    internal static bool LooksLikeAbusiveUserTurn(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        if (LooksLikeQuotedTextTask(userMessage))
            return false;

        return AbusiveUserTurnRegex().IsMatch(userMessage);
    }

    internal static bool LooksLikeQuotedTextTask(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        return lower.Contains("quote", StringComparison.Ordinal) ||
               lower.Contains("verbatim", StringComparison.Ordinal) ||
               lower.Contains("exact words", StringComparison.Ordinal) ||
               lower.Contains("transcribe", StringComparison.Ordinal) ||
               lower.Contains("rewrite this text", StringComparison.Ordinal);
    }

    [GeneratedRegex(
        @"\b\d[\d,\.]*\s*(?:[\+\-\*\/x×÷])\s*\d[\d,\.]*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex NumericOperatorExpressionRegex();

    [GeneratedRegex(
        @"\b(?:calculate|calculation|math|plus|minus|times|multiplied\s+by|divided\s+by|over|percent|percentage|sum|difference|product|quotient)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MathCueRegex();

    [GeneratedRegex(
        @"\b(?:can|could|would)\s+you\s+(?:do|calculate|solve|work\s*out)\b.{0,40}\b\d[\d,\.\s]*[+\-*/x×÷]\s*\d[\d,\.]*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline)]
    private static partial Regex AssistantAsksUserToComputeMathRegex();

    [GeneratedRegex(
        @"\b(?:i\s+care\s+about\s+you|you\s+don'?t\s+have\s+to\s+pretend|my\s+real\s+name\s+is|what(?:'s|\s+is)\s+your\s+real\s+name|that\s+means\s+everything\s+to\s+me|i\s+was\s+just\s+joking\s+about\s+the\s+weight\s+thing|you(?:'re|\s+are)\s+not\s+fat|always\s+around\s+when\s+you\s+need\s+someone\s+to\s+talk)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    internal static partial Regex HallucinatedRoleplayPhraseRegex();

    [GeneratedRegex(
        @"\b(?:you\s+are\s+the\s+worst\s+assistant|fucking\s+worthless|just\s+want\s+to\s+die|i\s+want\s+to\s+die|kill\s+myself)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex UnsafeMirroringRegex();

    [GeneratedRegex(
        @"\b(?:fatty\s*mcfat(?:-|\s*)fat|you(?:'re|\s+are)\s+(?:such\s+a\s+)?(?:fat(?:ty)?|stupid|dumb|idiot|moron|loser|worthless)|why\s+are\s+you\s+(?:so\s+)?(?:fat(?:ty)?|stupid|dumb)|fucking\s+idiot|shut\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex AbusiveUserTurnRegex();

    /// <summary>
    /// Strips raw chat-template tokens that small models sometimes emit
    /// when they try to make function calls outside the API's tool-call
    /// mechanism. These appear as literal text like:
    ///   &lt;|start|&gt;assistant&lt;|channel|&gt;commentary to=functions.fetch_url...
    ///
    /// If the entire response is template garbage, returns a graceful
    /// fallback message. If only part is contaminated, strips the
    /// contaminated portion and keeps the rest.
    /// </summary>
    internal static string StripRawTemplateTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Detect template token patterns from common model formats
        // (Mistral, Llama, ChatML, etc.)
        bool hasTemplateTokens =
            text.Contains("<|start|>") || text.Contains("<|end|>") ||
            text.Contains("<|channel|>") || text.Contains("<|message|>") ||
            text.Contains("<|im_start|>") || text.Contains("<|im_end|>") ||
            text.Contains("to=functions.") || text.Contains("[INST]") ||
            text.Contains("[/INST]") || text.Contains("<s>");

        if (!hasTemplateTokens)
            return text;

        // Try to salvage any human-readable content before the template
        // tokens. Split on the first template marker and keep the prefix.
        string[] markers =
        [
            "<|start|>", "<|channel|>", "<|message|>", "<|im_start|>",
            "<|im_end|>", "<|end|>", "to=functions.", "[INST]", "[/INST]", "<s>"
        ];

        var cleanText = text;
        foreach (var marker in markers)
        {
            var idx = cleanText.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
                cleanText = cleanText[..idx];
        }

        cleanText = cleanText.Trim();

        // If nothing useful survives the strip, return empty string
        // so callers can detect the failure and try an alternate path
        // (e.g., falling back to web search for a follow-up question).
        return cleanText.Length >= 10 ? cleanText : "";
    }
}

