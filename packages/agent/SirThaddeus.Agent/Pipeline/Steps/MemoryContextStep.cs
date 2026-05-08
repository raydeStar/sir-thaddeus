using SirThaddeus.Agent.Memory;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that prefetches relevant memory for the current turn and
/// injects it as a block appended to the system message. Delegates the
/// actual retrieval to an <see cref="IMemoryContextProvider"/> so the
/// memory source (MCP tools, SQLite, in-memory index, …) stays
/// swappable.
///
/// <para><b>Fail-open semantics</b>: any failure from the provider (error,
/// timeout, empty result) leaves the context's LLM messages untouched. The
/// turn never fails because memory was unavailable — the model just
/// answers without remembered context.</para>
///
/// <para><b>Timeout budget</b>: the provider's own <see cref="MemoryContextRequest.Timeout"/>
/// governs how long retrieval may take. The step forwards the pipeline's
/// cancellation token so a user-initiated stop still bubbles through.</para>
///
/// <para>Place this step <b>after</b> logic / intent scaffolds so the
/// memory block ends up adjacent to the base system prompt, and
/// <b>before</b> the tool loop so the primary model sees the context on
/// its very first call.</para>
/// </summary>
public sealed class MemoryContextStep : ITurnStep
{
    private readonly IMemoryContextProvider? _provider;
    private readonly Func<TurnContext, MemoryContextRequest> _requestBuilder;

    /// <param name="provider">Memory retrieval abstraction. Null = step is
    /// a no-op (runtimes without a wired-up memory stack simply pass
    /// through).</param>
    /// <param name="requestBuilder">Optional hook for callers that want to
    /// customize the request (e.g. set <see cref="MemoryContextRequest.ActiveProfileId"/>
    /// from session state, toggle <see cref="MemoryContextRequest.MemoryEnabled"/>
    /// from settings). Defaults to a safe request built from the turn
    /// context alone.</param>
    public MemoryContextStep(
        IMemoryContextProvider? provider,
        Func<TurnContext, MemoryContextRequest>? requestBuilder = null)
    {
        _provider = provider;
        _requestBuilder = requestBuilder ?? DefaultRequestBuilder;
    }

    public string Name => "MemoryContext";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_provider is null)
            return new StepResult.Continue(context);

        var request = _requestBuilder(context);
        MemoryContextResult result;
        try
        {
            result = await _provider.GetContextAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fail-open — memory problems must not derail the turn. The
            // provider is expected to surface transport failures via
            // Provenance.Success=false rather than throwing, but defend
            // against lazy implementations.
            return new StepResult.Continue(context);
        }

        // Propagate the onboarding signal regardless of pack contents
        // so OnboardingInjectionStep can fire on empty-memory turns.
        var withOnboarding = context with
        {
            IsNewUser = result.OnboardingNeeded,
            ToolCallsMade = AppendMemoryToolCall(context.ToolCallsMade, result, request)
        };

        if (string.IsNullOrWhiteSpace(result.PackText))
            return new StepResult.Continue(withOnboarding);

        var updatedMessages = AppendMemoryPackToSystemMessage(withOnboarding.LlmMessages, result.PackText);
        return new StepResult.Continue(withOnboarding with { LlmMessages = updatedMessages });
    }

    private static MemoryContextRequest DefaultRequestBuilder(TurnContext context) => new()
    {
        UserMessage = context.UserText ?? string.Empty,
        ConversationId = context.ThreadId,
        MemoryEnabled = true,
    };

    private static IReadOnlyList<ToolCallRecord> AppendMemoryToolCall(
        IReadOnlyList<ToolCallRecord> existing,
        MemoryContextResult result,
        MemoryContextRequest request)
    {
        var toolName = result.Provenance.SourceTool;
        if (string.IsNullOrWhiteSpace(toolName))
            return existing;

        if (!string.Equals(toolName, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        var calls = existing.ToList();
        calls.Add(new ToolCallRecord
        {
            ToolName = toolName,
            Arguments = System.Text.Json.JsonSerializer.Serialize(new
            {
                query = request.UserMessage,
                conversationId = request.ConversationId,
                activeProfileId = request.ActiveProfileId
            }),
            Result = result.RawResult ?? result.PackText,
            Success = result.Provenance.Success,
        });
        return calls;
    }

    private static IReadOnlyList<ChatMessage> AppendMemoryPackToSystemMessage(
        IReadOnlyList<ChatMessage> messages,
        string packText)
    {
        // Format the pack with a clear heading so small models recognise
        // it as retrieved memory rather than baseline instructions.
        var block = "\n\n[REMEMBERED CONTEXT]\n" + packText.Trim() + "\n[/REMEMBERED CONTEXT]";

        for (var i = 0; i < messages.Count; i++)
        {
            if (!string.Equals(messages[i].Role, "system", StringComparison.OrdinalIgnoreCase))
                continue;

            var combined = (messages[i].Content ?? string.Empty) + block;
            var next = messages.ToArray();
            next[i] = ChatMessage.System(combined);
            return next;
        }

        var inserted = new List<ChatMessage>(messages.Count + 1) { ChatMessage.System(block.TrimStart()) };
        inserted.AddRange(messages);
        return inserted;
    }
}
