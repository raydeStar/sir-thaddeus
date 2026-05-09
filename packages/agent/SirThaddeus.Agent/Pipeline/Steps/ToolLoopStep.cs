using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SirThaddeus.Agent.Utilities;
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
        string? forcedToolForNextRound = null;

        for (var round = 0; round < _maxRoundTrips; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            LlmResponse response;
            if (!string.IsNullOrWhiteSpace(forcedTool) && tools is not null)
            {
                // Router-directed call: pass tool_choice through so the
                // model cannot answer from stale training memory before the
                // lookup runs.
                response = await _llm
                    .ChatAsync(messages, tools, forcedTool, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                response = await _llm
                    .ChatAsync(messages, tools, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (response.ToolCalls is null || response.ToolCalls.Count == 0)
            {
                var assistantDraft = response.Content ?? string.Empty;
                var currentTimeDraft = TryBuildCurrentTimeInLocationDraft(context.UserText, toolCallsMade) ??
                                       ToolBackedResponseQualityGuards.TryBuildCurrentTimeInLocationFallback(context.UserText ?? string.Empty, toolCallsMade);
                if (!string.IsNullOrWhiteSpace(currentTimeDraft))
                {
                    assistantDraft = currentTimeDraft;
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
            Text = "(Tool-call loop hit its round-trip cap without a final answer. Try rephrasing or simplifying the request.)",
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
        if (!match.Success)
            return "the requested location";

        var location = Regex.Replace(match.Groups["location"].Value.Trim(), @"\s+", " ");
        return string.IsNullOrWhiteSpace(location) ? "the requested location" : location.Trim(',', '.', '?', '!');
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
