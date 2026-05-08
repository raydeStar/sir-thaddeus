using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that runs the reasoning-guardrails path for questions
/// that benefit from a deliberate, first-principles breakdown before the
/// main tool loop answers. Delegates detection + synthesis to
/// <see cref="ReasoningGuardrailsPipeline"/> (which internally runs a
/// goal-inferencer, entity extractor, and constraint builder); when that
/// returns a result, the step terminates the turn with the guardrails
/// answer + rationale.
///
/// <para>The guardrails pipeline only fires for questions that match its
/// detector heuristics (plus an always-on mode for users who want every
/// turn walked through the first-principles scaffold). For everyday
/// queries the detector returns null and this step is a no-op.</para>
///
/// <para>Place this step <b>after</b> <see cref="FootmanRouterStep"/> so
/// routing + gate happen first, and <b>before</b> <see cref="ToolLoopStep"/>
/// so a guardrails answer bypasses the LLM tool loop entirely.</para>
///
/// <para>No-op when <c>pipeline</c> is null — runtimes that don't want
/// guardrails (older UI builds, smoke tests) compose without it.</para>
/// </summary>
public sealed class GuardrailsStep : ITurnStep
{
    private readonly ReasoningGuardrailsPipeline? _pipeline;
    private readonly string _mode;

    /// <param name="pipeline">The guardrails pipeline. Null = step no-op.</param>
    /// <param name="mode">Guardrails mode from settings
    /// (<c>off</c>/<c>auto</c>/<c>always</c>). Defaults to <c>"auto"</c>.</param>
    public GuardrailsStep(ReasoningGuardrailsPipeline? pipeline, string mode = "auto")
    {
        _pipeline = pipeline;
        _mode = mode ?? "auto";
    }

    public string Name => "Guardrails";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (_pipeline is null)
            return new StepResult.Continue(context);

        if (HasPersonalContextCue(context.UserText) &&
            !HasMemoryRetrieveCall(context) &&
            !HasRememberedContext(context))
        {
            return new StepResult.Continue(context);
        }

        if (ShouldDeferToMemoryRetrieve(context))
            return new StepResult.Continue(context);

        GuardrailsPipelineResult? result;
        try
        {
            result = await _pipeline
                .TryRunAsync(context.UserText ?? string.Empty, _mode, extraContext: null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Guardrails is opportunistic — a failure inside the pipeline
            // must not derail the turn. Fall through to the tool loop.
            return new StepResult.Continue(context);
        }

        if (result is null || string.IsNullOrWhiteSpace(result.AnswerText))
            return new StepResult.Continue(context);

        var answerText = OrchestratorMessageHelpers.TryBuildEarlyDeterministicBenignFallback(context.UserText)
            ?? result.AnswerText;

        var response = new AgentResponse
        {
            Text = answerText,
            Success = true,
            ToolCallsMade = context.ToolCallsMade.ToList(),
            LlmRoundTrips = result.LlmRoundTrips,
            GuardrailsUsed = true,
            GuardrailsRationale = result.RationaleLines,
        };
        return new StepResult.Terminate(response);
    }

    private static bool ShouldDeferToMemoryRetrieve(TurnContext context)
    {
        if (HasMemoryRetrieveCall(context) || HasRememberedContext(context))
            return false;

        var userText = context.UserText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var toolCount = 0;
        var hasMemoryRetrieve = false;
        foreach (var def in context.ToolDefs)
        {
            var name = def.Function?.Name;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            toolCount++;
            if (string.Equals(name, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase))
            {
                hasMemoryRetrieve = true;
            }
        }

        var harnessMemoryOnly = HarnessAllowsOnlyMemoryRetrieve();
        if (!hasMemoryRetrieve && !harnessMemoryOnly)
            return false;

        return HasPersonalContextCue(userText);
    }

    private static bool HasMemoryRetrieveCall(TurnContext context)
        => context.ToolCallsMade.Any(call =>
            string.Equals(call.ToolName, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(call.ToolName, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));

    private static bool HasRememberedContext(TurnContext context)
        => context.LlmMessages.Any(message =>
            string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase) &&
            (message.Content?.Contains("[REMEMBERED CONTEXT]", StringComparison.OrdinalIgnoreCase) ?? false));

    private static bool HasPersonalContextCue(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = " " + userText.Trim().ToLowerInvariant() + " ";
        return lower.Contains(" my ", StringComparison.Ordinal) ||
               lower.Contains(" i'm ", StringComparison.Ordinal) ||
               lower.Contains(" im ", StringComparison.Ordinal) ||
               lower.Contains(" i've ", StringComparison.Ordinal) ||
               lower.Contains(" ive ", StringComparison.Ordinal) ||
               lower.Contains(" we ", StringComparison.Ordinal) ||
               lower.Contains(" our ", StringComparison.Ordinal);
    }

    private static bool HarnessAllowsOnlyMemoryRetrieve()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var tools = raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(tool => !string.IsNullOrWhiteSpace(tool))
            .ToList();
        return tools.Count == 1 &&
               (string.Equals(tools[0], ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(tools[0], ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));
    }
}
