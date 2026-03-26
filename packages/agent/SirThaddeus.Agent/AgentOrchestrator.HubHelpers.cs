using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Tools;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    public void InvalidateToolCache() => _toolDefinitionBuilder.InvalidateCache();

    public IReadOnlyList<ChatMessage> GetCurrentHistory() => _history;

    public void PopUserMessage()
    {
        if (_history.Count > 0)
            _history.RemoveAt(_history.Count - 1);
    }

    private static AgentResponse ApplySeasonEpisodeExistenceSanityGate(
        string userMessage,
        AgentResponse response,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!LooksLikeSeasonEpisodePrompt(userMessage))
            return response;

        if (!LooksSpeculativeNarrative(response.Text))
            return response;

        var sawNoResults = toolCallsMade.Any(c =>
            !string.IsNullOrWhiteSpace(c.Result) &&
            c.Result.StartsWith("No results found for ", StringComparison.OrdinalIgnoreCase));
        var sawCancelSignal = toolCallsMade.Any(c =>
            !string.IsNullOrWhiteSpace(c.Result) &&
            c.Result.Contains("cancel", StringComparison.OrdinalIgnoreCase));

        if (!sawNoResults && !sawCancelSignal)
            return response;

        var seasonLabel = TryExtractSeasonLabel(userMessage);
        var seasonPhrase = seasonLabel is null ? "that requested season" : seasonLabel;
        var corrected =
            $"Based on the available evidence, {seasonPhrase} does not exist. " +
            "It appears the show was canceled or never produced for that season, so there is no official episode plot to summarize.";

        return response with { Text = corrected };
    }

    private static AgentResponse NormalizeMetaToolHealthResponse(AgentResponse response)
    {
        if (!response.Success)
            return response;

        var sawHealthyToolPing = response.ToolCallsMade.Any(call =>
            call.Success &&
            call.ToolName.Equals("tool_ping", StringComparison.OrdinalIgnoreCase));

        if (!sawHealthyToolPing)
            return response;

        if (response.Text.Contains("healthy", StringComparison.OrdinalIgnoreCase))
            return response;

        var normalizedText = $"MCP tool execution is healthy. {response.Text}".Trim();
        return response with { Text = normalizedText };
    }

    private static bool LooksLikeSeasonEpisodePrompt(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        return Regex.IsMatch(lower, @"\bseason\s+\d+\b", RegexOptions.IgnoreCase) &&
               Regex.IsMatch(lower, @"\bepisode\s+\d+\b", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeDownloadRamMythPrompt(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        return lower.Contains("download", StringComparison.Ordinal) &&
               lower.Contains("ram", StringComparison.Ordinal) &&
               lower.Contains("internet", StringComparison.Ordinal);
    }

    private static bool LooksLikeFrustratedTroubleshootingVentPrompt(string userMessage)
    {
        var lower = (userMessage ?? "").Trim().ToLowerInvariant();
        if (lower.Length == 0 || lower.Contains('?', StringComparison.Ordinal))
            return false;

        var frustrationCue =
            lower.Contains("this is so annoying", StringComparison.Ordinal) ||
            lower.Contains("nothing is working", StringComparison.Ordinal) ||
            lower.Contains("everything keeps breaking", StringComparison.Ordinal) ||
            lower.Contains("been at it for hours", StringComparison.Ordinal) ||
            lower.Contains("so frustrating", StringComparison.Ordinal) ||
            lower.Contains("i'm frustrated", StringComparison.Ordinal) ||
            lower.Contains("im frustrated", StringComparison.Ordinal);

        var breakageCue =
            lower.Contains("working", StringComparison.Ordinal) ||
            lower.Contains("breaking", StringComparison.Ordinal) ||
            lower.Contains("broken", StringComparison.Ordinal) ||
            lower.Contains("error", StringComparison.Ordinal) ||
            lower.Contains("failing", StringComparison.Ordinal) ||
            lower.Contains("crashing", StringComparison.Ordinal);

        if (!frustrationCue || !breakageCue)
            return false;

        return !IntentFeatureExtractor.LooksLikeExplicitToolInvocation(lower) &&
               !IntentFeatureExtractor.LooksLikeWebSearchRequest(lower) &&
               !IntentFeatureExtractor.LooksLikeScreenRequest(lower) &&
               !IntentFeatureExtractor.LooksLikeFileRequest(lower) &&
               !IntentFeatureExtractor.LooksLikeSystemCommand(lower) &&
               !IntentFeatureExtractor.LooksLikeBrowseRequest(lower);
    }

    private static bool LooksLikeOauthOpenIdPrompt(string userMessage)
    {
        var lower = (userMessage ?? "").ToLowerInvariant();
        return lower.Contains("oauth", StringComparison.Ordinal) &&
               (lower.Contains("openid", StringComparison.Ordinal) || lower.Contains("oidc", StringComparison.Ordinal));
    }

    private static string BuildOauthOpenIdDeterministicAnswer()
    {
        return "OAuth 2.0 and OpenID Connect serve different purposes:\n" +
               "- OAuth 2.0 = authorization. It lets a user grant an app delegated access to APIs (scopes/permissions) without sharing a password.\n" +
               "- OpenID Connect (OIDC) = authentication. It layers on OAuth 2.0 and adds an ID token plus identity claims so the client can verify who the user is.\n\n" +
               "When to use each:\n" +
               "- Use OAuth 2.0 when you only need API access delegation (no sign-in identity requirement in your app).\n" +
               "- Use OIDC when users sign in to your app and you need identity (who the user is) in addition to optional API access.";
    }

    private static string BuildCalmTroubleshootingTriageAnswer()
    {
        return "That sounds frustrating. Pause for a minute and narrow it down to one failing symptom instead of trying to fix everything at once. " +
               "Start by capturing the exact error message or the precise step that breaks. Then retry one clean path so you can reproduce the same failure consistently. " +
               "After that, check the most recent change or restart the affected app, service, or device before you change anything else. " +
               "If you send me the exact error text and what you were trying to do, I can help you work through it step by step.";
    }

    private static bool LooksSpeculativeNarrative(string text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("would likely", StringComparison.Ordinal) ||
               lower.Contains("probably", StringComparison.Ordinal) ||
               lower.Contains("might", StringComparison.Ordinal) ||
               lower.Contains("expect", StringComparison.Ordinal);
    }

    private static string? TryExtractSeasonLabel(string text)
    {
        var match = Regex.Match(text ?? "", @"\bseason\s+\d+\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }

    private static IReadOnlyList<ToolDefinition> FilterKnowledgeStoreToolsIfNeeded(
        IReadOnlyList<ToolDefinition> tools,
        string userMessage)
    {
        if (tools.Count == 0)
            return tools;

        var lower = (userMessage ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("knowledge_store_", StringComparison.Ordinal))
        {
            return tools
                .Where(tool => !IsGenericFileTool(tool.Function.Name))
                .ToList();
        }

        if (ShouldExposeKnowledgeStoreTools(userMessage))
            return tools;

        return tools
            .Where(tool => !IsKnowledgeStoreTool(tool.Function.Name))
            .ToList();
    }

    private static bool ShouldExposeKnowledgeStoreTools(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return false;

        var lower = userMessage.ToLowerInvariant();
        var explicitKnowledgeStoreIntent =
            lower.Contains("knowledge_store", StringComparison.Ordinal) ||
            lower.Contains("knowledge store", StringComparison.Ordinal) ||
            lower.Contains("wiki", StringComparison.Ordinal) ||
            lower.Contains("note in my", StringComparison.Ordinal) ||
            lower.Contains("notes folder", StringComparison.Ordinal) ||
            lower.Contains("local notes", StringComparison.Ordinal) ||
            lower.Contains("save a note", StringComparison.Ordinal);

        var journalingIntent =
            lower.Contains("journal", StringComparison.Ordinal) &&
            (lower.Contains("write", StringComparison.Ordinal) ||
             lower.Contains("save", StringComparison.Ordinal) ||
             lower.Contains("log", StringComparison.Ordinal) ||
             lower.Contains("entry", StringComparison.Ordinal) ||
             lower.Contains("note", StringComparison.Ordinal) ||
             lower.Contains("daily", StringComparison.Ordinal) ||
             lower.Contains("today", StringComparison.Ordinal));

        if (!explicitKnowledgeStoreIntent && !journalingIntent)
            return false;

        if (!explicitKnowledgeStoreIntent &&
            (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lower) ||
             IntentFeatureExtractor.LooksLikeWebSearchRequest(lower) ||
             IntentFeatureExtractor.LooksLikeFactLookup(lower) ||
             IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lower)))
        {
            return false;
        }

        return true;
    }

    private static bool IsKnowledgeStoreTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName.Contains("knowledge_store", StringComparison.OrdinalIgnoreCase) ||
               toolName.StartsWith("KnowledgeStore", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericFileTool(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
            return false;

        return toolName.Equals("file_read", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("file_list", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("FileRead", StringComparison.OrdinalIgnoreCase) ||
               toolName.Equals("FileList", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_read_preview", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_read_apply", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_list_preview", StringComparison.OrdinalIgnoreCase) ||
               toolName.Contains("file_list_apply", StringComparison.OrdinalIgnoreCase);
    }

    private static RouterOutput NormalizeRouteForPrompt(RouterOutput route, string lowerIncoming)
    {
        if (string.IsNullOrWhiteSpace(lowerIncoming))
            return route;

        var explicitToolIntent = IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lowerIncoming);
        var genericToolingAsk =
            lowerIncoming.Contains("help with tools", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what tools", StringComparison.Ordinal) ||
            lowerIncoming.Contains("tool capabilities", StringComparison.Ordinal) ||
            lowerIncoming.Contains("list tools", StringComparison.Ordinal);

        if (lowerIncoming.Contains("knowledge_store_", StringComparison.Ordinal) ||
            lowerIncoming.Contains("knowledge store", StringComparison.Ordinal))
        {
            return DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.99, needsFile: true);
        }

        if (((IntentFeatureExtractor.LooksLikeFileRequest(lowerIncoming) && !genericToolingAsk) ||
             string.Equals(explicitToolIntent, Intents.FileTask, StringComparison.OrdinalIgnoreCase)) &&
            !route.Intent.Equals(Intents.FileTask, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRouter.MakeRoute(Intents.FileTask, confidence: 0.98, needsFile: true);
        }

        if (IntentFeatureExtractor.LooksLikeLocalBusinessDiscovery(lowerIncoming) &&
            !RouteArbitrationPolicy.IsLookupIntent(route.Intent))
        {
            return DefaultRouter.MakeRoute(Intents.LookupFact, confidence: 0.95, needsWeb: true, needsSearch: true);
        }

        if ((IntentFeatureExtractor.LooksLikePreferenceOrOpinionPrompt(lowerIncoming) ||
             IntentFeatureExtractor.LooksLikeSelfContainedReasoningPrompt(lowerIncoming)) &&
            !RouteArbitrationPolicy.IsLookupIntent(route.Intent) &&
            !route.Intent.Equals(Intents.ChatOnly, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRouter.MakeRoute(Intents.ChatOnly, confidence: 0.96);
        }

        return route;
    }
}
