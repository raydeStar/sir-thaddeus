using System.Diagnostics;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Pipeline step that runs the footman (gatekeeper) classifier, narrows
/// <see cref="TurnContext.ToolDefs"/> to the tools appropriate for the
/// classified intent, and emits a <c>chat.footman.decision</c> event so
/// UIs can render a gatekeeper chip.
///
/// <para><b>Fail-open semantics</b> — any exception from the footman
/// leaves the full tool list in place and emits a <c>footman_error</c>
/// decision event. Turns never fail because the gatekeeper couldn't be
/// reached.</para>
///
/// <para><b>Skip conditions</b> — the step is a no-op when:</para>
/// <list type="bullet">
///   <item>No footman is configured (<c>footman</c> is null).</item>
///   <item>This is an automation run (<see cref="TurnContext.IsAutomationRun"/>) —
///         the allowlist is already pinned, the gatekeeper would just add latency.</item>
///   <item>No tools are available (<c>ToolDefs.Count == 0</c>) — nothing to filter.</item>
///   <item>Features haven't been extracted — the pipeline was misconfigured;
///         better to fall through than guess.</item>
/// </list>
///
/// <para>Always-allow tool names (e.g. the runtime's virtual
/// <c>propose_automation</c>) bypass filtering and are passed in via the
/// constructor.</para>
/// </summary>
public sealed class FootmanRouterStep : ITurnStep
{
    private readonly IFootmanRouter? _footman;
    private readonly IChatEventSink _sink;
    private readonly IReadOnlyList<string> _alwaysAllowToolNames;

    public FootmanRouterStep(
        IFootmanRouter? footman,
        IChatEventSink sink,
        IReadOnlyList<string>? alwaysAllowToolNames = null)
    {
        _footman = footman;
        _sink = sink ?? NullChatEventSink.Instance;
        _alwaysAllowToolNames = alwaysAllowToolNames ?? Array.Empty<string>();
    }

    public string Name => "FootmanRouter";

    public async Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (ShouldSkip(context))
            return new StepResult.Continue(context);

        var sw = Stopwatch.StartNew();
        RoutingDecision decision;
        try
        {
            decision = await _footman!.RouteAsync(context.UserText ?? string.Empty, context.Features!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // genuine cancellation bubbles up
        }
        catch
        {
            sw.Stop();
            // Best-effort chip — the UI wants to know the gatekeeper
            // tried and failed, not that it was silently bypassed.
            await _sink.FootmanDecisionAsync(
                    context.ThreadId, context.MessageId,
                    nextState: "Fallback",
                    confidence: 0.0,
                    abstain: true,
                    reasonCode: "footman_error",
                    toolsKept: context.ToolDefs.Count,
                    toolsTotal: context.ToolDefs.Count,
                    elapsedMs: sw.ElapsedMilliseconds,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            return new StepResult.Continue(context);
        }
        sw.Stop();

        var filtered = FootmanToolFilter.Filter(
            context.ToolDefs,
            decision,
            alwaysAllowToolNames: _alwaysAllowToolNames);

        await _sink.FootmanDecisionAsync(
                context.ThreadId, context.MessageId,
                nextState: decision.NextState.ToString(),
                confidence: decision.Confidence,
                abstain: decision.Abstain,
                reasonCode: decision.ReasonCode,
                toolsKept: filtered.Count,
                toolsTotal: context.ToolDefs.Count,
                elapsedMs: sw.ElapsedMilliseconds,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        // Reference-equal filtered list means FootmanToolFilter returned
        // the input unchanged (fail-open / non-authoritative decision);
        // no point allocating a new context.
        if (ReferenceEquals(filtered, context.ToolDefs))
            return new StepResult.Continue(context);

        return new StepResult.Continue(context with { ToolDefs = filtered });
    }

    private bool ShouldSkip(TurnContext context)
    {
        if (_footman is null) return true;
        if (context.IsAutomationRun) return true;
        if (context.ToolDefs.Count == 0) return true;
        // Harness fixtures already pin the exact tool surface. Reclassifying
        // that surface can only remove required tools and invalidate the run.
        if (HarnessHasPinnedToolAllowlist()) return true;
        if (HasWeatherGeocode(context) && HasWeatherCue(context.UserText)) return true;
        if (HasTimeNow(context) && HasTimezoneLookup(context) && HasLocationScopedTimeCue(context.UserText)) return true;
        if (HasMemoryRetrieve(context) && HasPersonalContextCue(context.UserText)) return true;
        if (context.Features is null) return true;
        return false;
    }

    private static bool HasWeatherGeocode(TurnContext context)
        => context.ToolDefs.Any(def =>
            string.Equals(def.Function?.Name, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Function?.Name, ToolNames.WeatherGeocodeAlt, StringComparison.OrdinalIgnoreCase));

    private static bool HasWeatherCue(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        return lower.Contains("weather", StringComparison.Ordinal) ||
               lower.Contains("forecast", StringComparison.Ordinal) ||
               lower.Contains("temperature", StringComparison.Ordinal) ||
               lower.Contains("outlook", StringComparison.Ordinal);
    }

    private static bool HasMemoryRetrieve(TurnContext context)
        => context.ToolDefs.Any(def =>
            string.Equals(def.Function?.Name, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Function?.Name, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));

    private static bool HasTimeNow(TurnContext context)
        => context.ToolDefs.Any(def =>
            string.Equals(def.Function?.Name, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Function?.Name, ToolNames.TimeNowAlt, StringComparison.OrdinalIgnoreCase));

    private static bool HasTimezoneLookup(TurnContext context)
        => context.ToolDefs.Any(def =>
            string.Equals(def.Function?.Name, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(def.Function?.Name, ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase));

    private static bool HasLocationScopedTimeCue(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = " " + userText.Trim().ToLowerInvariant() + " ";
        var asksForCurrentTime =
            lower.Contains(" current time ", StringComparison.Ordinal) ||
            lower.Contains(" current date and time ", StringComparison.Ordinal) ||
            lower.Contains(" time there ", StringComparison.Ordinal) ||
            lower.Contains(" time in ", StringComparison.Ordinal) ||
            lower.Contains(" time at ", StringComparison.Ordinal) ||
            lower.Contains(" time for ", StringComparison.Ordinal);
        if (!asksForCurrentTime)
            return false;

        return lower.Contains(" in ", StringComparison.Ordinal) ||
               lower.Contains(" at ", StringComparison.Ordinal) ||
               lower.Contains(" for ", StringComparison.Ordinal) ||
               lower.Contains(" there ", StringComparison.Ordinal);
    }

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

    private static bool HarnessHasPinnedToolAllowlist()
    {
        var raw = Environment.GetEnvironmentVariable("ST_HARNESS_ALLOWED_TOOLS");
        return !string.IsNullOrWhiteSpace(raw);
    }
}
