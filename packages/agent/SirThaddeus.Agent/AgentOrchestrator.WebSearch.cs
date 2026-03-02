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

    /// <summary>
    /// Builds a readable extractive summary from raw search results
    /// when the LLM can't summarize (regex engine failure, timeout,
    /// etc.). Includes both the source title and the article excerpt
    /// so the user gets actual content, not just homepage headlines.
    ///
    /// Tool output format:
    ///   1. "Title" — source.com
    ///      Excerpt text up to ~1000 chars...
    ///
    /// The excerpts are the real value — article content already
    /// fetched by ContentExtractor. Truncated to ~300 chars each
    /// here to keep the fallback response a reasonable length.
    /// </summary>
    private static string BuildExtractiveSummary(string toolResult, string query)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return $"I found some results for \"{query}\" but couldn't generate a summary. " +
                   "The source links should be visible below.";

        // Strip the SOURCES_JSON section (UI-only metadata).
        var jsonIdx = toolResult.IndexOf(
            "<!-- SOURCES_JSON -->", StringComparison.Ordinal);
        var contentPart = jsonIdx > 0 ? toolResult[..jsonIdx] : toolResult;

        // Parse numbered entries with their indented excerpts.
        // Format:
        //   1. "Title" — source
        //      Excerpt paragraph...
        var lines = contentPart.Split('\n');
        var entries = new List<(string Title, string Source, string Excerpt)>();
        string? currentTitle = null;
        string? currentSource = null;
        var excerptBuilder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            // Skip instruction lines baked into the tool output.
            if (IsInstructionLine(trimmed)) continue;

            // Numbered entry: "1. "Title" — source"
            if (trimmed.Length > 3 && char.IsDigit(trimmed[0]) &&
                (trimmed[1] == '.' || (char.IsDigit(trimmed[1]) && trimmed[2] == '.')))
            {
                // Save previous entry
                if (currentTitle != null)
                    entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

                excerptBuilder.Clear();

                // Parse: remove number prefix, extract title and source
                var dotIdx = trimmed.IndexOf('.');
                var body = trimmed[(dotIdx + 1)..].Trim();

                var dashIdx = body.IndexOf(" — ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    currentTitle  = body[..dashIdx].Trim().Trim('"');
                    currentSource = body[(dashIdx + 3)..].Trim();
                }
                else
                {
                    currentTitle  = body.Trim('"');
                    currentSource = "";
                }
            }
            else if (currentTitle != null && line.StartsWith("   "))
            {
                // Indented excerpt line — append to current entry
                if (excerptBuilder.Length < 300)
                {
                    if (excerptBuilder.Length > 0) excerptBuilder.Append(' ');
                    excerptBuilder.Append(trimmed);
                }
            }
        }

        // Don't forget the last entry
        if (currentTitle != null)
            entries.Add((currentTitle, currentSource ?? "", excerptBuilder.ToString().Trim()));

        if (entries.Count == 0)
            return $"I found some results for \"{query}\" but couldn't generate a summary. " +
                   "The source links should be visible below.";

        var sb = new StringBuilder();
        sb.AppendLine($"Here's what I found for \"{query}\":");
        sb.AppendLine();

        foreach (var (title, source, excerpt) in entries.Take(5))
        {
            var attribution = string.IsNullOrWhiteSpace(source) ? "" : $" ({source})";
            sb.AppendLine($"**{title}**{attribution}");

            if (!string.IsNullOrWhiteSpace(excerpt))
            {
                // Trim to a clean sentence boundary if possible
                var trimmedExcerpt = TrimToSentence(excerpt, 280);
                sb.AppendLine(trimmedExcerpt);
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Returns true if the line is a prompt instruction baked into
    /// the search tool output (not actual search content).
    /// </summary>
    private static bool IsInstructionLine(string trimmed) =>
        trimmed.StartsWith("Synthesize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Summarize", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Cross-reference", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("Lead with", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("No URLs", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("ONLY state", StringComparison.OrdinalIgnoreCase) ||
        trimmed.StartsWith("If a detail", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trims text to approximately <paramref name="maxChars"/> at a
    /// sentence boundary (period, question mark, exclamation mark).
    /// Falls back to a word boundary if no sentence end is found.
    /// </summary>
    private static string TrimToSentence(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;

        // Look for the last sentence-ending punctuation before maxChars
        var window = text[..maxChars];
        var lastEnd = Math.Max(
            Math.Max(window.LastIndexOf(". "), window.LastIndexOf("? ")),
            window.LastIndexOf("! "));

        if (lastEnd > maxChars / 2)
            return text[..(lastEnd + 1)];

        // No good sentence boundary — break at a word boundary
        var lastSpace = window.LastIndexOf(' ');
        return lastSpace > maxChars / 2
            ? text[..lastSpace] + "..."
            : text[..maxChars] + "...";
    }

    // ─────────────────────────────────────────────────────────────────
    // Follow-Up Enrichment
    //
    // When the user asks to go deeper on a topic from a previous search,
    // fetch full article content from the URLs we already know about.
    // This avoids the shallow-search-again pattern and lets the LLM
    // cross-reference sources with actual article text, not snippets.
    // ─────────────────────────────────────────────────────────────────

    private const string SourcesJsonDelimiter  = "<!-- SOURCES_JSON -->";
    private const string BrowseToolName        = "browser_navigate";
    private const string BrowseToolNameAlt     = "BrowserNavigate";
    private const int    MaxFollowUpUrls       = 2;
    private const int    MaxArticleChars       = 3000;

    /// <summary>
    /// Extracts source URLs and titles from a web search tool result
    /// that contains a <c>&lt;!-- SOURCES_JSON --&gt;</c> section.
    /// Returns an empty list if the delimiter is missing or the JSON
    /// is malformed.
    /// </summary>
    private static List<(string Url, string Title)> ParseSourceUrls(string toolResult)
    {
        var sources = new List<(string Url, string Title)>();
        if (string.IsNullOrWhiteSpace(toolResult))
            return sources;

        var delimIdx = toolResult.IndexOf(
            SourcesJsonDelimiter, StringComparison.Ordinal);
        if (delimIdx < 0)
            return sources;

        var jsonPart = toolResult[(delimIdx + SourcesJsonDelimiter.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(jsonPart))
            return sources;

        try
        {
            using var doc = JsonDocument.Parse(jsonPart);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return sources;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var url   = item.TryGetProperty("url", out var u) ? u.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                if (!string.IsNullOrWhiteSpace(url))
                    sources.Add((url!, title ?? ""));
            }
        }
        catch
        {
            // Malformed JSON — not worth crashing over. Return what we have.
        }

        return sources;
    }

    private static string StripSourcesJsonSection(string toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return "";

        var idx = toolResult.IndexOf(SourcesJsonDelimiter, StringComparison.Ordinal);
        return idx >= 0
            ? toolResult[..idx].TrimEnd()
            : toolResult.TrimEnd();
    }

    private static bool LooksLikeFollowUpDepthRequest(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var asksForMore =
            lower.Contains("tell me more") ||
            lower.Contains("more info") ||
            lower.Contains("more information") ||
            lower.Contains("more detail") ||
            lower.Contains("more details") ||
            lower.Contains("more about") ||
            lower.Contains("more on") ||
            lower.Contains("go deeper") ||
            lower.Contains("dig into") ||
            lower.Contains("elaborate") ||
            lower.Contains("expand on") ||
            lower.StartsWith("more ");

        if (!asksForMore)
            return false;

        // Prefer strong follow-up signals so we don't hijack legitimate
        // standalone searches like "more efficient sorting algorithms".
        var pointsAtPriorContext =
            lower.Contains("this ") ||
            lower.Contains("that ") ||
            lower.Contains("it ") ||
            lower.Contains("these ") ||
            lower.Contains("those ");

        return pointsAtPriorContext || lower.Contains("tell me more") || lower.StartsWith("more ");
    }

    private static IReadOnlyList<string> ExtractFollowUpKeywords(string text)
    {
        var normalized = NormalizeQueryText(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return [];

        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(Math.Min(tokens.Length, 8));

        foreach (var t in tokens)
        {
            var lower = t.ToLowerInvariant();
            if (IsBannedSearchToken(lower))
                continue;

            // Follow-up boilerplate (keep topical nouns, drop meta)
            if (lower is
                "more" or "info" or "information" or "detail" or "details" or
                "news" or "headline" or "headlines" or
                "story" or "article" or "source" or "sources" or
                "today" or "week" or "month" or "year" or
                "latest" or "recent" or "recently" or "breaking")
                continue;

            kept.Add(lower);
            if (kept.Count >= 6)
                break;
        }

        return kept;
    }

    private static List<(string Url, string Title)> PickRelevantSources(
        string userMessage,
        IReadOnlyList<(string Url, string Title)> sources,
        int maxUrls)
    {
        if (sources.Count == 0)
            return [];

        var keywords = ExtractFollowUpKeywords(userMessage);
        if (keywords.Count == 0)
            return [];

        int Score(string title)
        {
            var tl = (title ?? "").ToLowerInvariant();
            var score = 0;
            foreach (var k in keywords)
            {
                if (tl.Contains(k, StringComparison.OrdinalIgnoreCase))
                    score++;
            }
            return score;
        }

        return sources
            .Select(s => (Source: s, Score: Score(s.Title)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Source.Title.Length) // shorter titles tend to be cleaner queries
            .Take(Math.Max(1, maxUrls))
            .Select(x => x.Source)
            .ToList();
    }

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
        if (!LooksLikeFollowUpDepthRequest(userMessage))
            return null;
        if (_searchOrchestrator.Session.LastResults.Count == 0)
            return null;

        var sourcesToFetch = PickRelevantSources(userMessage, _searchOrchestrator.Session.LastResults.Select(r => (r.Url, r.Title)).ToList(), MaxFollowUpUrls);
        if (sourcesToFetch.Count == 0)
            return null;

        LogEvent("AGENT_FOLLOWUP_START",
            $"Fetching {sourcesToFetch.Count} prior source(s) for follow-up");

        var fullText = await FetchArticleContentAsync(
            sourcesToFetch, toolCallsMade, cancellationToken);
        if (string.IsNullOrWhiteSpace(fullText))
            return null;

        // ── Related coverage search ───────────────────────────────────
        // After pulling the primary article(s), do a targeted search by
        // story title to find additional coverage. This helps answer
        // follow-up questions when one source is thin or paywalled.
        var relatedQuery = (sourcesToFetch[0].Title ?? "").Trim();
        if (string.IsNullOrWhiteSpace(relatedQuery))
            relatedQuery = TryParseFirstBrowserNavigateTitle(fullText) ?? "";

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
                            StripSourcesJsonSection(relatedToolResult);
        }

        var instruction = !string.IsNullOrWhiteSpace(relatedToolResult)
            ? WebFollowUpWithRelatedInstruction
            : WebFollowUpInstruction;

        var messagesForSummary = InjectModeIntoSystemPrompt(
            _history, memoryPackText + instruction);
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
            text = BuildExtractiveSummaryFromContent(fullText);
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

        _history.Add(ChatMessage.Assistant(text));
        LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text          = text,
            Success       = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<string?> FetchArticleContentAsync(
        IReadOnlyList<(string Url, string Title)> sourcesToFetch,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        if (sourcesToFetch.Count == 0)
            return null;

        // Fetch articles in parallel via MCP browser_navigate / BrowserNavigate.
        // Try snake_case first (MCP SDK default), fall back to PascalCase.
        var fetchTasks = sourcesToFetch.Select(async source =>
        {
            var args = JsonSerializer.Serialize(new { url = source.Url });
            string? content = null;
            var resolvedToolName = BrowseToolName;

            try
            {
                var redactedInput = ToolCallRedactor.RedactInput(BrowseToolName, args);
                LogEvent("AGENT_TOOL_CALL", $"{BrowseToolName}({redactedInput})");
                content = await _mcp.CallToolAsync(BrowseToolName, args, cancellationToken);
            }
            catch
            {
                // snake_case not found — try PascalCase variant
                try
                {
                    resolvedToolName = BrowseToolNameAlt;
                    var redactedInput = ToolCallRedactor.RedactInput(BrowseToolNameAlt, args);
                    LogEvent("AGENT_TOOL_CALL", $"{BrowseToolNameAlt}({redactedInput})");
                    content = await _mcp.CallToolAsync(BrowseToolNameAlt, args, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogEvent("AGENT_FOLLOWUP_FETCH_FAIL",
                        $"browser_navigate failed for {source.Url}: {ex.Message}");

                    toolCallsMade.Add(new ToolCallRecord
                    {
                        ToolName  = resolvedToolName,
                        Arguments = args,
                        Result    = $"Error: {ex.Message}",
                        Success   = false
                    });

                    return (source.Title, Content: (string?)null, Ok: false);
                }
            }

            toolCallsMade.Add(new ToolCallRecord
            {
                ToolName  = resolvedToolName,
                Arguments = args,
                Result    = content!.Length > 200
                    ? content[..200] + "…"
                    : content,
                Success   = true
            });

            // Truncate each article to keep the total context bounded.
            if (content!.Length > MaxArticleChars)
                content = content[..MaxArticleChars] + "\n[…truncated]";

            return (source.Title, Content: content, Ok: true);
        });

        var results = await Task.WhenAll(fetchTasks);

        var sb = new StringBuilder();
        foreach (var (title, content, ok) in results)
        {
            if (!ok || string.IsNullOrWhiteSpace(content))
                continue;

            // If BrowserNavigate returned a thin wrapper page (common with
            // Google News / RSS redirects), don't pretend we have "full
            // article content" — let the caller fall back to re-searching.
            if (IsLowSignalBrowserNavigateContent(content))
                continue;

            sb.AppendLine($"=== {title} ===");
            sb.AppendLine(content);
            sb.AppendLine();
        }

        var combined = sb.ToString().TrimEnd();
        if (string.IsNullOrWhiteSpace(combined))
            return null;

        LogEvent("AGENT_FOLLOWUP_FETCH_DONE",
            $"Fetched {results.Count(r => r.Ok)} article(s), {combined.Length} chars total");

        return combined;
    }

    private static bool IsLowSignalBrowserNavigateContent(string? content)
    {
        var lower = (content ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(lower))
            return true;

        // If the tool explicitly says it's a basic non-article extraction,
        // require a meaningful word count to treat it as usable.
        var isBasic = lower.Contains("extraction: basic (non-article page)");
        var wc = TryParseBrowserNavigateWordCount(content) ?? 0;

        if (isBasic && wc < 120)
            return true;

        // Google News wrapper pages are usually tiny and useless.
        if (lower.Contains("source: news.google.com") && wc < 300)
            return true;

        return false;
    }

    private static string? TryParseFirstBrowserNavigateTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Title:".Length..].Trim();
            raw = raw.Trim();

            // BrowserNavigate formats as: Title: "..."
            if (raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2)
                raw = raw[1..^1].Trim();

            return string.IsNullOrWhiteSpace(raw) ? null : raw;
        }

        return null;
    }

    private static int? TryParseBrowserNavigateWordCount(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Word Count:", StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = trimmed["Word Count:".Length..].Trim();
            raw = raw.Replace(",", "");

            if (int.TryParse(raw, out var wc))
                return wc;
        }

        return null;
    }

    private static string BuildExtractiveSummaryFromContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "I fetched the source, but couldn't extract usable content.";

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return "I fetched the source, but couldn't extract usable content.";

        var bottomLine = lines[0];
        var details = string.Join('\n', lines.Skip(1).Take(4));

        return string.IsNullOrWhiteSpace(details)
            ? $"Bottom line:\n{bottomLine}"
            : $"Bottom line:\n{bottomLine}\n\nDetails:\n{details}";
    }
}
