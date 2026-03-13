using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static SirThaddeus.Agent.OrchestratorMessageHelpers;
using SirThaddeus.Agent.Dialogue;
using SirThaddeus.Agent.Guardrails;
using SirThaddeus.Agent.Memory;
using SirThaddeus.Agent.PostProcessing;
using SirThaddeus.Agent.ConversationSegmentation;
using SirThaddeus.Agent.Routing;
using SirThaddeus.Agent.Search;
using SirThaddeus.Agent.ToolLoop;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine.Formatting;

namespace SirThaddeus.Agent;

public sealed partial class AgentOrchestrator
{

    // ─────────────────────────────────────────────────────────────────
    // Screen Observe: deterministic capture + LLM describe
    //
    // Local models are unreliable at calling tools via function
    // calling. For ScreenObserve we call screen_capture directly
    // and inject the result, then ask the LLM to describe it.
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> ExecuteDeterministicScreenCaptureAsync(
        string contextualUserMessage,
        string memoryPackText,
        string personalityAnchor,
        string personalityTurnTag,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        LlmUsageSnapshot? usageBaseline,
        CancellationToken cancellationToken)
    {
        var screenResult = await CallToolWithAliasAsync(
            ScreenCaptureToolName, ScreenCaptureToolNameAlt,
            "{}", cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = screenResult.ToolName,
            Arguments = "{}",
            Result = screenResult.Result,
            Success = screenResult.Success
        });

