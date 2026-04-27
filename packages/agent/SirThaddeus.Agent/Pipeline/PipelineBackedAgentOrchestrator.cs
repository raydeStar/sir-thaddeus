using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline;

/// <summary>
/// <see cref="IHeadlessAgent"/> implementation backed by a
/// <see cref="ChatPipeline"/>. Holds conversation state (history, system
/// prompt) externally; delegates the actual LLM + tool work to the
/// pipeline. This is the CLI-side sibling of
/// <c>Thaddeus.Runtime.Chat.LmStudioAssistant</c> — same pipeline, same
/// steps, different session-state owner.
///
/// <para>Single-conversation at a time. If the caller passes a
/// <c>conversationId</c>, it flows through to the
/// <see cref="TurnContext.ThreadId"/> but does not partition history — a
/// process holds one active conversation. Workflow coordinators swap
/// conversations via <see cref="ResetConversation"/> +
/// <see cref="SeedHistory"/> between iterations.</para>
///
/// <para>Thread-safe: the two <c>ProcessAsync</c> overloads,
/// <see cref="ResetConversation"/>, and <see cref="SeedHistory"/>
/// serialize through a single semaphore so concurrent callers get a
/// consistent view of history. The pipeline itself can be long-lived and
/// shared across orchestrator instances.</para>
/// </summary>
public sealed class PipelineBackedAgentOrchestrator : IHeadlessAgent, IDisposable
{
    private readonly ChatPipeline _pipeline;
    private readonly IMcpToolClient _mcp;
    private readonly string _systemPrompt;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly List<ChatMessage> _history = new();

    public PipelineBackedAgentOrchestrator(
        ChatPipeline pipeline,
        IMcpToolClient mcp,
        string systemPrompt)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _systemPrompt = systemPrompt ?? throw new ArgumentNullException(nameof(systemPrompt));
    }

    public Task<AgentResponse> ProcessAsync(string userMessage, CancellationToken cancellationToken = default)
        => ProcessAsync(userMessage, conversationId: null, cancellationToken);

    public async Task<AgentResponse> ProcessAsync(
        string userMessage,
        string? conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(userMessage);

        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var toolDefs = await BuildToolDefsAsync(cancellationToken).ConfigureAwait(false);

            var llmMessages = new List<ChatMessage>(_history.Count + 2)
            {
                ChatMessage.System(_systemPrompt),
            };
            llmMessages.AddRange(_history);
            llmMessages.Add(ChatMessage.User(userMessage));

            var context = new TurnContext
            {
                ThreadId = conversationId ?? "default",
                MessageId = BuildMessageId(),
                UserText = userMessage,
                LlmMessages = llmMessages,
                ToolDefs = toolDefs,
            };

            var response = await _pipeline.RunAsync(context, cancellationToken).ConfigureAwait(false);

            // Absorb the turn into history so the next ProcessAsync sees
            // the full conversation. Only the user + assistant text is
            // kept — intermediate tool calls stay in ToolCallsMade on the
            // response (for UI / audit) but do not balloon the prompt.
            _history.Add(ChatMessage.User(userMessage));
            if (!string.IsNullOrEmpty(response.Text))
                _history.Add(ChatMessage.Assistant(response.Text));

            return response;
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void ResetConversation()
    {
        _turnGate.Wait();
        try
        {
            _history.Clear();
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public void SeedHistory(IEnumerable<(string Role, string Content)> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        _turnGate.Wait();
        try
        {
            _history.Clear();
            foreach (var (role, content) in messages)
            {
                if (string.IsNullOrEmpty(content)) continue;
                var msg = NormalizeRole(role) switch
                {
                    "user" => ChatMessage.User(content),
                    "assistant" => ChatMessage.Assistant(content),
                    "system" => ChatMessage.System(content),
                    _ => null,
                };
                if (msg is not null)
                    _history.Add(msg);
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    public async Task<int> GetAvailableToolCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tools = await _mcp.ListToolsAsync(cancellationToken).ConfigureAwait(false);
            return tools.Count;
        }
        catch
        {
            // Diagnostic endpoint — never throw. Zero tools signals the
            // MCP layer is unreachable without hiding the call's failure
            // (caller can distinguish "0 tools available" from "threw"
            // via its own wrapping if needed).
            return 0;
        }
    }

    public void Dispose() => _turnGate.Dispose();

    /// <summary>Snapshot of current conversation history. Test-facing only.</summary>
    internal IReadOnlyList<ChatMessage> HistorySnapshot()
    {
        _turnGate.Wait();
        try
        {
            return _history.ToArray();
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private static string BuildMessageId()
        => "msg_" + Guid.NewGuid().ToString("N")[..12];

    private static string NormalizeRole(string role)
        => (role ?? string.Empty).Trim().ToLowerInvariant();

    private async Task<IReadOnlyList<ToolDefinition>> BuildToolDefsAsync(CancellationToken ct)
    {
        IReadOnlyList<McpToolInfo> mcpTools;
        try
        {
            mcpTools = await _mcp.ListToolsAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return Array.Empty<ToolDefinition>();
        }

        var defs = new List<ToolDefinition>(mcpTools.Count);
        foreach (var t in mcpTools)
        {
            defs.Add(new ToolDefinition
            {
                Function = new FunctionDefinition
                {
                    Name = t.Name,
                    Description = t.Description,
                    Parameters = t.InputSchema,
                },
            });
        }
        return defs;
    }
}
