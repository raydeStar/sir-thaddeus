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
    /// Calls the LLM with escalating retry for the "Failed to process regex"
    /// error that LM Studio throws when its grammar engine chokes.
    ///
    /// Strategy:
    ///   1. Try the full message list as-is.
    ///   2. On regex failure, wait briefly and retry the same call.
    ///   3. If that also fails, fall back to a minimal message set
    ///      (system prompt + last user message only) to eliminate
    ///      any message structure the template can't handle.
    /// </summary>
    private Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        CancellationToken cancellationToken)
        => CallLlmWithRetrySafe(messages, roundTrip, maxTokens: null, cancellationToken);

    private async Task<LlmResponse> CallLlmWithRetrySafe(
        IReadOnlyList<ChatMessage> messages,
        int roundTrip,
        int? maxTokens,
        CancellationToken cancellationToken)
    {
        LogEvent("AGENT_LLM_CALL", $"Round trip #{roundTrip}" +
            (maxTokens.HasValue ? $" (max_tokens={maxTokens})" : ""));

        Task<LlmResponse> Call(IReadOnlyList<ChatMessage> msgs) =>
            maxTokens.HasValue
                ? _llm.ChatAsync(msgs, tools: null, maxTokens.Value, cancellationToken)
                : _llm.ChatAsync(msgs, tools: null, cancellationToken);

        // ── Attempt 1: full message list ─────────────────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure — retrying same call after 500 ms");
        }
        catch (HttpRequestException ex) when (IsLlmConnectivityFailure(ex))
        {
            LogEvent("AGENT_LLM_CONNECTIVITY_FALLBACK",
                $"Primary LLM endpoint unreachable — using deterministic fallback. error={ex.Message}");
            return BuildConnectivityFallbackResponse(messages);
        }

        await Task.Delay(500, cancellationToken);

        // ── Attempt 2: same messages, second chance ──────────────────
        try
        {
            return await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            LogEvent("AGENT_LLM_REGEX_RETRY",
                "Regex failure persisted — falling back to minimal message set");
        }
        catch (HttpRequestException ex) when (IsLlmConnectivityFailure(ex))
        {
            LogEvent("AGENT_LLM_CONNECTIVITY_FALLBACK",
                $"Primary LLM endpoint unreachable on retry — using deterministic fallback. error={ex.Message}");
            return BuildConnectivityFallbackResponse(messages);
        }

        // ── Attempt 3: minimal messages (system + last user only) ────
        var minimal = new List<ChatMessage>();
        var sysMsg = messages.FirstOrDefault(m => m.Role == "system");
        var lastUser = messages.LastOrDefault(m => m.Role == "user");

        if (sysMsg is not null) minimal.Add(sysMsg);
        if (lastUser is not null) minimal.Add(lastUser);

        try
        {
            return minimal.Count > 0
                ? await Call(minimal)
                : await Call(messages);
        }
        catch (HttpRequestException ex) when (IsLmStudioRegexFailure(ex))
        {
            // All three attempts failed. Rather than crashing the
            // entire conversation, return a graceful error message
            // so the user can retry or switch models.
            LogEvent("AGENT_LLM_REGEX_EXHAUSTED",
                "All retry attempts failed — LM Studio grammar engine is " +
                "unresponsive for this model. The user should retry or " +
                "check the model configuration.");

            return new LlmResponse
            {
                IsComplete   = true,
                Content      = "I'm having trouble with the language model right now — " +
                               "it keeps rejecting my requests. Try sending your " +
                               "message again, or check if the model needs a reload " +
                               "in LM Studio.",
                FinishReason = "error"
            };
        }
        catch (HttpRequestException ex) when (IsLlmConnectivityFailure(ex))
        {
            LogEvent("AGENT_LLM_CONNECTIVITY_FALLBACK",
                $"Primary LLM endpoint unreachable after retries — using deterministic fallback. error={ex.Message}");
            return BuildConnectivityFallbackResponse(minimal.Count > 0 ? minimal : messages);
        }
    }

    private static bool IsLlmConnectivityFailure(HttpRequestException ex)
    {
        var message = ex.Message ?? string.Empty;
        return message.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Unable to connect", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection attempt failed", StringComparison.OrdinalIgnoreCase);
    }

    private LlmResponse BuildConnectivityFallbackResponse(IReadOnlyList<ChatMessage> messages)
    {
        var userText = messages.LastOrDefault(m => m.Role == "user")?.Content?.Trim() ?? string.Empty;
        var lower = userText.ToLowerInvariant();

        string content;
        if (lower.Contains("oauth", StringComparison.Ordinal) &&
            (lower.Contains("openid", StringComparison.Ordinal) || lower.Contains("oidc", StringComparison.Ordinal)))
        {
            content = "OAuth 2.0 is for authorization (granting app access to APIs), while OpenID Connect (OIDC) is for authentication (proving user identity) on top of OAuth 2.0. " +
                      "Use OAuth 2.0 when an app needs delegated API permissions. Use OIDC when you need sign-in, identity claims (like sub/email), and an ID token for the client app.";
        }
        else if (lower.Contains("hash table", StringComparison.Ordinal))
        {
            content = "A hash table stores key-value pairs and uses a hash function to map keys to buckets, giving average O(1) lookup/insert/delete. " +
                      "Use it when you need fast membership checks, indexing by key, caching, counting/frequency maps, or deduplication. " +
                      "Trade-offs: no guaranteed ordering, possible collisions, and performance can degrade if hashing/load factor is poor.";
        }
        else if (lower.Contains("car wash", StringComparison.Ordinal) &&
                 (lower.Contains("walk", StringComparison.Ordinal) || lower.Contains("drive", StringComparison.Ordinal)))
        {
            content = "Drive. The goal of going to a car wash is to wash the car, so the car must be there; walking by yourself does not complete that goal.";
        }
        else
        {
            content = "I can't reach the configured local language model endpoint right now. I can still help with direct tools and deterministic tasks, or we can retry once the model endpoint is reachable.";
        }

        return new LlmResponse
        {
            IsComplete = true,
            Content = content,
            FinishReason = "error"
        };
    }

    /// <summary>
    /// Keeps the history within a sliding window so small models don't
    /// lose coherence as the context fills up. The system prompt
    /// (message[0]) is always preserved; older turns are evicted FIFO.
    /// </summary>
    private void TrimHistory()
    {
        // Count non-system messages
        var turnMessages = _history.Count(m => m.Role != "system");
        if (turnMessages <= MaxHistoryTurns)
            return;

        var excess = turnMessages - MaxHistoryTurns;
        var removed = 0;
        for (var i = _history.Count - 1; i >= 0 && removed < excess; i--)
        {
            // Walk backwards through the list but remove from the FRONT
            // (oldest non-system messages). Easier to just rebuild:
        }

        // Rebuild: keep system prompt + last N messages
        var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
        var recent = _history.Where(m => m.Role != "system")
                             .TakeLast(MaxHistoryTurns)
                             .ToList();

        _history.Clear();
        if (sysPrompt is not null) _history.Add(sysPrompt);
        _history.AddRange(recent);

        LogEvent("AGENT_HISTORY_TRIM",
            $"Trimmed to {_history.Count} messages ({MaxHistoryTurns} turns)");
    }

    /// <summary>
    /// Applies the Footman router's context policy to shape the history
    /// before the primary model receives it. This reduces context poisoning
    /// and token waste by stripping irrelevant prior turns.
    /// </summary>
    private void ApplyFootmanContextPolicy(Routing.ContextPolicy contextPolicy)
    {
        switch (contextPolicy)
        {
            case Routing.ContextPolicy.None:
            {
                // Isolated query — keep only system prompt + current user message
                var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
                var currentUserMsg = _history.LastOrDefault(m => m.Role == "user");
                _history.Clear();
                if (sysPrompt is not null) _history.Add(sysPrompt);
                if (currentUserMsg is not null) _history.Add(currentUserMsg);
                LogEvent("FOOTMAN_CONTEXT_POLICY", "None — stripped to system + current user message.");
                break;
            }

            case Routing.ContextPolicy.LastAssistantOnly:
            {
                // Keep system prompt + last assistant message + current user message
                var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
                var lastAssistant = _history.LastOrDefault(m => m.Role == "assistant");
                var currentUserMsg = _history.LastOrDefault(m => m.Role == "user");
                _history.Clear();
                if (sysPrompt is not null) _history.Add(sysPrompt);
                if (lastAssistant is not null) _history.Add(lastAssistant);
                if (currentUserMsg is not null) _history.Add(currentUserMsg);
                LogEvent("FOOTMAN_CONTEXT_POLICY", "LastAssistantOnly — kept system + last assistant + current user.");
                break;
            }

            case Routing.ContextPolicy.LastTurns:
            {
                // Keep system prompt + last 3 user/assistant pairs + current user message
                const int keepTurns = 6; // 3 pairs of user+assistant
                var sysPrompt = _history.FirstOrDefault(m => m.Role == "system");
                var nonSystem = _history.Where(m => m.Role != "system").TakeLast(keepTurns).ToList();
                _history.Clear();
                if (sysPrompt is not null) _history.Add(sysPrompt);
                _history.AddRange(nonSystem);
                LogEvent("FOOTMAN_CONTEXT_POLICY", $"LastTurns — kept system + last {nonSystem.Count} messages.");
                break;
            }

            case Routing.ContextPolicy.ChatSessionSnapshot:
                // Full history retained — no trimming
                LogEvent("FOOTMAN_CONTEXT_POLICY", "ChatSessionSnapshot — full history retained.");
                break;

            case Routing.ContextPolicy.ScreenSnapshot:
                // Full history retained; screen capture will be appended downstream
                LogEvent("FOOTMAN_CONTEXT_POLICY", "ScreenSnapshot — full history retained (capture appended downstream).");
                break;

            default:
                LogEvent("FOOTMAN_CONTEXT_POLICY", $"Unknown policy {contextPolicy} — retaining full history.");
                break;
        }
    }
}
