using System.Diagnostics;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that runs the LLM↔tool loop: it calls the model, executes
/// whatever tools the model asked for (through the permission gate and
/// any interceptors), appends the results to the history, and repeats
/// until the model produces a final answer or the round-trip cap is hit.
/// Emits per-tool events via <see cref="IChatEventSink"/> so the UI can
/// render the "thinking" cadence.
///
/// <para>This is the core behavior of the desktop assistant, lifted out of
/// <c>LmStudioAssistant.RunToolLoopAsync</c>. It keeps runtime-specific
/// concerns (propose_automation interception, automation args rewriting)
/// on the outside via the <see cref="IToolCallInterceptor"/> and
/// <see cref="IToolArgsRewriter"/> seams — the runtime wires those in
/// when it composes the pipeline.</para>
///
/// <para><b>Return contract</b>:</para>
/// <list type="bullet">
///   <item>Happy path (model produced a final reply) — returns
///         <see cref="StepResult.Continue"/> with
///         <see cref="TurnContext.AssistantDraft"/> populated so a
///         downstream post-process + composer step can finalize the turn.</item>
///   <item>Round-trip cap hit — returns <see cref="StepResult.Terminate"/>
///         with a deterministic "we gave up" response. Skips
///         post-processing; the cap message is already final.</item>
///   <item>Cancellation — bubbles <see cref="OperationCanceledException"/>
///         so the facade can emit a cancelled <c>turn.complete</c>.</item>
/// </list>
/// </summary>
public sealed class ToolLoopStep : ITurnStep
{
    private readonly ILlmClient _llm;
    private readonly IMcpToolClient _mcp;
    private readonly IChatEventSink _sink;
    private readonly IToolPermissionGate? _permissionGate;
    private readonly IToolGroupClassifier _groupClassifier;
    private readonly IReadOnlyList<IToolCallInterceptor> _interceptors;
    private readonly IReadOnlyList<IToolArgsRewriter> _argsRewriters;
    private readonly int _maxRoundTrips;