        if (!screenResult.Success || string.IsNullOrWhiteSpace(screenResult.Result))
        {
            var errorText = "I wasn't able to capture your screen. " +
                            "Make sure the ScreenRead permission is enabled in Settings.";
            _history.Add(ChatMessage.Assistant(errorText));
            LogEvent("SCREEN_CAPTURE_FAILED", screenResult.Result ?? "(empty)");
            return AttachContextSnapshot(new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            }, usageBaseline);
        }

        LogEvent("SCREEN_CAPTURE_OK",
            $"Captured {screenResult.Result.Length} chars of screen data.");

        if (!string.IsNullOrWhiteSpace(memoryPackText))
            InjectMemoryIntoHistoryInPlace(_history, memoryPackText);
        InjectPersonalityAnchorIntoHistoryInPlace(_history, personalityAnchor, personalityTurnTag);

        _history.Add(ChatMessage.System(
            "The following is the result of capturing the user's current screen. " +
            "Describe what you see accurately. Do NOT fabricate details. " +
            "If the text is unclear or partial, say so.\n\n" +
            screenResult.Result));

        var messages = _history.ToList();
        InjectFewShotExamplesInPlace(messages, _personalityRuntime.Snapshot.Profile.Instructions.FewShotExamples);

        roundTrips++;
        var screenResponse = await CallLlmWithRetrySafe(
            messages, roundTrips, _maxTokensCasual, cancellationToken);

        var screenText = _postProcessor.ProcessChatOnlyDraft(
            screenResponse.Content ?? "[No response]",
            contextualUserMessage,
            toolCallsMade,
            LogEvent);

        _history.Add(ChatMessage.Assistant(screenText));
        LogEvent("AGENT_RESPONSE", screenText);

        return AttachContextSnapshot(new AgentResponse
        {
            Text = screenText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips,
            AllowToolResultPersonalityPresentation = true
        }, usageBaseline);
    }

    // ─────────────────────────────────────────────────────────────────
    // Tool Loop
    //
    // Shared by both casual (memory-only tools) and tooling (all tools)
    // paths. Iterates until the LLM produces a final text answer or
    // we hit the safety cap.
    // ─────────────────────────────────────────────────────────────────

    private async Task<AgentResponse> RunToolLoopAsync(
        IReadOnlyList<ToolDefinition> tools,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        return await _toolLoopExecutor.ExecuteAsync(
            new ToolLoopExecutionRequest
            {
                History = _history,
                Tools = tools,
                ToolCallsMade = toolCallsMade,
                InitialRoundTrips = roundTrips,
                MaxRoundTrips = MaxToolRoundTrips,
                Decision = new Orchestration.IntentDecisionV2 { Intent = "GeneralTool", Confidence = 1.0 },
                SanitizeAssistantText = text =>
                {
                    var responseKind = _responseKindClassifier.Classify(text, hasToolEvidence: true);
                    var preserveRationale = responseKind is ResponseKind.Reasoning;

                    var output = StripThinkingScaffold(text ?? "[No response]", preserveRationale);
                    output = TruncateSelfDialogue(output);
                    output = StripRawTemplateTokens(output);
                    output = TrimDanglingIncompleteEnding(output);
                    return output;
                },
                LogEvent = LogEvent,
                FewShotExamples = _personalityRuntime.Snapshot.Profile.Instructions.FewShotExamples
            },
            cancellationToken);
    }

    private static bool IsLmStudioRegexFailure(HttpRequestException ex)
    {
        var msg = ex.Message ?? "";
        return msg.Contains("Failed to process regex", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────
    // Web Search Execution (shared pipeline)
    //
    // Single implementation of the extract → search → summarize flow.
    // Called from the primary WebLookup intent path and from the
    // chat-only fallback. Keeps all tool-name negotiation, raw-dump
    // rewriting, and template stripping in one place.
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes the weather utility flow using dedicated MCP tools:
    /// weather_geocode -> weather_forecast. Returns a short deterministic
    /// weather summary without re-entering the web search pipeline.
    /// </summary>
    private async Task<AgentResponse> ExecuteWeatherUtilityAsync(
        string userMessage,
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        ValidatedSlots? validatedSlots = null)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Weather utility is missing required geocode args.");

        var geocodeCall = await CallToolWithAliasAsync(
            WeatherGeocodeToolName, WeatherGeocodeToolNameAlt,
            utilityResult.McpToolArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = geocodeCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = geocodeCall.Result,
            Success = geocodeCall.Success
        });

        if (!geocodeCall.Success)
        {
            var errorText = "I couldn't resolve that location for weather lookup. " +
                            "Try a city and region like \"Portland, OR\".";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        if (!TryParseBestGeocodeCandidate(geocodeCall.Result, out var geo))
        {
            var noLocationText = "I couldn't find coordinates for that location. " +
                                 "Try a more specific place name.";
            _history.Add(ChatMessage.Assistant(noLocationText));
            return new AgentResponse
            {
                Text = noLocationText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var activeState = _dialogueStore.Get();
        var explicitLocationChange = validatedSlots?.ExplicitLocationChange ?? false;
        var mismatchReason = "";
        var geocodeMismatch = false;
        if (!explicitLocationChange)
        {
            geocodeMismatch = ValidateSlots.IsStronglyDivergent(
                activeState,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                out mismatchReason);
        }
        var mismatchWarning = "";

        if (geocodeMismatch)
        {
            if (_validateSlots.ShouldRequireConfirm())
            {
                var confirmText =
                    $"I found **{geo.Name}**, but that conflicts with your current location context " +
                    $"(**{activeState.LocationName ?? "unknown"}**). Please confirm if you want me to switch.";

                _history.Add(ChatMessage.Assistant(confirmText));
                _dialogueStore.Update(activeState with { GeocodeMismatch = true });
                return new AgentResponse
                {
                    Text = confirmText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            if (!activeState.ContextLocked &&
                activeState.Latitude.HasValue &&
                activeState.Longitude.HasValue &&
                !string.IsNullOrWhiteSpace(activeState.LocationName))
            {
                mismatchWarning =
                    $"I detected a location mismatch ({mismatchReason.Replace('_', ' ')}), " +
                    $"so I kept your prior location context: **{activeState.LocationName}**.";

                geo = (
                    activeState.LocationName!,
                    activeState.CountryCode ?? geo.CountryCode,
                    activeState.RegionCode ?? geo.RegionCode,
                    activeState.Latitude.Value,
                    activeState.Longitude.Value
                );
            }
        }

        _contextAnchoringService.ApplyPatch(
            _contextAnchoringService.CreatePlacePatch(
                geo.Name,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                locationInferred: validatedSlots?.LocationInferred ?? false,
                geocodeMismatch: geocodeMismatch,
                explicitLocationChange: validatedSlots?.ExplicitLocationChange ?? false));

        var forecastArgs = JsonSerializer.Serialize(new
        {
            latitude = geo.Latitude,
            longitude = geo.Longitude,
            placeHint = geo.Name,
            countryCode = geo.CountryCode,
            days = 7
        });

        var forecastCall = await CallToolWithAliasAsync(
            WeatherForecastToolName, WeatherForecastToolNameAlt,
            forecastArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = forecastCall.ToolName,
            Arguments = forecastArgs,
            Result = forecastCall.Result,
            Success = forecastCall.Success
        });

        if (!forecastCall.Success)
        {
            var errorText = "I couldn't fetch the weather details right now. " +
                            "Please try again in a moment.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var weatherBrief = TryBuildWeatherBriefFromForecastJson(
            forecastCall.Result, userMessage, geo.Name);

        if (string.IsNullOrWhiteSpace(weatherBrief))
        {
            weatherBrief = "I found weather data, but couldn't extract a clean snapshot yet. " +
                           "Try asking again and I'll refresh it.";
        }

        if (!string.IsNullOrWhiteSpace(mismatchWarning))
            weatherBrief = $"{mismatchWarning}\n\n{weatherBrief}";

        _history.Add(ChatMessage.Assistant(weatherBrief));
        LogEvent("AGENT_RESPONSE", weatherBrief);

        return new AgentResponse
        {
            Text = weatherBrief,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteTimeUtilityAsync(
        string userMessage,
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        ValidatedSlots? validatedSlots = null)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Time utility is missing required geocode args.");

        var geocodeCall = await CallToolWithAliasAsync(
            WeatherGeocodeToolName, WeatherGeocodeToolNameAlt,
            utilityResult.McpToolArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = geocodeCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = geocodeCall.Result,
            Success = geocodeCall.Success
        });

        if (!geocodeCall.Success)
        {
            var errorText = "I couldn't resolve that location for a timezone lookup.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        if (!TryParseBestGeocodeCandidate(geocodeCall.Result, out var geo))
        {
            var noLocationText = "I couldn't find coordinates for that location. Try a more specific city/country.";
            _history.Add(ChatMessage.Assistant(noLocationText));
            return new AgentResponse
            {
                Text = noLocationText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var activeState = _dialogueStore.Get();
        var explicitLocationChange = validatedSlots?.ExplicitLocationChange ?? false;
        var mismatchReason = "";
        var geocodeMismatch = false;
        if (!explicitLocationChange)
        {
            geocodeMismatch = ValidateSlots.IsStronglyDivergent(
                activeState,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                out mismatchReason);
        }
        var mismatchWarning = "";

        if (geocodeMismatch)
        {
            if (_validateSlots.ShouldRequireConfirm())
            {
                var confirmText =
                    $"I found **{geo.Name}**, but that conflicts with your current location context " +
                    $"(**{activeState.LocationName ?? "unknown"}**). Please confirm if you want me to switch.";

                _history.Add(ChatMessage.Assistant(confirmText));
                _dialogueStore.Update(activeState with { GeocodeMismatch = true });
                return new AgentResponse
                {
                    Text = confirmText,
                    Success = true,
                    ToolCallsMade = toolCallsMade,
                    LlmRoundTrips = roundTrips
                };
            }

            if (!activeState.ContextLocked &&
                activeState.Latitude.HasValue &&
                activeState.Longitude.HasValue &&
                !string.IsNullOrWhiteSpace(activeState.LocationName))
            {
                mismatchWarning =
                    $"I detected a location mismatch ({mismatchReason.Replace('_', ' ')}), " +
                    $"so I kept your prior location context: **{activeState.LocationName}**.";

                geo = (
                    activeState.LocationName!,
                    activeState.CountryCode ?? geo.CountryCode,
                    activeState.RegionCode ?? geo.RegionCode,
                    activeState.Latitude.Value,
                    activeState.Longitude.Value
                );
            }
        }

        _contextAnchoringService.ApplyPatch(
            _contextAnchoringService.CreatePlacePatch(
                geo.Name,
                geo.CountryCode,
                geo.RegionCode,
                geo.Latitude,
                geo.Longitude,
                locationInferred: validatedSlots?.LocationInferred ?? false,
                geocodeMismatch: geocodeMismatch,
                explicitLocationChange: validatedSlots?.ExplicitLocationChange ?? false));

        var timezoneArgs = JsonSerializer.Serialize(new
        {
            latitude = geo.Latitude,
            longitude = geo.Longitude,
            countryCode = geo.CountryCode
        });

        var timezoneCall = await CallToolWithAliasAsync(
            ResolveTimezoneToolName, ResolveTimezoneToolNameAlt,
            timezoneArgs, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = timezoneCall.ToolName,
            Arguments = timezoneArgs,
            Result = timezoneCall.Result,
            Success = timezoneCall.Success
        });

        if (!timezoneCall.Success)
        {
            var errorText = "I couldn't resolve the timezone for that location right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var timeBrief = TryBuildTimeBriefFromTimezoneJson(
            timezoneCall.Result, geo.Name, userMessage);

        if (string.IsNullOrWhiteSpace(timeBrief))
        {
            timeBrief = $"I found the location for **{geo.Name}**, but couldn't build a clean time answer yet.";
        }

        if (!string.IsNullOrWhiteSpace(mismatchWarning))
            timeBrief = $"{mismatchWarning}\n\n{timeBrief}";

        _history.Add(ChatMessage.Assistant(timeBrief));
        LogEvent("AGENT_RESPONSE", timeBrief);

        return new AgentResponse
        {
            Text = timeBrief,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteHolidayUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Holiday utility is missing tool args.");

        var holidayCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = holidayCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = holidayCall.Result,
            Success = holidayCall.Success
        });

        if (!holidayCall.Success)
        {
            var errorText = "I couldn't fetch holiday data right now. Please try again in a moment.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var holidayText = BuildHolidayUtilityResponse(
            utilityResult.McpToolName, holidayCall.Result);

        _history.Add(ChatMessage.Assistant(holidayText));
        LogEvent("AGENT_RESPONSE", holidayText);

        return new AgentResponse
        {
            Text = holidayText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteFeedUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Feed utility is missing tool args.");

        var feedCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = feedCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = feedCall.Result,
            Success = feedCall.Success
        });

        if (!feedCall.Success)
        {
            var errorText = "I couldn't fetch that feed right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var feedText = BuildFeedUtilityResponse(feedCall.Result);
        _history.Add(ChatMessage.Assistant(feedText));
        LogEvent("AGENT_RESPONSE", feedText);

        return new AgentResponse
        {
            Text = feedText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteStatusUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError("Status utility is missing tool args.");

        var statusCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = statusCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = statusCall.Result,
            Success = statusCall.Success
        });

        if (!statusCall.Success)
        {
            var errorText = "I couldn't complete that reachability check right now.";
            _history.Add(ChatMessage.Assistant(errorText));
            return new AgentResponse
            {
                Text = errorText,
                Success = false,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var statusText = BuildStatusUtilityResponse(statusCall.Result);
        _history.Add(ChatMessage.Assistant(statusText));
        LogEvent("AGENT_RESPONSE", statusText);

        return new AgentResponse
        {
            Text = statusText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    // ── Tool alias resolution — delegates to ToolAliasResolver ──────

    private Task<(string ToolName, string Result, bool Success)> CallUtilityToolWithAliasAsync(
        string toolName, string argsJson, CancellationToken cancellationToken) =>
        _toolAliasResolver.CallUtilityWithAliasAsync(toolName, argsJson, cancellationToken);

    private static string ToPascalCaseToolAlias(string toolName) =>
        Agent.Tools.ToolAliasResolver.ToPascalCaseAlias(toolName);

    private Task<(string ToolName, string Result, bool Success)> CallToolWithAliasAsync(
        string primaryToolName, string alternateToolName, string argsJson,
        CancellationToken cancellationToken) =>
        _toolAliasResolver.CallWithAliasAsync(primaryToolName, alternateToolName, argsJson, cancellationToken);

    // ── Utility response building — delegates to UtilityResponseBuilder ──

    private static bool TryParseBestGeocodeCandidate(
        string geocodeJson,
        out (string Name, string CountryCode, string RegionCode, double Latitude, double Longitude) candidate) =>
        Utilities.UtilityResponseBuilder.TryParseBestGeocodeCandidate(geocodeJson, out candidate);

    // ── Weather response building — delegates to WeatherResponseBuilder ──

    private string? TryBuildWeatherBriefFromForecastJson(
        string forecastJson, string userMessage, string fallbackLocation) =>
        Utilities.WeatherResponseBuilder.TryBuildBriefFromForecastJson(
            forecastJson, userMessage, fallbackLocation, PreferredUnits);

    private static string BuildWeatherActivityAdvice(
        string location, int? currentTemp, string unitSuffix,
        string? condition, int? avgTemp, string avgSuffix) =>
        Utilities.WeatherResponseBuilder.BuildActivityAdvice(
            location, currentTemp, unitSuffix, condition, avgTemp, avgSuffix);

    private static string BuildWeatherSnapshot(
        string location, int? currentTemp, string unitSuffix,
        string? condition, int? avgTemp, string avgSuffix) =>
        Utilities.WeatherResponseBuilder.BuildSnapshot(
            location, currentTemp, unitSuffix, condition, avgTemp, avgSuffix);

    private static string NormalizeTemperatureUnit(string? rawUnit) =>
        Utilities.WeatherResponseBuilder.NormalizeTemperatureUnit(rawUnit);

    private static int? ConvertTemperature(int? value, string fromUnit, string toUnit) =>
        Utilities.WeatherResponseBuilder.ConvertTemperature(value, fromUnit, toUnit);

    private static bool HasExplicitTemperatureUnitRequest(string userMessage) =>
        Utilities.WeatherResponseBuilder.HasExplicitTemperatureUnitRequest(userMessage);

    private static double? ToFahrenheit(int? temp, string unitSuffix) =>
        Utilities.WeatherResponseBuilder.ToFahrenheit(temp, unitSuffix);

    // ── Time response building — delegates to TimeResponseBuilder ──

    private string? TryBuildTimeBriefFromTimezoneJson(
        string timezoneJson, string fallbackLocation, string userMessage) =>
        Utilities.TimeResponseBuilder.TryBuildBriefFromTimezoneJson(
            timezoneJson, fallbackLocation, userMessage,
            _timeProvider.GetUtcNow().UtcDateTime);

    private void RememberUtilityContext(UtilityRouter.UtilityResult utilityResult)
    {
        if (utilityResult is null || string.IsNullOrWhiteSpace(utilityResult.ContextKey))
            return;

        _lastUtilityContextKey = utilityResult.ContextKey.Trim();
        _lastUtilityContextAt = _timeProvider.GetUtcNow();

        var state = _dialogueStore.Get();
        _dialogueStore.Update(state with
        {
            Topic = utilityResult.ContextKey.Trim()
        });
    }

    private bool TryGetActiveUtilityContext(out string contextKey)
    {
        contextKey = "";
        if (string.IsNullOrWhiteSpace(_lastUtilityContextKey))
            return false;

        var now = _timeProvider.GetUtcNow();
        if ((now - _lastUtilityContextAt) > UtilityContextTtl)
            return false;

        contextKey = _lastUtilityContextKey!;
        return true;
    }

    private UtilityRouter.UtilityResult? TryHandleUtilityFollowUpWithContext(string userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
            return null;

        if (!TryGetActiveUtilityContext(out var contextKey))
            return null;

        var lower = userMessage.Trim().ToLowerInvariant();
        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            TryResolveMoonFollowUpUnit(lower, out var requestedUnit))
        {
            return BuildMoonUnitFollowUpResult(requestedUnit);
        }

        if (contextKey.Equals("moon_distance", StringComparison.OrdinalIgnoreCase) &&
            LooksLikePrecisionFollowUp(lower))
        {
            return BuildMoonPrecisionFollowUpResult(lower);
        }

        return null;
    }

    private static bool LooksLikePrecisionFollowUp(string lowerMessage)
    {
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        return lowerMessage.Contains("more precise", StringComparison.Ordinal) ||
               lowerMessage.Contains("precise figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("more exact", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact figure", StringComparison.Ordinal) ||
               lowerMessage.Contains("exact value", StringComparison.Ordinal) ||
               lowerMessage.Contains("higher precision", StringComparison.Ordinal) ||
               lowerMessage.Contains("more accurate", StringComparison.Ordinal) ||
               lowerMessage.Contains("to the decimal", StringComparison.Ordinal) ||
               lowerMessage.Contains("more digits", StringComparison.Ordinal) ||
               lowerMessage.Contains("significant digit", StringComparison.Ordinal) ||
               lowerMessage.Contains("more detail", StringComparison.Ordinal) ||
               lowerMessage.Equals("i need a more precise figure!", StringComparison.Ordinal) ||
               lowerMessage.Equals("more precise", StringComparison.Ordinal) ||
               lowerMessage.Equals("exactly", StringComparison.Ordinal);
    }

    private static bool TryResolveMoonFollowUpUnit(string lowerMessage, out string unit)
    {
        unit = "";
        if (string.IsNullOrWhiteSpace(lowerMessage))
            return false;

        var tokens = lowerMessage
            .Split(
                [' ', '\t', '\r', '\n', '?', '!', ',', '.', ';', ':', '(', ')', '/', '\\', '-'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static bool ContainsAnyToken(string[] source, params string[] candidates)
        {
            foreach (var token in source)
            {
                for (var i = 0; i < candidates.Length; i++)
                {
                    if (token.Equals(candidates[i], StringComparison.Ordinal))
                        return true;
                }
            }

            return false;
        }

        // Use token matches (not substring matches) so words like
        // "velocity" don't accidentally trip the "it" referential check.
        var referencesPreviousValue = ContainsAnyToken(tokens, "that", "it", "distance", "moon");
        if (!referencesPreviousValue)
            return false;

        if (ContainsAnyToken(tokens, "feet", "foot", "ft"))
        {
            unit = "feet";
            return true;
        }

        if (ContainsAnyToken(tokens, "mile", "miles", "mi"))
        {
            unit = "miles";
            return true;
        }

        if (ContainsAnyToken(tokens, "kilometer", "kilometers", "km"))
        {
            unit = "kilometers";
            return true;
        }

        if (ContainsAnyToken(tokens, "meter", "meters", "m"))
        {
            unit = "meters";
            return true;
        }

        return false;
    }

    private static UtilityRouter.UtilityResult BuildMoonPrecisionFollowUpResult(string lowerMessage)
    {
        const double averageKm = 384_400.0;
        const double perigeeKm = 363_300.0;
        const double apogeeKm = 405_500.0;
        const double kmToMiles = 0.621371;

        var averageMiles = averageKm * kmToMiles;
        var perigeeMiles = perigeeKm * kmToMiles;
        var apogeeMiles = apogeeKm * kmToMiles;

        string answer;
        if (lowerMessage.Contains("mile", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageMiles:N1} miles**. " +
                $"Because the orbit is elliptical, it ranges from about **{perigeeMiles:N0} miles** " +
                $"(perigee) to **{apogeeMiles:N0} miles** (apogee).";
        }
        else if (lowerMessage.Contains("km", StringComparison.Ordinal) ||
                 lowerMessage.Contains("kilometer", StringComparison.Ordinal))
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km**. " +
                $"It ranges from about **{perigeeKm:N0} km** (perigee) to **{apogeeKm:N0} km** (apogee).";
        }
        else
        {
            answer =
                $"More precise numbers: average Earth-Moon distance is **{averageKm:N1} km** " +
                $"(**{averageMiles:N1} miles**). The orbit varies between about **{perigeeKm:N0} km** " +
                $"(**{perigeeMiles:N0} miles**) and **{apogeeKm:N0} km** (**{apogeeMiles:N0} miles**).";
        }

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static UtilityRouter.UtilityResult BuildMoonUnitFollowUpResult(string unit)
    {
        const double averageKm = 384_400.0;
        const double kmToMiles = 0.621371;
        const double milesToFeet = 5_280.0;

        var averageMilesRounded = Math.Round(averageKm * kmToMiles);
        var averageFeetFromMiles = averageMilesRounded * milesToFeet;
        var averageMeters = averageKm * 1_000.0;

        var normalizedUnit = unit.Trim().ToLowerInvariant();
        var answer = normalizedUnit switch
        {
            "feet" =>
                $"That is about **{averageFeetFromMiles:N0} feet** " +
                $"(using **{averageMilesRounded:N0} miles * 5,280 ft/mile**, average Earth-Moon distance).",
            "meters" =>
                $"That is about **{averageMeters:N0} meters** (average Earth-Moon distance).",
            "miles" =>
                $"That is about **{averageMilesRounded:N0} miles** (average Earth-Moon distance).",
            _ =>
                $"That is about **{averageKm:N0} kilometers** (average Earth-Moon distance)."
        };

        return new UtilityRouter.UtilityResult
        {
            Category = "fact",
            Answer = answer,
            ContextKey = "moon_distance"
        };
    }

    private static bool TryResolveTimeZoneInfo(string timezoneId, out TimeZoneInfo tzInfo) =>
        Utilities.TimeResponseBuilder.TryResolveTimeZoneInfo(timezoneId, out tzInfo);

    private static string BuildHolidayUtilityResponse(string toolName, string toolJson) =>
        Utilities.UtilityResponseBuilder.BuildHolidayResponse(toolName, toolJson);

    private static string BuildFeedUtilityResponse(string toolJson) =>
        Utilities.UtilityResponseBuilder.BuildFeedResponse(toolJson);

    private static string BuildStatusUtilityResponse(string toolJson) =>
        Utilities.UtilityResponseBuilder.BuildStatusResponse(toolJson);

    private static string? ExtractLocationFromWeatherMessage(string message) =>
        Utilities.WeatherResponseBuilder.ExtractLocationFromMessage(message);

    private static bool TryBuildExplicitFileReadArgs(
        string message,
        out string argsJson,
        out string? path)
    {
        argsJson = "{}";
        path = null;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        if (!lower.Contains("file_read", StringComparison.Ordinal))
            return false;

        var match = Regex.Match(
            message,
            @"\bfile_read\b.*?\bon\s+(?<path>.+?)(?:\s+and\s+|[?.!]|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
            return true;

        var candidate = match.Groups["path"].Value.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(candidate))
            return true;

        path = candidate;
        argsJson = JsonSerializer.Serialize(new { path = candidate });
        return true;
    }

    private async Task<AgentResponse> ExecuteExplicitFileReadAsync(
        string fileReadArgsJson,
        string? explicitPath,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        var fileReadCall = await CallUtilityToolWithAliasAsync(
            "file_read",
            fileReadArgsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = fileReadCall.ToolName,
            Arguments = fileReadArgsJson,
            Result = fileReadCall.Result,
            Success = fileReadCall.Success
        });

        var target = string.IsNullOrWhiteSpace(explicitPath) ? "that path" : explicitPath;
        var resultText = (fileReadCall.Result ?? "").Trim();

        string responseText;
        if (!fileReadCall.Success || LooksLikeFileReadFailure(resultText))
        {
            responseText =
                $"I attempted `file_read` on `{target}`, but it failed. " +
                "That file/path is likely missing or inaccessible from the current permissions.";
        }
        else
        {
            var summary = resultText.Length > 600
                ? resultText[..600].TrimEnd() + "..."
                : resultText;

            responseText =
                $"I read `{target}`. Summary:\n{summary}";
        }

        _history.Add(ChatMessage.Assistant(responseText));
        LogEvent("AGENT_RESPONSE", responseText);

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private static bool LooksLikeFileReadFailure(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return true;

        if (payload.Contains("error occurred invoking", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("denied", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            return doc.RootElement.TryGetProperty("error", out _);
        }
        catch
        {
            return false;
        }
    }
}
