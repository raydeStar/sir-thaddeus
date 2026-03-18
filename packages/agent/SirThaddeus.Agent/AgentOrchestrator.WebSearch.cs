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
    private static string TrimDanglingIncompleteEnding(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = text.Trim();
        var lines = new List<string>(cleaned.Split('\n'));
        while (lines.Count > 0)
        {
            var last = lines[^1].Trim();
            if (last.Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            // Token-limited outputs often end in half-built markdown tables.
            if (last.StartsWith("|", StringComparison.Ordinal))
            {
                lines.RemoveAt(lines.Count - 1);
                continue;
            }

            break;
        }

        cleaned = string.Join("\n", lines).Trim();
        if (cleaned.Length == 0)
            return text.Trim();

        var lastChar = cleaned[^1];
        if (lastChar is '.' or '!' or '?' or '"' or '\'' or ')' or ']')
            return cleaned;

        var sentenceEnd = cleaned.LastIndexOfAny(['.', '!', '?']);
        if (sentenceEnd >= 40)
            return cleaned[..(sentenceEnd + 1)].Trim();

        return cleaned.TrimEnd(',', ';', ':', '-', '—').Trim();
    }

    // ─────────────────────────────────────────────────────────────────
    // Web Search Fallback
    //
    // When the chat-only path produces garbage (template tokens, empty
    // response), the user likely asked a follow-up about something the
    // model can't answer from memory alone. Rather than returning a
    // useless "something went sideways" message, we try a web search.
    // This handles the common pattern:
    //   Turn 1: "pull up the news"  → web search → great summary
    //   Turn 2: "whats with X?"     → chat-only  → garbage
    //                               → fallback   → web search for X
    // ─────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────
    // Follow-Up Enrichment
    //
    // When the user asks to go deeper on a topic from a previous search,
    // fetch full article content from the URLs we already know about.
    // This avoids the shallow-search-again pattern and lets the LLM
    // cross-reference sources with actual article text, not snippets.
    // ─────────────────────────────────────────────────────────────────

    private const int    MaxFollowUpUrls       = 2;

    private async Task<string> TryCallWebSearchAsync(
        string query,
        string recency,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Serialize(new
        {
            query,
            maxResults = DefaultWebSearchMaxResults,
            recency
        });

        var toolName = WebSearchToolName;
        var toolOk = false;
        string toolResult;

        try
        {
            var redactedInput = ToolCallRedactor.RedactInput(toolName, args);
            LogEvent("AGENT_TOOL_CALL", $"{toolName}({redactedInput})");
            toolResult = await _mcp.CallToolAsync(toolName, args, cancellationToken);
            toolOk = true;
        }
        catch (Exception ex)
        {
            // Back-compat: some MCP stacks register PascalCase tool names.
            try
            {
                toolName = WebSearchToolNameAlt;
                var redactedInput = ToolCallRedactor.RedactInput(toolName, args);
                LogEvent("AGENT_TOOL_CALL", $"{toolName}({redactedInput})");
                toolResult = await _mcp.CallToolAsync(toolName, args, cancellationToken);
                toolOk = true;
            }
            catch
            {
                toolResult = $"Tool error: {ex.Message}";
            }
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName  = toolName,
            Arguments = args,
            Result    = toolResult,
            Success   = toolOk
        });
        LogEvent("AGENT_TOOL_RESULT", $"{toolName} -> {(toolOk ? "ok" : "error")}");

        return toolResult;
    }

    private async Task<AgentResponse?> TryAnswerFollowUpFromLastSourcesAsync(
        string userMessage,
        string memoryPackText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (!WebSearchFollowUpSupport.LooksLikeFollowUpDepthRequest(userMessage))
            return null;
        if (_searchOrchestrator.Session.LastResults.Count == 0)
            return null;

        var sourcesToFetch = WebSearchFollowUpSupport.PickRelevantSources(userMessage, _searchOrchestrator.Session.LastResults.Select(r => (r.Url, r.Title)).ToList(), MaxFollowUpUrls);
        if (sourcesToFetch.Count == 0)
            return null;

        LogEvent("AGENT_FOLLOWUP_START",
            $"Fetching {sourcesToFetch.Count} prior source(s) for follow-up");

        var fullText = await new WebArticleContentFetcher(_mcp, LogEvent).FetchAsync(
            sourcesToFetch, toolCallsMade, cancellationToken);
        if (string.IsNullOrWhiteSpace(fullText))
            return null;

        // ── Related coverage search ───────────────────────────────────
        // After pulling the primary article(s), do a targeted search by
        // story title to find additional coverage. This helps answer
        // follow-up questions when one source is thin or paywalled.
        var relatedQuery = (sourcesToFetch[0].Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(relatedQuery))
            relatedQuery = WebSearchFollowUpSupport.TryParseFirstBrowserNavigateTitle(fullText) ?? "";

        relatedQuery = relatedQuery.Trim().Trim('"');
        var relatedRecency = (_searchOrchestrator.Session.LastRecency ?? "any") != "any"
            ? _searchOrchestrator.Session.LastRecency!
            : DetectRecencyFallback(userMessage);

        string? relatedToolResult = null;
        if (!string.IsNullOrWhiteSpace(relatedQuery) && relatedQuery.Length >= 8)
        {
            relatedToolResult = await TryCallWebSearchAsync(
                relatedQuery, relatedRecency, toolCallsMade, cancellationToken);

            if (!string.IsNullOrWhiteSpace(relatedToolResult))
            {
                /* Sources now tracked in SearchOrchestrator.Session */
                _searchOrchestrator.Session.LastRecency = relatedRecency;
            }
        }

        roundTrips++;
        var summaryInput = "[Primary article content — reference only, do not display to user]\n" +
                           fullText;

        if (!string.IsNullOrWhiteSpace(relatedToolResult))
        {
            summaryInput += "\n\n[Related coverage search results — reference only, do not display to user]\n" +
                            WebSearchFollowUpSupport.StripSourcesJsonSection(relatedToolResult);
        }

        var instruction = !string.IsNullOrWhiteSpace(relatedToolResult)
            ? WebFollowUpWithRelatedInstruction
            : WebFollowUpInstruction;

        var messagesForSummary = InjectModeIntoSystemPrompt(
            _history, SearchOrchestrator.CombineMemoryAndInstruction(memoryPackText, instruction));
        messagesForSummary.Add(ChatMessage.User(summaryInput));

        var response = await CallLlmWithRetrySafe(
            messagesForSummary, roundTrips, MaxTokensWebSummary, cancellationToken);

        if (string.Equals(response.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
        {
            LogEvent("AGENT_SUMMARY_RETRY_EXPANDED",
                $"Follow-up summary hit token cap ({MaxTokensWebSummary}); retrying with {MaxTokensWebSummaryRetry}.");
            roundTrips++;
            response = await CallLlmWithRetrySafe(
                messagesForSummary, roundTrips, MaxTokensWebSummaryRetry, cancellationToken);
        }

        string text;
        if (response.FinishReason == "error")
        {
            LogEvent("AGENT_SUMMARY_FOLLOWUP_FALLBACK",
                "LLM summary failed — building extractive fallback");
            text = WebSearchFollowUpSupport.BuildExtractiveSummaryFromContent(fullText, userMessage);
        }
        else
        {
            text = StripThinkingScaffold(response.Content ?? "[No response]");
            text = TruncateSelfDialogue(text);

            // Raw dump → rewrite
            if (LooksLikeRawDump(text))
            {
                LogEvent("AGENT_REWRITE", "Follow-up response looked like a raw dump — rewriting");
                var rewriteMessages = new List<ChatMessage>
                {
                    ChatMessage.System(
                        _systemPrompt + " " +
                        "Rewrite the draft into the final answer. " +
                        "Casual tone. Bottom line first. 2-3 short paragraphs. " +
                        "No markdown tables. No URLs. No copied excerpts. " +
                        "Do NOT add facts not present in the draft."),
                    ChatMessage.User(text)
                };

                roundTrips++;
                var rewritten = await CallLlmWithRetrySafe(
                    rewriteMessages, roundTrips, MaxTokensWebSummary, cancellationToken);

                if (string.Equals(rewritten.FinishReason, "length", StringComparison.OrdinalIgnoreCase))
                {
                    LogEvent("AGENT_SUMMARY_REWRITE_RETRY_EXPANDED",
                        $"Rewrite hit token cap ({MaxTokensWebSummary}); retrying with {MaxTokensWebSummaryRetry}.");
                    roundTrips++;
                    rewritten = await CallLlmWithRetrySafe(
                        rewriteMessages, roundTrips, MaxTokensWebSummaryRetry, cancellationToken);
                }

                if (!string.IsNullOrWhiteSpace(rewritten.Content) &&
                    rewritten.FinishReason != "error")
                    text = StripThinkingScaffold(rewritten.Content!);
            }
        }

        text = StripThinkingScaffold(text);
        text = StripRawTemplateTokens(text);
        text = TrimDanglingIncompleteEnding(text);
        if (string.IsNullOrWhiteSpace(text))
            text = "I wasn't able to generate a clean answer for that. " +
                   "Could you try asking a different way?";

        AppendAssistantMessage(text);
        LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

}