    public ToolLoopStep(
        ILlmClient llm,
        IMcpToolClient mcp,
        IChatEventSink? sink = null,
        IToolPermissionGate? permissionGate = null,
        IToolGroupClassifier? groupClassifier = null,
        IEnumerable<IToolCallInterceptor>? interceptors = null,
        IEnumerable<IToolArgsRewriter>? argsRewriters = null,
        int maxRoundTrips = 6)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _mcp = mcp ?? throw new ArgumentNullException(nameof(mcp));
        _sink = sink ?? NullChatEventSink.Instance;
        _permissionGate = permissionGate;
        _groupClassifier = groupClassifier ?? DefaultToolGroupClassifier.Instance;
        _interceptors = interceptors?.ToArray() ?? Array.Empty<IToolCallInterceptor>();
        _argsRewriters = argsRewriters?.ToArray() ?? Array.Empty<IToolArgsRewriter>();
        if (maxRoundTrips < 1)
            throw new ArgumentOutOfRangeException(nameof(maxRoundTrips), "Must be >= 1.");
        _maxRoundTrips = maxRoundTrips;
    }

    public string Name => "ToolLoop";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        // Working lists — we append to history and tool-calls-made across
        // rounds, so a local mutable copy is cheaper than .With() per call.
        var messages = context.LlmMessages.ToList();
        var toolCallsMade = context.ToolCallsMade.ToList();

        // Spin-detection state: counts failed (tool, normalized-args) signatures
        // across rounds. Two consecutive identical failures nudges the model
        // to stop retrying.
        var callSignatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var lastCallOk = true;

        for (var round = 0; round < _maxRoundTrips; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await _llm
                .ChatAsync(messages, context.ToolDefs.Count > 0 ? context.ToolDefs : null, cancellationToken)
                .ConfigureAwait(false);

            if (response.ToolCalls is null || response.ToolCalls.Count == 0)
            {
                // Happy path — hand the draft off to the next step
                // (typically PostProcess + ResponseComposer). The context
                // now carries the assembled history, every tool call we
                // made, and the raw model text.
                var updated = context with
                {
                    LlmMessages = messages,
                    ToolCallsMade = toolCallsMade,
                    AssistantDraft = response.Content ?? string.Empty,
                };
                return new StepResult.Continue(updated);
            }

            // Spin nudge: only after the 2nd consecutive failed repeat so
            // one legitimate retry (transient 503) still gets to run.
            if (!lastCallOk && response.ToolCalls.Count > 0)
            {
                var firstSig = BuildCallSignature(response.ToolCalls[0]);
                if (callSignatureCounts.TryGetValue(firstSig, out var prior) && prior >= 2)
                {
                    messages.Add(ChatMessage.System(
                        "The previous tool call returned an error and retrying the same call will not help. " +
                        "Stop calling tools. Produce a short final reply that reports what succeeded and " +
                        "what failed in one sentence, then stop."));
                    continue;
                }
            }

            messages.Add(ChatMessage.AssistantToolCalls(response.ToolCalls));

            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toolName = call.Function.Name;
                var rawArgs = call.Function.Arguments ?? "{}";
                var args = ApplyArgsRewriters(context, toolName, rawArgs);

                var group = _groupClassifier.Classify(toolName);
                var activityId = Guid.NewGuid().ToString("N");

                await _sink.ToolStartedAsync(
                        activityId, context.ThreadId, context.MessageId,
                        toolName, group, Trim(args, 512), cancellationToken)
                    .ConfigureAwait(false);

                var sw = Stopwatch.StartNew();
                var outcome = await ExecuteSingleCallAsync(
                        context, toolName, args, activityId, cancellationToken)
                    .ConfigureAwait(false);
                sw.Stop();

                await _sink.ToolCompletedAsync(
                        activityId, context.ThreadId, context.MessageId, toolName,
                        outcome.Ok, sw.ElapsedMilliseconds,
                        outcome.Ok ? Trim(outcome.ResultText, 280) : null,
                        outcome.Error,
                        cancellationToken)
                    .ConfigureAwait(false);

                messages.Add(ChatMessage.ToolResult(call.Id, outcome.ResultText));
                toolCallsMade.Add(new ToolCallRecord
                {
                    ToolName = toolName,
                    Arguments = args,
                    Result = outcome.ResultText,
                    Success = outcome.Ok,
                });

                lastCallOk = outcome.Ok;
                if (!outcome.Ok)
                {
                    var sig = BuildCallSignature(call);
                    callSignatureCounts[sig] = callSignatureCounts.GetValueOrDefault(sig) + 1;
                }
            }
        }

        // Cap exhausted — deterministic message so the UI doesn't look
        // like it silently gave up. We terminate here (skipping post-process)
        // because the cap message is already final and not model-generated.
        var capResponse = new AgentResponse
        {
            Text = "(Tool-call loop hit its round-trip cap without a final answer. Try rephrasing or simplifying the request.)",
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = _maxRoundTrips,
        };
        return new StepResult.Terminate(capResponse);
    }

    private string ApplyArgsRewriters(TurnContext context, string toolName, string args)
    {
        var current = args;
        foreach (var rewriter in _argsRewriters)
            current = rewriter.Rewrite(context, toolName, current) ?? current;
        return current;
    }

    private async Task<ToolCallOutcome> ExecuteSingleCallAsync(
        TurnContext context,
        string toolName,
        string args,
        string activityId,
        CancellationToken ct)
    {
        // Permission gate first — denial skips both interceptors and MCP.
        if (_permissionGate is not null)
        {
            var check = await _permissionGate
                .CheckAsync(toolName, args, ct)
                .ConfigureAwait(false);
            if (!check.Granted)
            {
                return new ToolCallOutcome(
                    ResultText: $"(Permission denied for '{toolName}': {check.DenialReason ?? "policy"})",
                    Ok: false,
                    Error: check.DenialReason ?? "Permission denied.");
            }
        }

        // Interceptors next — any one of them may own the tool name (e.g.
        // the runtime's propose_automation handler).
        foreach (var interceptor in _interceptors)
        {
            var claimed = await interceptor
                .TryInterceptAsync(context, toolName, args, activityId, ct)
                .ConfigureAwait(false);
            if (claimed is not null) return claimed;
        }

        // Fall through to the real MCP server.
        try
        {
            var result = await _mcp.CallToolAsync(toolName, args, ct).ConfigureAwait(false);
            return new ToolCallOutcome(result, Ok: true, Error: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ToolCallOutcome($"Error: {ex.Message}", Ok: false, Error: ex.Message);
        }
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>Signature for spin detection: tool name + normalized args JSON
    /// so whitespace / property order differences don't hide identical calls.</summary>
    private static string BuildCallSignature(ToolCallRequest call)
    {
        var name = call.Function.Name ?? string.Empty;
        var rawArgs = call.Function.Arguments ?? "{}";
        string normalized;
        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            normalized = JsonSerializer.Serialize(doc.RootElement);
        }
        catch
        {
            normalized = rawArgs;
        }
        return name + "|" + normalized;
    }
}
