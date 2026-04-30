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
    private readonly bool _allowLlmFallback;
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
        TimeSpan? timeout = null,
        bool allowLlmFallback = true)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _audit = audit;
        _timeout = timeout ?? DefaultTimeout;
        _allowLlmFallback = allowLlmFallback;
    }

    public async Task<MemoryIntentDecision> ClassifyAsync(string userMessage, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return MemoryIntentDecision.Unsure;

        var lower = userMessage.Trim().ToLowerInvariant();
        if (LooksLikeDeterministicInject(lower))
            return MemoryIntentDecision.Inject;

        if (LooksLikeDeterministicSuppress(lower))
            return MemoryIntentDecision.Suppress;

        if (!_allowLlmFallback)
            return MemoryIntentDecision.Unsure;

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

    private static bool LooksLikeDeterministicInject(string lower)
    {
        if (HasExplicitPersonalizationSignal(lower))
            return true;

        return LooksLikePersonalizedDecisionRequest(lower);
    }

    private static bool LooksLikeDeterministicSuppress(string lower)
    {
        if (IntentFeatureExtractor.LooksLikeGreetingOnlyOrSmallTalk(lower))
            return true;

        if (LooksLikeSelfContainedNameRecallPrompt(lower))
            return true;

        if (LooksLikeGenericPuzzleOrAnagramPrompt(lower))
            return true;

        if (LooksLikeGenericMultiQuestionPrompt(lower))
            return true;

        if (IntentFeatureExtractor.HasLocalBusinessProximitySignals(lower) ||
            LooksLikeLocalBusinessDiscoveryRequest(lower))
            return true;

        if (LooksLikeShortGenericFollowUp(lower))
            return true;

        if (IntentFeatureExtractor.LooksLikeLogicPuzzlePrompt(lower))
            return true;

        if (LooksLikeHypotheticalLogicScenario(lower))
            return true;

        if (UtilityRouter.TryHandle(lower) is not null)
            return true;

        if (IntentFeatureExtractor.TryGetExplicitToolInvocationIntent(lower) is not null)
            return true;

        if (lower.Contains("thank you", StringComparison.Ordinal) ||
               lower.Contains("thanks for helping", StringComparison.Ordinal) ||
               lower.Contains("just wanted to say thanks", StringComparison.Ordinal) ||
               lower.Contains("appreciate your help", StringComparison.Ordinal))
        {
            return true;
        }

        if (HasExplicitPersonalizationSignal(lower))
            return false;

        if (LooksLikeGenericImperativeRequest(lower))
            return true;

        return LooksLikePublicInfoOrReasoningPrompt(lower);
    }

    private static bool HasExplicitPersonalizationSignal(string lower)
    {
        return lower.Contains("about me", StringComparison.Ordinal) ||
               lower.Contains("know about me", StringComparison.Ordinal) ||
               lower.Contains("what's my name", StringComparison.Ordinal) ||
               lower.Contains("what is my name", StringComparison.Ordinal) ||
               lower.Contains("what do i like", StringComparison.Ordinal) ||
               lower.Contains("what i like", StringComparison.Ordinal) ||
               lower.Contains("my preferences", StringComparison.Ordinal) ||
               lower.Contains("my profile", StringComparison.Ordinal) ||
               lower.Contains("my favorite", StringComparison.Ordinal) ||
               lower.Contains("my schedule", StringComparison.Ordinal) ||
               lower.Contains("my routine", StringComparison.Ordinal) ||
               lower.Contains("my habit", StringComparison.Ordinal) ||
               lower.Contains("i told you", StringComparison.Ordinal) ||
               lower.Contains("remember that", StringComparison.Ordinal) ||
               lower.Contains("based on what you know about me", StringComparison.Ordinal) ||
               lower.Contains("given what you know about me", StringComparison.Ordinal);
    }

    /// <summary>
    /// Imperative requests (write/create/explain/tell/teach/etc.) that don't reference
    /// the user's stored personal data. These are generic tasks the model can handle
    /// without memory retrieval.
    /// </summary>
    private static bool LooksLikeGenericImperativeRequest(string lower)
    {
        var startsWithImperative =
            lower.StartsWith("write ", StringComparison.Ordinal) ||
            lower.StartsWith("create ", StringComparison.Ordinal) ||
            lower.StartsWith("generate ", StringComparison.Ordinal) ||
            lower.StartsWith("compose ", StringComparison.Ordinal) ||
            lower.StartsWith("draft ", StringComparison.Ordinal) ||
            lower.StartsWith("teach ", StringComparison.Ordinal) ||
            lower.StartsWith("show ", StringComparison.Ordinal) ||
            lower.StartsWith("explain ", StringComparison.Ordinal) ||
            lower.StartsWith("describe ", StringComparison.Ordinal) ||
            lower.StartsWith("tell ", StringComparison.Ordinal) ||
            lower.StartsWith("list ", StringComparison.Ordinal) ||
            lower.StartsWith("summarize ", StringComparison.Ordinal) ||
            lower.StartsWith("help ", StringComparison.Ordinal) ||
            lower.StartsWith("compare ", StringComparison.Ordinal) ||
            lower.StartsWith("give ", StringComparison.Ordinal) ||
            lower.StartsWith("suggest ", StringComparison.Ordinal) ||
            lower.StartsWith("find ", StringComparison.Ordinal);

        // HasExplicitPersonalizationSignal is already checked before this method
        return startsWithImperative;
    }

    private static bool LooksLikePublicInfoOrReasoningPrompt(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var padded = $" {lower} ";

        var startsLikeGeneralQuestion =
            lower.StartsWith("what ", StringComparison.Ordinal) ||
            lower.StartsWith("when ", StringComparison.Ordinal) ||
            lower.StartsWith("where ", StringComparison.Ordinal) ||
            lower.StartsWith("which ", StringComparison.Ordinal) ||
            lower.StartsWith("who ", StringComparison.Ordinal) ||
            lower.StartsWith("why ", StringComparison.Ordinal) ||
            lower.StartsWith("how ", StringComparison.Ordinal) ||
            lower.StartsWith("is ", StringComparison.Ordinal) ||
            lower.StartsWith("are ", StringComparison.Ordinal) ||
            lower.StartsWith("does ", StringComparison.Ordinal) ||
            lower.StartsWith("do ", StringComparison.Ordinal) ||
            lower.StartsWith("can ", StringComparison.Ordinal) ||
            lower.StartsWith("could ", StringComparison.Ordinal);

        var hasFreshnessOrPublicTopicCue =
            padded.Contains(" latest ", StringComparison.Ordinal) ||
            padded.Contains(" current ", StringComparison.Ordinal) ||
            padded.Contains(" news ", StringComparison.Ordinal) ||
            padded.Contains(" headline ", StringComparison.Ordinal) ||
            padded.Contains(" open ", StringComparison.Ordinal) ||
            padded.Contains(" hours ", StringComparison.Ordinal) ||
            padded.Contains(" close ", StringComparison.Ordinal) ||
            padded.Contains(" weather ", StringComparison.Ordinal) ||
            padded.Contains(" forecast ", StringComparison.Ordinal) ||
            padded.Contains(" probability ", StringComparison.Ordinal) ||
            padded.Contains(" riddle ", StringComparison.Ordinal) ||
            padded.Contains(" puzzle ", StringComparison.Ordinal) ||
            padded.Contains(" logic ", StringComparison.Ordinal) ||
            padded.Contains(" paradox ", StringComparison.Ordinal) ||
            padded.Contains(" syllogism ", StringComparison.Ordinal) ||
            padded.Contains(" anagram ", StringComparison.Ordinal) ||
            padded.Contains(" calculate ", StringComparison.Ordinal) ||
            padded.Contains(" explain ", StringComparison.Ordinal) ||
            padded.Contains(" compare ", StringComparison.Ordinal);

        if (!startsLikeGeneralQuestion && !hasFreshnessOrPublicTopicCue)
            return false;

        var hasSelfReference =
            padded.Contains(" my ", StringComparison.Ordinal) ||
            padded.Contains(" me ", StringComparison.Ordinal) ||
            padded.Contains(" i ", StringComparison.Ordinal) ||
            padded.Contains(" mine ", StringComparison.Ordinal) ||
            padded.Contains(" myself ", StringComparison.Ordinal);

        if (!hasSelfReference)
            return true;

        // Self-reference is present but may be generic pronoun usage
        // (e.g., "should I use", "can you help me", "how do I")
        return HasOnlyGenericPronounUsage(padded, lower);
    }

    /// <summary>
    /// Returns true when all personal pronouns in the text are in common generic
    /// constructions (e.g., "tell me", "should I", "my computer") that do not
    /// indicate the user is asking about stored personal data.
    /// </summary>
    private static bool HasOnlyGenericPronounUsage(string padded, string lower)
    {
        if (padded.Contains(" mine ", StringComparison.Ordinal) ||
            padded.Contains(" myself ", StringComparison.Ordinal))
            return false;

        if (padded.Contains(" me ", StringComparison.Ordinal))
        {
            bool genericMe =
                lower.Contains("tell me", StringComparison.Ordinal) ||
                lower.Contains("show me", StringComparison.Ordinal) ||
                lower.Contains("help me", StringComparison.Ordinal) ||
                lower.Contains("let me", StringComparison.Ordinal) ||
                lower.Contains("give me", StringComparison.Ordinal) ||
                lower.Contains("teach me", StringComparison.Ordinal) ||
                lower.Contains("write me", StringComparison.Ordinal) ||
                lower.Contains("send me", StringComparison.Ordinal) ||
                lower.Contains("explain to me", StringComparison.Ordinal) ||
                lower.Contains("walk me", StringComparison.Ordinal);
            if (!genericMe) return false;
        }

        if (padded.Contains(" my ", StringComparison.Ordinal))
        {
            bool genericMy =
                lower.Contains("my computer", StringComparison.Ordinal) ||
                lower.Contains("my code", StringComparison.Ordinal) ||
                lower.Contains("my app", StringComparison.Ordinal) ||
                lower.Contains("my application", StringComparison.Ordinal) ||
                lower.Contains("my project", StringComparison.Ordinal) ||
                lower.Contains("my server", StringComparison.Ordinal) ||
                lower.Contains("my machine", StringComparison.Ordinal) ||
                lower.Contains("my phone", StringComparison.Ordinal) ||
                lower.Contains("my laptop", StringComparison.Ordinal) ||
                lower.Contains("my pc", StringComparison.Ordinal) ||
                lower.Contains("my understanding", StringComparison.Ordinal) ||
                lower.Contains("my question", StringComparison.Ordinal) ||
                lower.Contains("my neighbor", StringComparison.Ordinal) ||
                lower.Contains("my neighbour", StringComparison.Ordinal);
            if (!genericMy) return false;
        }

        if (padded.Contains(" i ", StringComparison.Ordinal))
        {
            bool genericI =
                lower.Contains("should i ", StringComparison.Ordinal) ||
                lower.Contains("can i ", StringComparison.Ordinal) ||
                lower.Contains("do i ", StringComparison.Ordinal) ||
                lower.Contains("how do i", StringComparison.Ordinal) ||
                lower.Contains("how can i", StringComparison.Ordinal) ||
                lower.Contains("how should i", StringComparison.Ordinal) ||
                lower.Contains("what should i", StringComparison.Ordinal) ||
                lower.Contains("when should i", StringComparison.Ordinal) ||
                lower.Contains("would i ", StringComparison.Ordinal) ||
                lower.Contains("could i ", StringComparison.Ordinal) ||
                lower.Contains("if i ", StringComparison.Ordinal);
            if (!genericI) return false;
        }

        return true;
    }

    private static bool LooksLikeHypotheticalLogicScenario(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasMontyHallFragment =
            (lower.Contains("game show", StringComparison.Ordinal) &&
             (lower.Contains("door", StringComparison.Ordinal) ||
              lower.Contains("goat", StringComparison.Ordinal) ||
              lower.Contains("car", StringComparison.Ordinal))) ||
            (lower.Contains("host opens", StringComparison.Ordinal) &&
             lower.Contains("door", StringComparison.Ordinal)) ||
            (lower.Contains("switch", StringComparison.Ordinal) &&
             lower.Contains("stick", StringComparison.Ordinal) &&
             lower.Contains("door", StringComparison.Ordinal));

        if (hasMontyHallFragment)
            return true;

        var hasReasoningVocabulary =
            lower.Contains("puzzle", StringComparison.Ordinal) ||
            lower.Contains("riddle", StringComparison.Ordinal) ||
            lower.Contains("paradox", StringComparison.Ordinal) ||
            lower.Contains("syllogism", StringComparison.Ordinal) ||
            lower.Contains("anagram", StringComparison.Ordinal) ||
            lower.Contains("logic", StringComparison.Ordinal) ||
            lower.Contains("probability", StringComparison.Ordinal);

        var hasHypotheticalSetup =
            lower.Contains("imagine ", StringComparison.Ordinal) ||
            lower.Contains("suppose ", StringComparison.Ordinal) ||
            lower.Contains("let's say", StringComparison.Ordinal) ||
            lower.Contains("lets say", StringComparison.Ordinal) ||
            lower.Contains("if i ", StringComparison.Ordinal) ||
            lower.Contains("i'm on ", StringComparison.Ordinal) ||
            lower.Contains("im on ", StringComparison.Ordinal);

        return hasReasoningVocabulary && hasHypotheticalSetup;
    }

    private static bool LooksLikePersonalizedDecisionRequest(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasDecisionCue =
            lower.Contains("should i", StringComparison.Ordinal) ||
            lower.Contains("do i ", StringComparison.Ordinal) ||
            lower.Contains("help me choose", StringComparison.Ordinal) ||
            lower.Contains("better for me", StringComparison.Ordinal);

        if (!hasDecisionCue)
            return false;

        var hasPersonalContextCue =
            lower.Contains("my house", StringComparison.Ordinal) ||
            lower.Contains("my home", StringComparison.Ordinal) ||
            lower.Contains("my apartment", StringComparison.Ordinal) ||
            lower.Contains("my place", StringComparison.Ordinal) ||
            lower.Contains("my car", StringComparison.Ordinal) ||
            lower.Contains("my truck", StringComparison.Ordinal) ||
            lower.Contains("my bike", StringComparison.Ordinal) ||
            lower.Contains("my commute", StringComparison.Ordinal) ||
            lower.Contains("for me", StringComparison.Ordinal);

        if (!hasPersonalContextCue)
            return false;

        return lower.Contains("walk", StringComparison.Ordinal) ||
               lower.Contains("drive", StringComparison.Ordinal) ||
               lower.Contains("bike", StringComparison.Ordinal) ||
               lower.Contains("commute", StringComparison.Ordinal) ||
               lower.Contains("take the bus", StringComparison.Ordinal) ||
               lower.Contains("go there", StringComparison.Ordinal);
    }

    private static bool LooksLikeSelfContainedNameRecallPrompt(string lower)
    {
        return lower.Contains("my name is", StringComparison.Ordinal) &&
               (lower.Contains("what is my name", StringComparison.Ordinal) ||
                lower.Contains("what's my name", StringComparison.Ordinal) ||
                lower.Contains("tell me what my name is", StringComparison.Ordinal));
    }

    private static bool LooksLikeGenericPuzzleOrAnagramPrompt(string lower)
    {
        return lower.Contains("anagram", StringComparison.Ordinal) ||
               lower.Contains("rearrange the letters", StringComparison.Ordinal) ||
               lower.Contains("letters of", StringComparison.Ordinal) ||
               lower.Contains("2+2", StringComparison.Ordinal);
    }

    private static bool LooksLikeGenericMultiQuestionPrompt(string lower)
    {
        var numbered =
            lower.Contains("1)", StringComparison.Ordinal) &&
            lower.Contains("2)", StringComparison.Ordinal);

        if (!numbered)
            return false;

        // If this looks explicitly personal, allow normal decision flow.
        if (HasExplicitPersonalizationSignal(lower))
            return false;

        return true;
    }

    private static bool LooksLikeShortGenericFollowUp(string lower)
    {
        var trimmed = lower.Trim();
        if (trimmed.Length > 12)
            return false;

        return trimmed is "why" or "why?" or
               "how" or "how?" or
               "what" or "what?" or
               "ok" or "okay";
    }

    private static bool LooksLikeLocalBusinessDiscoveryRequest(string lower)
    {
        if (string.IsNullOrWhiteSpace(lower))
            return false;

        var hasFindCue =
            lower.Contains("find me", StringComparison.Ordinal) ||
            lower.Contains("good ", StringComparison.Ordinal) ||
            lower.Contains("recommend", StringComparison.Ordinal);
        if (!hasFindCue)
            return false;

        var hasBusinessType =
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("florist", StringComparison.Ordinal) ||
            lower.Contains("coffee", StringComparison.Ordinal) ||
            lower.Contains("restaurant", StringComparison.Ordinal) ||
            lower.Contains("pizza", StringComparison.Ordinal) ||
            lower.Contains("bakery", StringComparison.Ordinal);
        if (!hasBusinessType)
            return false;

        return lower.Contains(" in ", StringComparison.Ordinal) ||
               lower.Contains(" near ", StringComparison.Ordinal);
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
