using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Context;

/// <summary>
/// Contract for the Dynamic Context Decoupler's gatekeeper.
/// Returns a strict boolean: does the current query linguistically
/// require the immediate chat history to be understood?
/// </summary>
public interface IGatekeeperService
{
    /// <summary>
    /// Evaluates whether <paramref name="currentQuery"/> depends on
    /// prior conversational context to be fully understood.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the query is context-dependent (e.g. "tell me more",
    /// "what about the second one?"); <c>false</c> if it stands alone
    /// (e.g. "what is the speed of light?", "solve this puzzle").
    /// </returns>
    Task<bool> IsContextDependentAsync(
        string currentQuery,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A lightning-fast gatekeeper that asks a tiny local LLM (~1B params)
/// whether the current user query depends on prior chat history.
///
/// The Dynamic Context Decoupler — because even a butler knows when
/// yesterday's conversation is irrelevant to today's breakfast order.
///
/// Design principles:
///   - Maximum determinism: temperature 0.0, max 5 tokens.
///   - Fail-open: if the gatekeeper is unreachable or times out,
///     we assume context IS needed (safe default — never lose context).
///   - No side effects: pure read-only linguistic analysis.
/// </summary>
public sealed class FastLlmGatekeeperService : IGatekeeperService
{
    private readonly ILlmClient _gatekeeperLlm;

    /// <summary>
    /// Hard ceiling on gatekeeper response tokens.
    /// 5 tokens gives the model enough room for "TRUE" or "FALSE"
    /// plus any minor throat-clearing whitespace.
    /// </summary>
    private const int MaxGatekeeperTokens = 5;

    /// <summary>
    /// The system prompt that transforms a small LLM into a
    /// binary linguistic dependency classifier.
    /// </summary>
    private const string GatekeeperSystemPrompt =
        "You are a strict binary classifier for linguistic dependency analysis.\n" +
        "Your ONLY job is to determine if the user's CURRENT query requires " +
        "the IMMEDIATELY PRECEDING conversation history to be understood.\n\n" +
        "Rules:\n" +
        "- Output ONLY the single word TRUE or FALSE. Nothing else.\n" +
        "- TRUE means the query uses pronouns, references, or follow-ups " +
        "that are meaningless without prior context " +
        "(e.g., \"tell me more\", \"what about the second one?\", \"and the price?\").\n" +
        "- FALSE means the query is a complete, self-contained question or command " +
        "(e.g., \"what is the speed of light?\", \"write a poem about cats\", " +
        "\"solve 2+2\").\n" +
        "- When in doubt, output TRUE.\n" +
        "- NEVER explain. NEVER add punctuation. NEVER output anything other " +
        "than TRUE or FALSE.";

    public FastLlmGatekeeperService(ILlmClient gatekeeperLlm)
    {
        _gatekeeperLlm = gatekeeperLlm ?? throw new ArgumentNullException(nameof(gatekeeperLlm));
    }

    /// <inheritdoc />
    public async Task<bool> IsContextDependentAsync(
        string currentQuery,
        CancellationToken cancellationToken = default)
    {
        // Fail-open: if anything goes wrong, assume context is needed.
        // Better to include unnecessary history than to strip necessary context.
        try
        {
            var messages = new List<ChatMessage>
            {
                ChatMessage.System(GatekeeperSystemPrompt),
                ChatMessage.User(currentQuery)
            };

            var response = await _gatekeeperLlm.ChatAsync(
                messages,
                tools: null,
                maxTokensOverride: MaxGatekeeperTokens,
                cancellationToken: cancellationToken);

            var answer = (response.Content ?? "").Trim().ToUpperInvariant();

            // Strip any punctuation or whitespace the model might have added
            // despite our stern instructions. Small models gonna small-model.
            if (answer.StartsWith("FALSE"))
                return false;

            // Anything that isn't a clear "FALSE" is treated as context-dependent.
            // This is the fail-open principle: when in doubt, keep the history.
            return true;
        }
        catch (OperationCanceledException)
        {
            throw; // Respect cancellation — don't swallow it.
        }
        catch
        {
            // Gatekeeper is down or returned garbage.
            // Fail-open: assume context is needed.
            return true;
        }
    }
}
