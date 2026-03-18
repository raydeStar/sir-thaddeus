using System.Text.Json;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Agent.Memory;

public enum MemoryIntentDecision
{
    Inject,
    Suppress,
    Unsure
}

public interface ISmartIntentClassifier
{
    Task<MemoryIntentDecision> ClassifyAsync(string userMessage, CancellationToken ct = default);
}

public sealed class SmartIntentClassifier : ISmartIntentClassifier
{
    private readonly ILlmClient _llm;
    private readonly IAuditLogger? _audit;
    private readonly TimeSpan _timeout;
    private const int MessagePreviewLength = 80;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMilliseconds(750);

    private static readonly string SystemPrompt = """
        You are a memory gating classifier. Your job is to decide whether retrieving the user's personal memories and profile will improve the quality of the answer to their request.

        RULES:
        1. If the user asks about themself (e.g., "what's my name", "what do I like"), or uses personal pronouns indicating they want personalized advice, output "Inject".
        2. If the user asks a purely technical, factual, or general question where personal preferences are irrelevant (e.g., "how to fix this bug", "what is the capital of France"), output "Suppress".
        3. If you do not have a strong signal either way, output "Unsure".
        4. Would personalization improve answer quality?
        
        Respond ONLY with a JSON object in this exact format:
        {
            "decision": "Inject" | "Suppress" | "Unsure"
        }
        """;

    public SmartIntentClassifier(
        ILlmClient llm,
        IAuditLogger? audit = null,
        TimeSpan? timeout = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _audit = audit;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<MemoryIntentDecision> ClassifyAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return MemoryIntentDecision.Unsure;

        var lower = userMessage.Trim().ToLowerInvariant();
        if (LooksLikeDeterministicSuppress(lower))
            return MemoryIntentDecision.Suppress;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var result = await _llm.ChatAsync(
                new[]
                {
                    ChatMessage.System(SystemPrompt),
                    ChatMessage.User(userMessage)
                },
                tools: null,
                maxTokensOverride: 20,
                cancellationToken: cts.Token);

            if (string.IsNullOrWhiteSpace(result.Content))
                return MemoryIntentDecision.Unsure;

            var cleaned = StripCodeFence(result.Content);

            var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.TryGetProperty("decision", out var prop) && prop.ValueKind == JsonValueKind.String)
            {
                var val = prop.GetString();
                if (Enum.TryParse<MemoryIntentDecision>(val, ignoreCase: true, out var dec))
                    return dec;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            WriteAudit("MEMORY_CLASSIFIER_TIMEOUT", userMessage, error: null);
        }
        catch (JsonException ex)
        {
            WriteAudit("MEMORY_CLASSIFIER_FAIL", userMessage, ex.Message);
        }
        catch (Exception ex)
        {
            WriteAudit("MEMORY_CLASSIFIER_FAIL", userMessage, ex.Message);
        }

        return MemoryIntentDecision.Unsure;
    }

    private static bool LooksLikeDeterministicSuppress(string lower)
    {
        if (IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower))
            return true;

        if (UtilityRouter.TryHandle(lower) is not null)
            return true;

        if (IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower) is not null)
            return true;

        return lower.Contains("thank you", StringComparison.Ordinal) ||
               lower.Contains("thanks for helping", StringComparison.Ordinal) ||
               lower.Contains("just wanted to say thanks", StringComparison.Ordinal) ||
               lower.Contains("appreciate your help", StringComparison.Ordinal);
    }

    private void WriteAudit(string action, string userMessage, string? error)
    {
        if (_audit is null)
            return;

        var details = new Dictionary<string, object>
        {
            ["timeout_ms"] = (int)_timeout.TotalMilliseconds,
            ["message_preview"] = Truncate(userMessage, MessagePreviewLength)
        };

        if (!string.IsNullOrWhiteSpace(error))
            details["error"] = error;

        _audit.Append(new AuditEvent
        {
            Actor = "agent",
            Action = action,
            Result = "error",
            Details = details
        });
    }

    private static string StripCodeFence(string raw)
    {
        var trimmed = raw.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstBreak = trimmed.IndexOf('\n');
        if (firstBreak < 0)
            return trimmed.Trim('`', ' ');

        var inner = trimmed[(firstBreak + 1)..];
        var closing = inner.LastIndexOf("```", StringComparison.Ordinal);
        if (closing >= 0)
            inner = inner[..closing];

        return inner.Trim();
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..maxLength] + "...";
    }
}
