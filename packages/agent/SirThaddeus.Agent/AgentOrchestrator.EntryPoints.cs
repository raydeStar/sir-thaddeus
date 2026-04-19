using System.Text.Json;
using SirThaddeus.LlmClient;
using SirThaddeus.Agent.Routing;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{
    private async Task MaybeQueueExplicitContinuationToolCallAsync(
        string lowerIncoming,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var explicitContinuationPrompt =
            lowerIncoming.Contains("anything else", StringComparison.Ordinal) ||
            lowerIncoming.Contains("what else", StringComparison.Ordinal);
        if (!explicitContinuationPrompt || SafeModeEnabled)
            return;

        var recentUserMessages = _history
            .Where(message => message.Role == "user")
            .Select(message => message.Content ?? string.Empty)
            .ToList();
        if (recentUserMessages.Count < 2)
            return;

        var priorUserLower = recentUserMessages[^2].Trim().ToLowerInvariant();
        var priorWasLookupLike =
            IntentFeatureExtractor.LooksLikeWebSearchRequest(priorUserLower) ||
            IntentFeatureExtractor.LooksLikeFactLookup(priorUserLower) ||
            IntentFeatureExtractor.LooksLikeExplicitNewsLookup(priorUserLower) ||
            priorUserLower.Contains("news", StringComparison.Ordinal);
        if (!priorWasLookupLike)
            return;

        var dialogueLocation = _dialogueStore.Get().LocationName;
        var continuationQuery = !string.IsNullOrWhiteSpace(dialogueLocation)
            ? $"{dialogueLocation} more news"
            : "more news";
        var continuationArgs = JsonSerializer.Serialize(new
        {
            query = continuationQuery,
            maxResults = 5,
            recency = "week"
        });

        var continuationToolName = "web_search";
        var continuationToolOk = false;
        string continuationToolResult;
        try
        {
            continuationToolResult = await _mcp.CallToolAsync(
                continuationToolName,
                continuationArgs,
                cancellationToken);
            continuationToolOk = true;
        }
        catch (Exception ex)
        {
            continuationToolResult = $"Tool error: {ex.Message}";
        }

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = continuationToolName,
            Arguments = continuationArgs,
            Result = continuationToolResult,
            Success = continuationToolOk
        });
    }

    private AgentResponse? TryBuildDeterministicPromptResponse(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips)
    {
        string? responseText = null;

        if (LooksLikeFrustratedTroubleshootingVentPrompt(userMessage))
        {
            responseText = BuildCalmTroubleshootingTriageAnswer();
        }
        else if (LooksLikeDownloadRamMythPrompt(userMessage))
        {
            responseText = "You can’t download RAM from the internet. RAM is physical hardware, so the real fixes are: close heavy apps/tabs, disable unnecessary startup apps, and if possible upgrade memory sticks (or use a machine with more RAM). If you want, I can walk through a quick speed-up checklist for your OS.";
        }
        else if (LooksLikeOauthOpenIdPrompt(userMessage))
        {
            responseText = BuildOauthOpenIdDeterministicAnswer();
        }

        if (string.IsNullOrWhiteSpace(responseText))
            return null;

        var response = new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };

        AppendAssistantMessage(response.Text);
        LogEvent("AGENT_RESPONSE", response.Text);
        return response;
    }

    private async Task<AgentResponse?> TryHandleEarlyExplicitToolRequestsAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (TryBuildExplicitFileReadArgs(userMessage, out var earlyFileReadArgs, out var earlyFilePath))
        {
            var blockedResponse = await TryBlockFileAccessIfDeniedAsync(
                ToolNames.FileRead,
                earlyFileReadArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);
            if (blockedResponse is not null)
                return blockedResponse;

            return await ExecuteExplicitFileReadAsync(
                earlyFileReadArgs,
                earlyFilePath,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        if (TryBuildExplicitFileListArgs(userMessage, out var earlyFileListArgs, out var earlyFolderPath))
        {
            var blockedResponse = await TryBlockFileAccessIfDeniedAsync(
                ToolNames.FileList,
                earlyFileListArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);
            if (blockedResponse is not null)
                return blockedResponse;

            return await ExecuteExplicitFileListAsync(
                earlyFileListArgs,
                earlyFolderPath,
                userMessage,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        if (TryBuildExplicitKnowledgeStoreCreateListRoundTripArgs(
                userMessage,
                out var earlyKnowledgeStoreRootId,
                out var earlyKnowledgeStoreRelativePath,
                out _,
                out var earlyKnowledgeStoreListPath,
                out var earlyKnowledgeStoreCreateArgs,
                out var earlyKnowledgeStoreListArgs))
        {
            var blockedResponse = await TryBlockFileAccessIfDeniedAsync(
                "knowledge_store_create_file",
                earlyKnowledgeStoreCreateArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);
            if (blockedResponse is not null)
                return blockedResponse;

            return await ExecuteExplicitKnowledgeStoreCreateListRoundTripAsync(
                earlyKnowledgeStoreRootId,
                earlyKnowledgeStoreRelativePath,
                earlyKnowledgeStoreListPath,
                earlyKnowledgeStoreCreateArgs,
                earlyKnowledgeStoreListArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        if (TryBuildExplicitKnowledgeStoreListRootsArgs(userMessage, out var earlyKnowledgeStoreListRootsArgs))
        {
            return await ExecuteExplicitKnowledgeStoreListRootsAsync(
                earlyKnowledgeStoreListRootsArgs,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        return null;
    }

    private async Task<AgentResponse?> TryBlockGenericFileAccessIfDeniedAsync(
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        return await TryBlockFileAccessIfDeniedAsync(
            ToolNames.FileRead,
            BuildGenericFileAccessPreflightArgs(userMessage),
            toolCallsMade,
            roundTrips,
            cancellationToken);
    }

    private async Task<AgentResponse?> TryBlockFileAccessIfDeniedAsync(
        string toolName,
        string argumentsJson,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (_mcp is not AuditedMcpToolClient auditedClient)
            return null;

        var permission = await auditedClient.PreflightToolPermissionAsync(
            toolName,
            argumentsJson,
            cancellationToken);

        if (permission.Granted)
            return null;

        var denialReason = string.IsNullOrWhiteSpace(permission.DenialReason)
            ? "File access was not approved."
            : permission.DenialReason.Trim();

        var responseText =
            "I need file access approval before I can work with local files or the knowledge store. " +
            "This request was blocked before any file tools were exposed or run. " +
            $"Reason: {denialReason}";

        AppendAssistantMessage(responseText);
        LogEvent("FILE_ACCESS_PREFLIGHT_BLOCKED", $"tool={toolName}, reason={denialReason}");

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips,
            SuppressToolActivityUi = true
        };
    }

    private static string BuildGenericFileAccessPreflightArgs(string userMessage)
    {
        var trimmed = (userMessage ?? string.Empty).Trim();
        if (trimmed.Length > 200)
            trimmed = trimmed[..200];

        return JsonSerializer.Serialize(new
        {
            query = string.IsNullOrWhiteSpace(trimmed) ? "local file access requested" : trimmed
        });
    }

    private async Task<AgentResponse> TryHandleChatFallbackToFileAsync(
        string userMessage,
        string contextualUserMessage,
        Task<IReadOnlyList<ToolDefinition>> toolDefsTask,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        LogEvent("CHAT_FALLBACK_TO_FILE",
            "Chat-only refusal/uncertainty detected — falling back to file tools.");

        var blockedFileAccessResponse = await TryBlockGenericFileAccessIfDeniedAsync(
            contextualUserMessage,
            toolCallsMade,
            roundTrips,
            cancellationToken);
        if (blockedFileAccessResponse is not null)
            return blockedFileAccessResponse;

        if (TryBuildExplicitFileReadArgs(userMessage, out var fallbackFileReadArgs, out var fallbackFilePath) ||
            TryBuildExplicitFileReadArgs(contextualUserMessage, out fallbackFileReadArgs, out fallbackFilePath))
        {
            return await ExecuteExplicitFileReadAsync(
                fallbackFileReadArgs,
                fallbackFilePath,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        if (TryBuildExplicitFileListArgs(userMessage, out var fallbackFileListArgs, out var fallbackFolderPath) ||
            TryBuildExplicitFileListArgs(contextualUserMessage, out fallbackFileListArgs, out fallbackFolderPath))
        {
            return await ExecuteExplicitFileListAsync(
                fallbackFileListArgs,
                fallbackFolderPath,
                contextualUserMessage,
                toolCallsMade,
                roundTrips,
                cancellationToken);
        }

        var fallbackToolsCatalog = await toolDefsTask;
        var filePolicy = PolicyGate.Evaluate(new RouterOutput
        {
            Intent = Intents.FileTask,
            NeedsFileAccess = true,
            RequiredCapabilities = [ToolCapability.FileRead],
            Confidence = 1.0
        });
        var fileTools = FilterKnowledgeStoreToolsIfNeeded(
            PolicyGate.FilterTools(fallbackToolsCatalog, filePolicy),
            contextualUserMessage);

        LogEvent("AGENT_TOOLS_POLICY_FILTERED",
            $"{fileTools.Count} file tool(s) exposed for fallback: [{string.Join(", ", fileTools.Select(t => t.Function.Name))}]");

        return await RunToolLoopAsync(
            fileTools,
            toolCallsMade,
            roundTrips,
            cancellationToken);
    }

    private AgentResponse? TryBuildConnectivityRecoveredResponse(
        Exception ex,
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips)
    {
        if (ex is not HttpRequestException httpRequestException ||
            !IsLlmConnectivityFailure(httpRequestException))
        {
            return null;
        }

        var fallback = BuildConnectivityFallbackResponse(
            [ChatMessage.User(userMessage)]);
        var fallbackText = _postProcessor.SanitizeFinalResponse(
            fallback.Content ?? string.Empty,
            toolCallsMade,
            userMessage);

        if (string.IsNullOrWhiteSpace(fallbackText) &&
            !string.IsNullOrWhiteSpace(fallback.Content))
        {
            fallbackText = fallback.Content;
        }

        if (string.IsNullOrWhiteSpace(fallbackText))
        {
            fallbackText =
                "I can't reach the configured local language model endpoint right now. " +
                "I can still help with direct tools and deterministic tasks, or we can retry once the model endpoint is reachable.";
        }

        AppendAssistantMessage(fallbackText);
        LogEvent("AGENT_CONNECTIVITY_RECOVERED", fallbackText);

        return new AgentResponse
        {
            Text = fallbackText,
            Success = true,
            Error = ex.Message,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }
}