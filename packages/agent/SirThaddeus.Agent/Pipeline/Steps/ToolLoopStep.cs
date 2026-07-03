using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Utilities;
using SirThaddeus.Agent.Validation;
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
    private const int ToolLoopMaxOutputTokens = 1024;

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
        string? forcedToolForNextRound = null;

        // Class-B "script mechanics" recovery (~61% of live compute failures):
        // when a python_eval call errors or prints nothing, nudge the model to
        // rewrite a proper multi-line script and force one more python_eval
        // round. Capped so a genuinely broken problem can't spin the loop.
        var pythonRepairNudges = 0;

        // Class-C "answer selection" recovery (~24%): when a strict-compute turn
        // ends with the model's own successful computations disagreeing, force
        // exactly one reconciliation round before adopting a draft.
        var reconciliationNudges = 0;

        if (ShouldAddCalculatorSetupHint(context))
        {
            messages.Add(ChatMessage.System(
                "For calculator math, set up the expression before calling the tool. " +
                "Preserve operand order exactly from formulas. For recurrences or indexed sequences, " +
                "label the previous terms first, then call calculator for each derived term."));
        }

        for (var round = 0; round < _maxRoundTrips; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (round > 0 &&
                TryBuildPlacesDiscoverDraftFromRecords(toolCallsMade, context.UserText) is { Text.Length: > 0 } existingPlacesDraft)
            {
                var placesResponse = new AgentResponse
                {
                    Text = existingPlacesDraft.Text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = round,
                    Sources = existingPlacesDraft.Sources,
                };
                return new StepResult.Terminate(placesResponse);
            }

            // Force the user-requested tool only on the FIRST round. After
            // the tool fires and returns results, subsequent rounds must be
            // free to synthesize prose or chain follow-up tools — otherwise
            // we'd loop forever trying to call web_search over and over.
            var forcedTool = forcedToolForNextRound ?? (round == 0 ? context.ForcedTool : null);
            forcedToolForNextRound = null;
            var tools = context.ToolDefs.Count > 0 ? context.ToolDefs : null;
            if (round == 0 && string.IsNullOrWhiteSpace(forcedTool) && tools is not null)
            {
                forcedTool = ResolveMemoryRetrieveForPersonalContext(context, tools);
            }

            // Tool-loop turns should be concise: either choose a tool or
            // synthesize the already gathered evidence. Leaving the global
            // output budget here lets local models burn the whole harness
            // timeout on a single overlong generation.
            var forcedToolChoice = tools is not null ? forcedTool : null;
            LlmResponse response;
            try
            {
                response = await _llm
                    .ChatAsync(messages, tools, ToolLoopMaxOutputTokens, forcedToolChoice, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (TryBuildPlacesDiscoverDraftFromRecords(toolCallsMade, context.UserText) is { Text.Length: > 0 })
            {
                var placesDraft = TryBuildPlacesDiscoverDraftFromRecords(toolCallsMade, context.UserText)!;
                var deterministicResponse = new AgentResponse
                {
                    Text = placesDraft.Text,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = round,
                    Sources = placesDraft.Sources,
                };
                return new StepResult.Terminate(deterministicResponse);
            }
            catch (HttpRequestException) when (toolCallsMade.Count > 0)
            {
                var fallback = ToolBackedResponseQualityGuards.TryBuildToolEvidenceFallback(
                    context.UserText ?? string.Empty,
                    toolCallsMade);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    return new StepResult.Terminate(new AgentResponse
                    {
                        Text = fallback,
                        Success = true,
                        ToolCallsMade = toolCallsMade,
                        LlmRoundTrips = round,
                        Sources = SourceCardExtractor.ExtractMerged(toolCallsMade.Select(call => call.Result))
                    });
                }

                throw;
            }

            if (response.ToolCalls is null || response.ToolCalls.Count == 0)
            {
                // INTERVENTION 2 (class C, answer selection): on a strict-compute
                // turn, if the model's OWN successful bare-numeric computations
                // disagree, force exactly one reconciliation round before we let
                // any draft stand. Purely mechanical — we only ever quote back
                // the model's own tool outputs, never a recognized answer.
                if (reconciliationNudges < 1 &&
                    IsStrictComputeTurn(context.UserText) &&
                    IsAdvertisedTool(context.ToolDefs, "python_eval") &&
                    TryDescribeComputeDisagreement(context.UserText, toolCallsMade) is { Length: > 0 } disagreement)
                {
                    reconciliationNudges++;
                    messages.Add(ChatMessage.System(disagreement));
                    forcedToolForNextRound = "python_eval";
                    continue;
                }

                var assistantDraft = response.Content ?? string.Empty;
                var currentTimeDraft = TryBuildCurrentTimeInLocationDraft(context.UserText, toolCallsMade) ??
                                       ToolBackedResponseQualityGuards.TryBuildCurrentTimeInLocationFallback(context.UserText ?? string.Empty, toolCallsMade);
                if (!string.IsNullOrWhiteSpace(currentTimeDraft))
                {
                    assistantDraft = currentTimeDraft;
                }
                else if (TryBuildStrictComputeResultDraft(context.UserText, toolCallsMade) is { Length: > 0 } computeDraft)
                {
                    assistantDraft = computeDraft;
                }

                // Happy path — hand the draft off to the next step
                // (typically PostProcess + ResponseComposer). The context
                // now carries the assembled history, every tool call we
                // made, and the raw model text.
                var updated = context with
                {
                    LlmMessages = messages,
                    ToolCallsMade = toolCallsMade,
                    AssistantDraft = assistantDraft,
                };
                return new StepResult.Continue(updated);
            }

            var responseToolCalls = FilterToAdvertisedToolCalls(response.ToolCalls, tools, out var blockedToolNames);
            if (blockedToolNames.Count > 0)
            {
                messages.Add(ChatMessage.System(
                    "The assistant attempted to call tool(s) that are not available for this turn: " +
                    string.Join(", ", blockedToolNames) + ". Do not call unavailable tools. " +
                    "Use only the available tool results already in the conversation and produce the final answer."));

                if (responseToolCalls.Count == 0)
                    continue;
            }

            // Spin nudge: only after the 2nd consecutive failed repeat so
            // one legitimate retry (transient 503) still gets to run.
            if (!lastCallOk && responseToolCalls.Count > 0)
            {
                var firstSig = BuildCallSignature(responseToolCalls[0]);
                if (callSignatureCounts.TryGetValue(firstSig, out var prior) && prior >= 2)
                {
                    messages.Add(ChatMessage.System(
                        "The previous tool call returned an error and retrying the same call will not help. " +
                        "Stop calling tools. Produce a short final reply that reports what succeeded and " +
                        "what failed in one sentence, then stop."));
                    continue;
                }
            }

            messages.Add(ChatMessage.AssistantToolCalls(responseToolCalls));

            foreach (var call in responseToolCalls)
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

                if (outcome.Ok && LooksLikePlacesDiscoverNeedsLocation(toolName, outcome.ResultText, context.UserText))
                {
                    var updated = context with
                    {
                        LlmMessages = messages,
                        ToolCallsMade = toolCallsMade,
                        AssistantDraft = BuildMissingLocationPlacesReply(context.UserText)
                    };
                    return new StepResult.Continue(updated);
                }

                if (outcome.Ok &&
                    TryBuildPlacesDiscoverDraft(toolName, outcome.ResultText, context.UserText) is { Text.Length: > 0 } placesDraft)
                {
                    var placesResponse = new AgentResponse
                    {
                        Text = placesDraft.Text,
                        Success = true,
                        ToolCallsMade = toolCallsMade,
                        LlmRoundTrips = round + 1,
                        Sources = placesDraft.Sources,
                    };
                    return new StepResult.Terminate(placesResponse);
                }

                if (LooksLikeTimeNowTool(toolName) &&
                    TryBuildCurrentTimeInLocationDraft(context.UserText, toolCallsMade) is { Length: > 0 } currentTimeDraft)
                {
                    var updated = context with
                    {
                        LlmMessages = messages,
                        ToolCallsMade = toolCallsMade,
                        AssistantDraft = currentTimeDraft
                    };
                    return new StepResult.Continue(updated);
                }

                if (LooksLikeResolveTimezoneForCurrentTimeRequest(toolName, context.UserText) &&
                    (!IsAdvertisedTool(context.ToolDefs, ToolNames.TimeNow, ToolNames.TimeNowAlt) ||
                     toolCallsMade.Any(existing => LooksLikeTimeNowTool(existing.ToolName))) &&
                    TryBuildCurrentTimeFromResolvedTimezone(context.UserText, outcome.ResultText, toolCallsMade) is { Length: > 0 } resolvedTimeDraft)
                {
                    var updated = context with
                    {
                        LlmMessages = messages,
                        ToolCallsMade = toolCallsMade,
                        AssistantDraft = resolvedTimeDraft
                    };
                    return new StepResult.Continue(updated);
                }

                if (LooksLikeResolveTimezoneForCurrentTimeRequest(toolName, context.UserText) &&
                    IsAdvertisedTool(context.ToolDefs, ToolNames.TimeNow, ToolNames.TimeNowAlt) &&
                    !toolCallsMade.Any(existing => LooksLikeTimeNowTool(existing.ToolName)))
                {
                    if (await TryExecuteTimeNowAndBuildDraftAsync(context, toolCallsMade, cancellationToken).ConfigureAwait(false) is { Length: > 0 } deterministicTimeDraft)
                    {
                        var updated = context with
                        {
                            LlmMessages = messages,
                            ToolCallsMade = toolCallsMade,
                            AssistantDraft = deterministicTimeDraft
                        };
                        return new StepResult.Continue(updated);
                    }

                    messages.Add(ChatMessage.System(
                        "The timezone lookup resolved the target timezone. Call time_now next, then convert that clock value to the resolved timezone and answer directly."));
                    forcedToolForNextRound = ToolNames.TimeNow;
                }

                if (outcome.Ok &&
                    LooksLikeWeatherGeocodeForCurrentTimeRequest(toolName, context.UserText) &&
                    IsAdvertisedTool(context.ToolDefs, ToolNames.ResolveTimezone, ToolNames.ResolveTimezoneAlt) &&
                    !toolCallsMade.Any(existing =>
                        string.Equals(existing.ToolName, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.ToolName, ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase)))
                {
                    messages.Add(ChatMessage.System(
                        "The geocode result resolved the target location. Call resolve_timezone next with the best matching coordinates, then use time_now if available before answering."));
                    forcedToolForNextRound = ToolNames.ResolveTimezone;
                }

                if (outcome.Ok &&
                    HasSatisfiedWeatherAndNewsRequest(context.UserText, toolCallsMade) &&
                    ToolBackedResponseQualityGuards.TryBuildToolEvidenceFallback(context.UserText ?? string.Empty, toolCallsMade) is { Length: > 0 } weatherNewsDraft)
                {
                    var updated = context with
                    {
                        LlmMessages = messages,
                        ToolCallsMade = toolCallsMade,
                        AssistantDraft = weatherNewsDraft
                    };
                    return new StepResult.Continue(updated);
                }

                if (outcome.Ok &&
                    LooksLikeWeatherGeocodeForWeatherRequest(toolName, context.UserText) &&
                    IsAdvertisedTool(context.ToolDefs, ToolNames.WeatherForecast, ToolNames.WeatherForecastAlt) &&
                    !toolCallsMade.Any(existing =>
                        string.Equals(existing.ToolName, ToolNames.WeatherForecast, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.ToolName, ToolNames.WeatherForecastAlt, StringComparison.OrdinalIgnoreCase)))
                {
                    messages.Add(ChatMessage.System(
                        "The weather_geocode result is sufficient for a general-city weather request. " +
                        "Call weather_forecast next using the best matching coordinates; do not ask the user to confirm the city center."));
                    forcedToolForNextRound = ToolNames.WeatherForecast;
                }

                if (!outcome.Ok && LooksLikeCalculatorParseError(toolName, outcome.ResultText, outcome.Error))
                {
                    messages.Add(ChatMessage.System(
                        "The calculator rejected the last call because it was prose, not arithmetic. " +
                        "Call calculator again with only a pure expression made of numbers, operators, and supported functions. " +
                        "For list/sum problems, enumerate the terms first; do not answer until a calculator call succeeds."));
                    forcedToolForNextRound = toolName;
                }

                // INTERVENTION 1 (class B, script mechanics): a python_eval call
                // that errored (exit_code != 0) or exited 0 with empty stdout is
                // the dominant compute failure. Small models don't recover from
                // the traceback — they fabricate an answer. Inject a targeted
                // rewrite nudge and force python_eval next round. Capped at 2 per
                // turn so a truly stuck problem can't spin the loop.
                if (LooksLikePythonEvalTool(toolName) &&
                    LooksLikeFailedOrEmptyPythonResult(outcome) &&
                    pythonRepairNudges < 2)
                {
                    pythonRepairNudges++;
                    messages.Add(ChatMessage.System(
                        "Your python script failed or printed nothing. Rewrite it as a proper MULTI-LINE script: " +
                        "use real newlines (never put the whole program on one physical line), " +
                        "import only standard-library modules that actually exist, define every name you use, " +
                        "and END with print(<final value>). Then call python_eval again."));
                    forcedToolForNextRound = toolName;
                }

                lastCallOk = outcome.Ok;
                if (LooksLikeUnavailablePlacesLookup(toolName, outcome.ResultText) &&
                    IsAdvertisedTool(context.ToolDefs, ToolNames.WebSearch, ToolNames.WebSearchAlt) &&
                    !toolCallsMade.Any(existing =>
                        string.Equals(existing.ToolName, ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(existing.ToolName, ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)))
                {
                    messages.Add(ChatMessage.System(
                        "places_lookup is unavailable in this environment. Use web_search next to ground the local business answer, " +
                        "then answer with a clear verification caveat instead of mentioning configuration or API keys."));
                    forcedToolForNextRound = ToolNames.WebSearch;
                }

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
            Text = "I got stuck while checking this and couldn't finish the answer cleanly. Please try again; for current information, I'll verify it online before answering.",
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = _maxRoundTrips,
        };
        return new StepResult.Terminate(capResponse);
    }

    private async Task<string?> TryExecuteTimeNowAndBuildDraftAsync(
        TurnContext context,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        const string args = "{}";
        var activityId = Guid.NewGuid().ToString("N");
        var group = _groupClassifier.Classify(ToolNames.TimeNow);

        await _sink.ToolStartedAsync(
                activityId, context.ThreadId, context.MessageId,
                ToolNames.TimeNow, group, args, cancellationToken)
            .ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        var outcome = await ExecuteSingleCallAsync(context, ToolNames.TimeNow, args, activityId, cancellationToken)
            .ConfigureAwait(false);
        sw.Stop();

        await _sink.ToolCompletedAsync(
                activityId, context.ThreadId, context.MessageId, ToolNames.TimeNow,
                outcome.Ok, sw.ElapsedMilliseconds,
                outcome.Ok ? Trim(outcome.ResultText, 280) : null,
                outcome.Error,
                cancellationToken)
            .ConfigureAwait(false);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = ToolNames.TimeNow,
            Arguments = args,
            Result = outcome.ResultText,
            Success = outcome.Ok,
        });

        return TryBuildCurrentTimeInLocationDraft(context.UserText, toolCallsMade);
    }

    private static string? TryBuildCurrentTimeFromResolvedTimezone(
        string? userText,
        string timezoneResult,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return null;

        var timezoneId = ExtractToolValue(timezoneResult, "timezone");
        if (string.IsNullOrWhiteSpace(timezoneId))
            return null;

        var instant = DateTimeOffset.UtcNow;
        DateTimeOffset localTime;
        if (TimeResponseBuilder.TryResolveTimeZoneInfo(timezoneId, out var targetZone))
        {
            localTime = TimeZoneInfo.ConvertTime(instant, targetZone);
        }
        else if (TryResolveFixedTimezoneOffset(timezoneId, out var fixedOffset))
        {
            localTime = instant.ToUniversalTime().ToOffset(fixedOffset);
        }
        else
        {
            return null;
        }

        var location = ExtractTimeLocationFromPrompt(userText);
        var geocodeSource = ExtractLatestToolSource(toolCallsMade, ToolNames.WeatherGeocode, ToolNames.WeatherGeocodeAlt);
        var timezoneSource = ExtractToolValue(timezoneResult, "source");

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(geocodeSource))
            details.Add($"geocode source={geocodeSource}");
        details.Add($"timezone={timezoneId}");
        if (!string.IsNullOrWhiteSpace(timezoneSource))
            details.Add($"timezone source={timezoneSource}");
        details.Add("clock=system UTC");

        return $"It is currently {localTime.ToString("h:mm tt", CultureInfo.InvariantCulture)} on {localTime.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)} in {location} ({timezoneId}). " +
               $"Lookup details: {string.Join("; ", details)}.";
    }

    private static IReadOnlyList<ToolCallRequest> FilterToAdvertisedToolCalls(
        IReadOnlyList<ToolCallRequest> calls,
        IReadOnlyList<ToolDefinition>? tools,
        out IReadOnlyList<string> blockedToolNames)
    {
        blockedToolNames = Array.Empty<string>();
        if (calls.Count == 0 || tools is null || tools.Count == 0)
            return calls;

        var advertised = new HashSet<string>(
            tools.Select(tool => tool.Function?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))!,
            StringComparer.OrdinalIgnoreCase);
        if (advertised.Count == 0)
            return calls;

        List<ToolCallRequest>? allowed = null;
        List<string>? blocked = null;
        foreach (var call in calls)
        {
            var name = call.Function?.Name ?? string.Empty;
            if (advertised.Contains(name))
            {
                allowed?.Add(call);
                continue;
            }

            allowed ??= calls.TakeWhile(existing => !ReferenceEquals(existing, call)).ToList();
            blocked ??= new List<string>();
            if (!string.IsNullOrWhiteSpace(name) && !blocked.Contains(name, StringComparer.OrdinalIgnoreCase))
                blocked.Add(name);
        }

        if (blocked is null)
            return calls;

        blockedToolNames = blocked;
        return allowed is null ? Array.Empty<ToolCallRequest>() : allowed;
    }

    private static bool IsAdvertisedTool(IReadOnlyList<ToolDefinition> tools, params string[] toolNames)
        => tools.Any(tool => toolNames.Any(toolName =>
            string.Equals(tool.Function?.Name, toolName, StringComparison.OrdinalIgnoreCase)));

    private static string? ResolveMemoryRetrieveForPersonalContext(
        TurnContext context,
        IReadOnlyList<ToolDefinition> tools)
    {
        if (context.ToolCallsMade.Any(call =>
                string.Equals(call.ToolName, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(call.ToolName, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (!HasPersonalContextCue(context.UserText))
            return null;

        return tools.Select(tool => tool.Function?.Name)
            .FirstOrDefault(name =>
                string.Equals(name, ToolNames.MemoryRetrieve, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, ToolNames.MemoryRetrieveAlt, StringComparison.OrdinalIgnoreCase));
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

    private static bool LooksLikeUnavailablePlacesLookup(string toolName, string resultText)
    {
           if ((!string.Equals(toolName, ToolNames.PlacesLookup, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(toolName, ToolNames.PlacesLookupAlt, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(resultText))
        {
            return false;
        }

        return resultText.Contains("api key is not configured", StringComparison.OrdinalIgnoreCase) ||
               resultText.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
               resultText.Contains("places lookup error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlacesDiscoverNeedsLocation(string toolName, string resultText, string? userText)
    {
        if ((!string.Equals(toolName, ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(toolName, ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(resultText) ||
            string.IsNullOrWhiteSpace(userText) ||
            !LooksLikeLocalBusinessProximityRequest(userText))
        {
            return false;
        }

        var hasNoResolvedLocation =
            resultText.Contains("\"resolvedLocation\":\"\"", StringComparison.OrdinalIgnoreCase) ||
            resultText.Contains("\"resolvedLocation\": \"\"", StringComparison.OrdinalIgnoreCase);
        var hasNoLocationHint =
            resultText.Contains("\"userLocationHint\":\"\"", StringComparison.OrdinalIgnoreCase) ||
            resultText.Contains("\"userLocationHint\": \"\"", StringComparison.OrdinalIgnoreCase);
        var hasNoCenter = resultText.Contains("\"center\":null", StringComparison.OrdinalIgnoreCase) ||
                          resultText.Contains("\"center\": null", StringComparison.OrdinalIgnoreCase);
        var explicitlyNeedsLocation = resultText.Contains("location", StringComparison.OrdinalIgnoreCase) &&
                                      (resultText.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                                       resultText.Contains("missing", StringComparison.OrdinalIgnoreCase) ||
                                       resultText.Contains("set your", StringComparison.OrdinalIgnoreCase));

        return (hasNoResolvedLocation && hasNoLocationHint) ||
               (hasNoResolvedLocation && hasNoCenter) ||
               explicitlyNeedsLocation;
    }

    private static bool LooksLikeLocalBusinessProximityRequest(string userText)
    {
        var lower = userText.ToLowerInvariant();
        var hasBusinessTerm =
            lower.Contains("florist", StringComparison.Ordinal) ||
            lower.Contains("flower", StringComparison.Ordinal) ||
            lower.Contains("deli", StringComparison.Ordinal) ||
            lower.Contains("bakery", StringComparison.Ordinal) ||
            lower.Contains("restaurant", StringComparison.Ordinal) ||
            lower.Contains("cafe", StringComparison.Ordinal) ||
            lower.Contains("coffee", StringComparison.Ordinal) ||
            lower.Contains("store", StringComparison.Ordinal) ||
            lower.Contains("shop", StringComparison.Ordinal) ||
            lower.Contains("salon", StringComparison.Ordinal) ||
            lower.Contains("pharmacy", StringComparison.Ordinal) ||
            lower.Contains("grocery", StringComparison.Ordinal);
        if (!hasBusinessTerm)
            return false;

        return lower.Contains("nearby", StringComparison.Ordinal) ||
               lower.Contains("near me", StringComparison.Ordinal) ||
               lower.Contains("around here", StringComparison.Ordinal) ||
               lower.Contains("local", StringComparison.Ordinal) ||
               lower.Contains("my area", StringComparison.Ordinal);
    }

    private static string BuildMissingLocationPlacesReply(string? userText)
    {
        var label = InferLocalBusinessLabel(userText);
         var exampleLabel = label.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? label : label + "s";
        return $"I need a location before I can search for a nearby {label}. " +
               "I checked the local places lookup, but it did not have a resolved location for this request. " +
             $"Set Settings -> Location or include a city, for example \"{exampleLabel} in Portland, OR\".";
    }

    private static string InferLocalBusinessLabel(string? userText)
    {
        var lower = (userText ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("flor", StringComparison.Ordinal) || lower.Contains("flower", StringComparison.Ordinal))
            return "florist";
        if (lower.Contains("deli", StringComparison.Ordinal))
            return "deli";
        if (lower.Contains("baker", StringComparison.Ordinal))
            return "bakery";
        if (lower.Contains("cafe", StringComparison.Ordinal))
            return "cafe";
        if (lower.Contains("coffee", StringComparison.Ordinal))
            return "coffee shop";
        if (lower.Contains("restaurant", StringComparison.Ordinal))
            return "restaurant";
        if (lower.Contains("pharmacy", StringComparison.Ordinal))
            return "pharmacy";
        if (lower.Contains("grocery", StringComparison.Ordinal))
            return "grocery store";
        if (lower.Contains("salon", StringComparison.Ordinal))
            return "salon";
        if (lower.Contains("store", StringComparison.Ordinal) || lower.Contains("shop", StringComparison.Ordinal))
            return "shop";
        return "local business";
    }

    private sealed record PlacesDiscoverDraft(string Text, IReadOnlyList<AgentSource> Sources);

    private static PlacesDiscoverDraft? TryBuildPlacesDiscoverDraft(string toolName, string resultText, string? userText)
    {
        if (!string.Equals(toolName, ToolNames.PlacesDiscover, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, ToolNames.PlacesDiscoverAlt, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(resultText))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultText);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return null;
            }

            var label = PluralizeLocalBusinessLabel(InferLocalBusinessLabel(userText));
            var resolvedLocation = root.TryGetProperty("resolvedLocation", out var resolvedEl)
                ? resolvedEl.GetString()
                : null;
            var provider = root.TryGetProperty("provider", out var providerEl)
                ? providerEl.GetString()
                : null;

            var lines = new List<string>();
            foreach (var item in results.EnumerateArray().Take(3))
            {
                var name = ReadJsonString(item, "name");
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var details = new List<string>();
                var address = ReadJsonString(item, "address");
                if (!string.IsNullOrWhiteSpace(address))
                    details.Add(address!);
                if (item.TryGetProperty("distanceMeters", out var distanceEl) &&
                    distanceEl.TryGetInt32(out var distanceMeters) &&
                    distanceMeters >= 0)
                {
                    details.Add(FormatDistance(distanceMeters));
                }

                lines.Add(details.Count == 0
                    ? $"- **{name}**"
                    : $"- **{name}** — {string.Join(" · ", details)}");
            }

            if (lines.Count == 0)
                return null;

            var locationText = string.IsNullOrWhiteSpace(resolvedLocation)
                ? "nearby"
                : $"near {resolvedLocation}";
            var providerText = string.IsNullOrWhiteSpace(provider) ? "Open Places" : provider;

            var text = $"I found these {label} {locationText} via places_discover/{providerText}:\n" +
                       string.Join("\n", lines) +
                       "\n\nOpen Places can miss hours, inventory, and recent closures, so confirm details before heading over.";
            return new PlacesDiscoverDraft(text, BuildPlacesDiscoverSources(results));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PlacesDiscoverDraft? TryBuildPlacesDiscoverDraftFromRecords(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        string? userText)
    {
        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success)
                continue;

            var draft = TryBuildPlacesDiscoverDraft(call.ToolName, call.Result ?? string.Empty, userText);
            if (!string.IsNullOrWhiteSpace(draft?.Text))
                return draft;
        }

        return null;
    }

    private static IReadOnlyList<AgentSource> BuildPlacesDiscoverSources(JsonElement results)
    {
        var sources = new List<AgentSource>();
        foreach (var item in results.EnumerateArray().Take(3))
        {
            var name = ReadJsonString(item, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var url = ReadJsonString(item, "osmUrl");
            if (string.IsNullOrWhiteSpace(url) &&
                item.TryGetProperty("latitude", out var latitudeEl) &&
                item.TryGetProperty("longitude", out var longitudeEl) &&
                latitudeEl.TryGetDouble(out var latitude) &&
                longitudeEl.TryGetDouble(out var longitude))
            {
                url = "https://www.openstreetmap.org/?" +
                      $"mlat={latitude.ToString("F6", CultureInfo.InvariantCulture)}&" +
                      $"mlon={longitude.ToString("F6", CultureInfo.InvariantCulture)}";
            }

            if (string.IsNullOrWhiteSpace(url))
                continue;

            var excerptParts = new List<string>();
            var address = ReadJsonString(item, "address");
            if (!string.IsNullOrWhiteSpace(address))
                excerptParts.Add(address!);
            var category = ReadJsonString(item, "category");
            if (!string.IsNullOrWhiteSpace(category))
                excerptParts.Add(category!);
            if (item.TryGetProperty("distanceMeters", out var distanceEl) &&
                distanceEl.TryGetInt32(out var distanceMeters) &&
                distanceMeters >= 0)
            {
                excerptParts.Add(FormatDistance(distanceMeters));
            }

            sources.Add(new AgentSource
            {
                Url = url!,
                Title = name,
                Domain = "openstreetmap.org",
                Excerpt = string.Join(" · ", excerptParts)
            });
        }

        return sources;
    }

    private static string PluralizeLocalBusinessLabel(string label)
        => label.Equals("deli", StringComparison.OrdinalIgnoreCase)
            ? "delis"
            : label.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? label : label + "s";

    private static string? ReadJsonString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FormatDistance(int distanceMeters)
    {
        if (distanceMeters < 1_000)
            return $"{distanceMeters.ToString(CultureInfo.InvariantCulture)} m away";

        var miles = distanceMeters / 1609.344;
        return $"{miles.ToString("0.0", CultureInfo.InvariantCulture)} mi away";
    }

    private static bool HasSatisfiedWeatherAndNewsRequest(string? userText, IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        if ((!lower.Contains("weather", StringComparison.Ordinal) &&
             !lower.Contains("forecast", StringComparison.Ordinal)) ||
            (!lower.Contains("news", StringComparison.Ordinal) &&
             !lower.Contains("headlines", StringComparison.Ordinal)))
        {
            return false;
        }

        var hasForecast = toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WeatherForecast, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WeatherForecastAlt, StringComparison.OrdinalIgnoreCase)));
        var hasNewsSearch = toolCallsMade.Any(call =>
            call.Success &&
            (call.ToolName.Equals(ToolNames.WebSearch, StringComparison.OrdinalIgnoreCase) ||
             call.ToolName.Equals(ToolNames.WebSearchAlt, StringComparison.OrdinalIgnoreCase)) &&
            (call.Arguments ?? string.Empty).Contains("news", StringComparison.OrdinalIgnoreCase));

        return hasForecast && hasNewsSearch;
    }

    private static bool LooksLikeWeatherGeocodeForWeatherRequest(string toolName, string? userText)
    {
        if (!string.Equals(toolName, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, ToolNames.WeatherGeocodeAlt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        return lower.Contains("weather", StringComparison.Ordinal) ||
               lower.Contains("forecast", StringComparison.Ordinal) ||
               lower.Contains("outlook", StringComparison.Ordinal);
    }

    private static bool LooksLikeWeatherGeocodeForCurrentTimeRequest(string toolName, string? userText)
    {
        if (!string.Equals(toolName, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, ToolNames.WeatherGeocodeAlt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(userText) && LooksLikeCurrentTimeRequest(userText);
    }

    private static bool LooksLikeResolveTimezoneForCurrentTimeRequest(string toolName, string? userText)
    {
        if (!string.Equals(toolName, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(toolName, ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(userText))
            return false;

        var lower = userText.ToLowerInvariant();
        return lower.Contains("time", StringComparison.Ordinal) ||
               lower.Contains("timezone", StringComparison.Ordinal);
    }

    private static bool LooksLikeTimeNowTool(string toolName)
        => string.Equals(toolName, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase) ||
           string.Equals(toolName, ToolNames.TimeNowAlt, StringComparison.OrdinalIgnoreCase);

    private static string? TryBuildCurrentTimeInLocationDraft(
        string? userText,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (string.IsNullOrWhiteSpace(userText) || !LooksLikeCurrentTimeRequest(userText))
            return null;

        var timezoneCall = toolCallsMade.LastOrDefault(call =>
            (string.Equals(call.ToolName, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(call.ToolName, ToolNames.ResolveTimezoneAlt, StringComparison.OrdinalIgnoreCase)) &&
            !string.IsNullOrWhiteSpace(call.Result));
        var timeNowCall = toolCallsMade.LastOrDefault(call =>
            LooksLikeTimeNowTool(call.ToolName) &&
            !string.IsNullOrWhiteSpace(call.Result));

        if (timezoneCall is null || timeNowCall is null)
            return null;

        var timezoneId = ExtractToolValue(timezoneCall.Result ?? string.Empty, "timezone");
        if (string.IsNullOrWhiteSpace(timezoneId))
        {
            return null;
        }

        if (!TryExtractClockInstant(timeNowCall.Result ?? string.Empty, out var instant, out var clockLabel))
        {
            instant = DateTimeOffset.UtcNow;
            clockLabel = "system UTC";
        }

        DateTimeOffset localTime;
        if (TimeResponseBuilder.TryResolveTimeZoneInfo(timezoneId, out var targetZone))
        {
            localTime = TimeZoneInfo.ConvertTime(instant, targetZone);
        }
        else if (TryResolveFixedTimezoneOffset(timezoneId, out var fixedOffset))
        {
            localTime = instant.ToUniversalTime().ToOffset(fixedOffset);
        }
        else
        {
            return null;
        }

        var location = ExtractTimeLocationFromPrompt(userText);
        var geocodeSource = ExtractLatestToolSource(toolCallsMade, ToolNames.WeatherGeocode, ToolNames.WeatherGeocodeAlt);
        var timezoneSource = ExtractToolValue(timezoneCall.Result ?? string.Empty, "source");

        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(geocodeSource))
            details.Add($"geocode source={geocodeSource}");
        details.Add($"timezone={timezoneId}");
        if (!string.IsNullOrWhiteSpace(timezoneSource))
            details.Add($"timezone source={timezoneSource}");
        if (!string.IsNullOrWhiteSpace(clockLabel))
            details.Add($"time_now={clockLabel}");

        return $"It is currently {localTime.ToString("h:mm tt", CultureInfo.InvariantCulture)} on {localTime.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture)} in {location} ({timezoneId}). " +
               $"Lookup details: {string.Join("; ", details)}.";
    }

    private static bool TryResolveFixedTimezoneOffset(string timezoneId, out TimeSpan offset)
    {
        if (string.Equals(timezoneId, "Asia/Tokyo", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.FromHours(9);
            return true;
        }

        if (string.Equals(timezoneId, "UTC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(timezoneId, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            offset = TimeSpan.Zero;
            return true;
        }

        offset = TimeSpan.Zero;
        return false;
    }

    private static bool LooksLikeCurrentTimeRequest(string userText)
    {
        var lower = userText.ToLowerInvariant();
        return lower.Contains("time", StringComparison.Ordinal) &&
               (lower.Contains(" right now", StringComparison.Ordinal) ||
                lower.Contains(" now", StringComparison.Ordinal) ||
                lower.Contains("current", StringComparison.Ordinal) ||
                Regex.IsMatch(lower, @"\bwhat(?:'s|\s+is)\s+.*\btime\b", RegexOptions.CultureInvariant));
    }

    private static bool TryExtractClockInstant(string result, out DateTimeOffset instant, out string clockLabel)
    {
        instant = default;
        clockLabel = string.Empty;

        if (string.IsNullOrWhiteSpace(result))
            return false;

        var extractedIso = ExtractJsonString(result, "iso");
        if (!string.IsNullOrWhiteSpace(extractedIso) &&
            DateTimeOffset.TryParse(extractedIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant))
        {
            clockLabel = extractedIso;
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            if (!document.RootElement.TryGetProperty("iso", out var isoProperty))
                return false;

            var iso = isoProperty.GetString();
            if (string.IsNullOrWhiteSpace(iso) ||
                !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant))
            {
                return false;
            }

            clockLabel = iso;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ExtractTimeLocationFromPrompt(string userText)
    {
        var match = Regex.Match(
            userText,
            @"\b(?:time|timezone)\b.*\b(?:in|at|for)\s+(?<location>[A-Za-z][A-Za-z0-9 .,'-]{1,80}?)(?:\s+(?:right\s+now|now)|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || LooksLikeFormatInstructionLocation(match.Groups["location"].Value))
        {
            match = Regex.Match(
                userText,
                @"\b(?:in|at|for|with\s+someone\s+in)\s+(?<location>[A-Za-z][A-Za-z0-9 .,'-]{1,80}?)(?:[?.!,]|\s+(?:use|then|and)\b|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (!match.Success)
            return "the requested location";

        var location = Regex.Replace(match.Groups["location"].Value.Trim(), @"\s+", " ");
        return string.IsNullOrWhiteSpace(location) ? "the requested location" : location.Trim(',', '.', '?', '!');
    }

    private static bool LooksLikeFormatInstructionLocation(string value)
    {
        var lower = (value ?? string.Empty).Trim().ToLowerInvariant();
        return lower.StartsWith("one ", StringComparison.Ordinal) ||
               lower.StartsWith("a ", StringComparison.Ordinal) ||
               lower.Contains("sentence", StringComparison.Ordinal) ||
               lower.Contains("paragraph", StringComparison.Ordinal) ||
               lower.Contains("bullet", StringComparison.Ordinal);
    }

    private static string ExtractLatestToolSource(
        IReadOnlyList<ToolCallRecord> toolCallsMade,
        params string[] toolNames)
    {
        foreach (var call in toolCallsMade.Reverse())
        {
            if (!call.Success || string.IsNullOrWhiteSpace(call.Result))
                continue;
            if (!toolNames.Any(name => string.Equals(call.ToolName, name, StringComparison.OrdinalIgnoreCase)))
                continue;

            var source = ExtractToolValue(call.Result ?? string.Empty, "source");
            if (!string.IsNullOrWhiteSpace(source))
                return source;
        }

        return string.Empty;
    }

    private static string ExtractToolValue(string result, string key)
    {
        if (string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var match = Regex.Match(
            result,
            $@"\b{Regex.Escape(key)}=(?<value>[^,\]\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success)
            return match.Groups["value"].Value.Trim();

        return ExtractJsonString(result, key)?.Trim() ?? string.Empty;
    }

    private static string? ExtractJsonString(string text, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(propertyName))
            return null;

        if (TryMatchJsonString(text, propertyName) is { Length: > 0 } direct)
            return direct;

        if (text.Contains("\\\"", StringComparison.Ordinal) &&
            TryMatchJsonString(text.Replace("\\\"", "\""), propertyName) is { Length: > 0 } unescaped)
        {
            return unescaped;
        }

        return null;
    }

    private static string? TryMatchJsonString(string text, string propertyName)
    {
        var match = Regex.Match(
            text,
            $"\\\"{Regex.Escape(propertyName)}\\\"\\s*:\\s*\\\"(?<value>[^\\\"]*)\\\"",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value : null;
    }


    private string ApplyArgsRewriters(TurnContext context, string toolName, string args)
    {
        var current = args;
        foreach (var rewriter in _argsRewriters)
            current = rewriter.Rewrite(context, toolName, current) ?? current;
        return current;
    }

    private static bool LooksLikeCalculatorParseError(string toolName, string resultText, string? error) =>
        string.Equals(toolName, "calculator", StringComparison.OrdinalIgnoreCase) &&
        (ContainsCalculatorParseCue(resultText) || ContainsCalculatorParseCue(error));

    private static bool ContainsCalculatorParseCue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("pure arithmetic expression", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikePythonEvalTool(string toolName) =>
        string.Equals(toolName, "python_eval", StringComparison.OrdinalIgnoreCase);

    // A python_eval call fails "mechanically" when the sandbox exited non-zero
    // (SyntaxError, NameError, fake import, stray ')') OR the interpreter exited
    // 0 but printed nothing (a script that computed but forgot its final print).
    // Both leave the model with no usable value; it fabricates one otherwise.
    private static bool LooksLikeFailedOrEmptyPythonResult(ToolCallOutcome outcome)
    {
        if (!outcome.Ok)
            return true;

        if (string.IsNullOrWhiteSpace(outcome.ResultText))
            return true;

        try
        {
            using var doc = JsonDocument.Parse(outcome.ResultText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("exit_code", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.GetInt32() != 0)
            {
                return true;
            }

            var stdout = root.TryGetProperty("stdout", out var stdoutEl) && stdoutEl.ValueKind == JsonValueKind.String
                ? stdoutEl.GetString()
                : null;
            var hasResult = root.TryGetProperty("result", out var resultEl) &&
                            resultEl.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(resultEl.GetString());

            // Exit 0 with empty/whitespace stdout and no explicit result value:
            // the script ran but produced no answer to read.
            return !hasResult && string.IsNullOrWhiteSpace(stdout);
        }
        catch (JsonException)
        {
            // Non-JSON payloads from a successful call are unusual for the
            // sandbox; leave them alone rather than guess at a repair.
            return false;
        }
    }

    // Strict-compute turn: the user explicitly directed a compute tool AND
    // demanded a bare number ("reply with only the integer/number/value").
    // Shared by the draft adoption and the reconciliation trigger so both
    // always agree on which turns qualify.
    private static bool IsStrictComputeTurn(string? userText) =>
        !string.IsNullOrWhiteSpace(userText) &&
        Regex.IsMatch(userText, @"reply\s+with\s+only\s+the\s+(integer|number|value)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
        StrictComputeTools.Any(tool => userText.Contains(tool, StringComparison.OrdinalIgnoreCase));

    // Compute tools whose successful output IS the answer for a strict
    // "reply with only the number" turn. Small models sometimes get the right
    // value from their own tool call and then mistranscribe it in the final
    // draft (observed: calculator returned 576, model answered 600; python
    // printed 111, model answered 1). When the user explicitly directed the
    // tool and demanded a bare number, the model's own successful computation
    // wins over its transcription. The reasoning (expression / program
    // construction) remains entirely the model's.
    private static readonly string[] StrictComputeTools = ["calculator", "python_eval"];

    // Every successful, bare-numeric result the model's own compute tools
    // produced this turn, oldest→newest. Failed scripts (exit_code != 0) are
    // excluded — their stdout is from a broken program, never an answer.
    private static List<string> CollectSuccessfulComputeValues(
        string? userText,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var values = new List<string>();
        if (string.IsNullOrWhiteSpace(userText))
            return values;

        foreach (var call in toolCallsMade)
        {
            if (!call.Success)
                continue;

            var tool = StrictComputeTools.FirstOrDefault(t =>
                string.Equals(call.ToolName, t, StringComparison.OrdinalIgnoreCase));
            if (tool is null || !userText.Contains(tool, StringComparison.OrdinalIgnoreCase))
                continue;

            if (TryExtractBareNumericComputeValue(call.Result) is { Length: > 0 } value)
                values.Add(value);
        }

        return values;
    }

    private static string? TryExtractBareNumericComputeValue(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(result);

            // Failed scripts return exit_code != 0 with Ok=true (the model
            // is meant to read the traceback) — never treat those as answers.
            if (doc.RootElement.TryGetProperty("exit_code", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.GetInt32() != 0)
            {
                return null;
            }

            string? value = null;
            if (doc.RootElement.TryGetProperty("result", out var resultEl) &&
                resultEl.ValueKind == JsonValueKind.String)
            {
                value = resultEl.GetString();
            }
            else if (doc.RootElement.TryGetProperty("stdout", out var stdoutEl) &&
                     stdoutEl.ValueKind == JsonValueKind.String)
            {
                value = stdoutEl.GetString()?.Trim();
            }

            // Only a bare numeric output is unambiguous enough to adopt.
            if (!string.IsNullOrWhiteSpace(value) &&
                Regex.IsMatch(value, @"^-?\d+(?:\.\d+)?$", RegexOptions.CultureInvariant))
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed tool output and let the model draft stand.
        }

        return null;
    }

    // Refactored per INTERVENTION 2: adopt the MAJORITY value among all the
    // turn's successful bare-numeric compute results; ties break to the newest.
    // (Previously newest-only, which let a subtly corrupted "verify" call
    // clobber a correct first result.) Single or agreeing results behave
    // exactly as before: the majority of one value, or of equal values, is
    // that value.
    private static string? TryBuildStrictComputeResultDraft(string? userText, IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        if (!IsStrictComputeTurn(userText))
            return null;

        var values = CollectSuccessfulComputeValues(userText, toolCallsMade);
        return SelectMajorityThenNewest(values);
    }

    // Majority wins; on a tie the newest (last appended) of the tied values wins.
    private static string? SelectMajorityThenNewest(IReadOnlyList<string> valuesOldestFirst)
    {
        if (valuesOldestFirst.Count == 0)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in valuesOldestFirst)
            counts[value] = counts.GetValueOrDefault(value) + 1;

        var maxCount = counts.Values.Max();

        // Walk newest→oldest and return the first value that hits the max
        // count, so a tie resolves to the most recent computation.
        for (var i = valuesOldestFirst.Count - 1; i >= 0; i--)
        {
            if (counts[valuesOldestFirst[i]] == maxCount)
                return valuesOldestFirst[i];
        }

        return valuesOldestFirst[^1];
    }

    // INTERVENTION 2 trigger: names the disagreement when the turn's successful
    // bare-numeric compute results contain >= 2 DISTINCT values. The message
    // quotes back only the model's own outputs and asks for one clean script.
    private static string? TryDescribeComputeDisagreement(
        string? userText,
        IReadOnlyList<ToolCallRecord> toolCallsMade)
    {
        var values = CollectSuccessfulComputeValues(userText, toolCallsMade);
        var distinct = values.Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count < 2)
            return null;

        return $"Your computations returned different values: {string.Join(" and ", distinct)}. " +
               "Write ONE careful, multi-line script that computes the answer from scratch, " +
               "define every name you use, and print only the final value.";
    }

    private static bool ShouldAddCalculatorSetupHint(TurnContext context) =>
        !string.IsNullOrWhiteSpace(context.UserText) &&
        context.UserText.Contains("calculator", StringComparison.OrdinalIgnoreCase) &&
        context.ToolDefs.Any(tool =>
            string.Equals(tool.Function?.Name, "calculator", StringComparison.OrdinalIgnoreCase));

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

        // Pre-flight argument check: catch structurally broken arguments
        // (unparseable JSON, missing required params) before spending a tool
        // round-trip, and hand the model a schema-aware repair message so it can
        // re-formulate the call rather than guess. Only fires on definitely-fatal
        // issues, so a call that would have worked is never blocked.
        var toolDef = context.ToolDefs?
            .FirstOrDefault(d => string.Equals(d.Function?.Name, toolName, StringComparison.OrdinalIgnoreCase));
        if (toolDef is not null)
        {
            var argCheck = ToolArgumentValidator.Validate(args, toolDef);
            if (!argCheck.IsValid && argCheck.Issues.Any(ToolArgumentRepair.IsFatalIssue))
            {
                return new ToolCallOutcome(
                    ToolArgumentRepair.BuildStructuredError(toolName, toolDef, argCheck.Issues),
                    Ok: false,
                    Error: "invalid_arguments");
            }
        }

        // Fall through to the real MCP server.
        try
        {
            var result = await _mcp.CallToolAsync(toolName, args, ct).ConfigureAwait(false);
            if (TryExtractStructuredToolError(result, out var error))
                return new ToolCallOutcome(result, Ok: false, Error: error);

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

    private static bool TryExtractStructuredToolError(string payload, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        var trimmed = payload.TrimStart();
        if (trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
        {
            error = trimmed["Error:".Length..].Trim();
            return !string.IsNullOrWhiteSpace(error);
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("error", out var errorEl))
            {
                return false;
            }

            if (errorEl.ValueKind == JsonValueKind.String)
            {
                error = errorEl.GetString() ?? "";
                return !string.IsNullOrWhiteSpace(error);
            }

            if (errorEl.ValueKind != JsonValueKind.Object)
                return false;

            var code = "";
            var message = "";
            if (errorEl.TryGetProperty("code", out var codeEl) &&
                codeEl.ValueKind == JsonValueKind.String)
            {
                code = codeEl.GetString() ?? "";
            }

            if (errorEl.TryGetProperty("message", out var messageEl) &&
                messageEl.ValueKind == JsonValueKind.String)
            {
                message = messageEl.GetString() ?? "";
            }

            error = string.Join(": ", new[] { code, message }.Where(value => !string.IsNullOrWhiteSpace(value)));
            return !string.IsNullOrWhiteSpace(error);
        }
        catch
        {
            return false;
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
