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
            AppendAssistantMessage(errorText);
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

        // ── Deterministic path: build response directly from capture ─
        if (TryBuildScreenCaptureSummary(screenResult.Result, out var directSummary))
        {
            AppendAssistantMessage(directSummary);
            LogEvent("AGENT_RESPONSE", directSummary);

            return AttachContextSnapshot(new AgentResponse
            {
                Text = directSummary,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips,
                AllowToolResultPersonalityPresentation = true
            }, usageBaseline);
        }

        // ── LLM fallback: let the model describe the capture ────────
        if (!string.IsNullOrWhiteSpace(memoryPackText))
            InjectMemoryIntoHistoryInPlace(_history, memoryPackText);
        InjectPersonalityAnchorIntoHistoryInPlace(_history, personalityAnchor, personalityTurnTag);

        _history.Add(ChatMessage.System(
            "The following is a structured screen read of the user's current screen.\n\n" +
            "Instructions:\n" +
            "1. Describe what you see in plain language — \"You're looking at...\"\n" +
            "2. Respond to the user's likely intent — if they asked for help, help with the content.\n" +
            "3. Acknowledge any limitations honestly if stated in the screen read.\n" +
            "4. NEVER say you can't see the screen — the data below IS from their screen.\n" +
            "5. NEVER output raw control names, framework types, or technical UI tree data.\n\n" +
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

        if ((string.IsNullOrWhiteSpace(screenText) || LooksLikeScreenAccessDisclaimer(screenText)) &&
            TryBuildScreenCaptureSummary(screenResult.Result, out var deterministicSummary))
        {
            screenText = deterministicSummary;
        }

        AppendAssistantMessage(screenText);
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

    private static bool LooksLikeScreenAccessDisclaimer(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("can't read your screen", StringComparison.Ordinal) ||
               lower.Contains("cannot read your screen", StringComparison.Ordinal) ||
               lower.Contains("can't see your screen", StringComparison.Ordinal) ||
               lower.Contains("cannot see your screen", StringComparison.Ordinal) ||
               lower.Contains("don't have access to see what's running", StringComparison.Ordinal) ||
               lower.Contains("do not have access to see what's running", StringComparison.Ordinal) ||
               lower.Contains("cannot directly access your screen", StringComparison.Ordinal) ||
               lower.Contains("not enabled in our local-first session", StringComparison.Ordinal) ||
               lower.Contains("would require a specific tool integration", StringComparison.Ordinal) ||
               lower.Contains("i don't have the ability to view", StringComparison.Ordinal) ||
               lower.Contains("i'm unable to view", StringComparison.Ordinal) ||
               lower.Contains("i cannot view your screen", StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────
    // Screen Read format parsing
    // ─────────────────────────────────────────────────────────────────

    private static bool TryBuildScreenCaptureSummary(string payload, out string summary)
    {
        summary = string.Empty;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        // ── New [Screen Read] format ────────────────────────────────
        if (payload.TrimStart().StartsWith("[Screen Read]", StringComparison.Ordinal))
            return TryBuildFromScreenReadFormat(payload, out summary);

        // ── Legacy: JSON payload ────────────────────────────────────
        if (TryExtractFromJsonScreenPayload(payload, out var jWindow, out var jProcess, out var jContent))
            return BuildLegacySummary(jWindow, jProcess, jContent, out summary);

        // ── Legacy: === Screen Report === format ────────────────────
        var window = ExtractScreenReportField(payload, "Window:");
        var process = ExtractScreenReportField(payload, "Process:");
        var content = ExtractScreenReportSection(payload, "=== Content ===", "NOTE:")
            ?? ExtractScreenReportSection(payload, "=== OCR Text ===", null)
            ?? ExtractAccessibilityText(payload);
        return BuildLegacySummary(window, process, content, out summary);
    }

    private static bool TryBuildFromScreenReadFormat(string payload, out string summary)
    {
        summary = string.Empty;

        var windowContext = ExtractScreenReadField(payload, "Window:");
        var contentType = ExtractScreenReadField(payload, "Content Type:");
        var readableContent = ExtractScreenReadSection(payload, "Content:");
        var limitations = ExtractScreenReadField(payload, "Limitations:");

        // No readable content at all
        if (string.IsNullOrWhiteSpace(readableContent) ||
            readableContent.StartsWith("(no readable text", StringComparison.OrdinalIgnoreCase))
        {
            var parts = new List<string>
            {
                "I captured your current screen, but only limited readable content was available."
            };
            if (!string.IsNullOrWhiteSpace(windowContext))
                parts.Add($"Active window: {windowContext}.");
            if (!string.IsNullOrWhiteSpace(limitations))
                parts.Add(limitations);
            summary = string.Join(" ", parts);
            return !string.IsNullOrWhiteSpace(summary);
        }

        // Build a natural response
        var sb = new StringBuilder();

        // Opening based on content type
        if (!string.IsNullOrWhiteSpace(contentType) && !string.IsNullOrWhiteSpace(windowContext))
        {
            sb.Append(contentType switch
            {
                "WebPage" => $"You're looking at a web page: {windowContext}.",
                "Code"    => $"You're looking at a code editor: {windowContext}.",
                "Document" => $"You're looking at a document: {windowContext}.",
                "Terminal" => $"You're looking at a terminal: {windowContext}.",
                "Math"    => $"You're looking at a calculator: {windowContext}.",
                "Self"    => $"You're looking at Sir Thaddeus's own window.",
                "System"  => $"You're looking at: {windowContext}.",
                _         => $"I captured your current screen. Active window: {windowContext}."
            });
        }
        else
        {
            sb.Append("I captured your current screen.");
            if (!string.IsNullOrWhiteSpace(windowContext))
                sb.Append($" Active window: {windowContext}.");
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append("Visible content: ");
        sb.Append(NormalizeScreenExcerpt(readableContent, 600));

        if (!string.IsNullOrWhiteSpace(limitations))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(limitations);
        }

        summary = sb.ToString().Trim();
        return !string.IsNullOrWhiteSpace(summary);
    }

    private static string? ExtractScreenReadField(string payload, string fieldName)
    {
        foreach (var line in payload.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed[fieldName.Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        return null;
    }

    private static string? ExtractScreenReadSection(string payload, string header)
    {
        var lines = payload.Split('\n');
        var capturing = false;
        var sb = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (capturing)
            {
                // Stop at the next known section header
                if (trimmed.StartsWith("Secondary:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Available Actions:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("Limitations:", StringComparison.OrdinalIgnoreCase))
                    break;

                sb.AppendLine(line);
            }
            else if (trimmed.StartsWith(header, StringComparison.OrdinalIgnoreCase))
            {
                // Check for inline content ("Content: some text here")
                var inlineValue = trimmed[header.Length..].Trim();
                if (!string.IsNullOrWhiteSpace(inlineValue))
                    sb.AppendLine(inlineValue);
                capturing = true;
            }
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    // ─────────────────────────────────────────────────────────────────
    // Legacy format helpers (kept for backward compatibility)
    // ─────────────────────────────────────────────────────────────────

    private static bool BuildLegacySummary(
        string? window, string? process, string? content, out string summary)
    {
        summary = string.Empty;

        var cleanedContent = NormalizeScreenExcerpt(content, 280);
        var hasReadableContent = !string.IsNullOrWhiteSpace(cleanedContent) &&
                                 !cleanedContent.Contains("No readable text detected.", StringComparison.OrdinalIgnoreCase) &&
                                 !cleanedContent.Contains("Browser detected, but the address bar could not be read", StringComparison.OrdinalIgnoreCase);

        var parts = new List<string>
        {
            hasReadableContent
                ? "I captured your current screen."
                : "I captured your current screen, but only limited readable content was available."
        };

        if (!string.IsNullOrWhiteSpace(window) || !string.IsNullOrWhiteSpace(process))
        {
            var windowPart = string.IsNullOrWhiteSpace(window) ? "active window unavailable" : window;
            var processPart = string.IsNullOrWhiteSpace(process) ? null : process;
            parts.Add(processPart is null
                ? $"Active window: {windowPart}."
                : $"Active window: {windowPart} ({processPart}).");
        }

        if (hasReadableContent)
            parts.Add($"Visible content: {cleanedContent}");

        summary = string.Join(" ", parts).Trim();
        return !string.IsNullOrWhiteSpace(summary);
    }

    private static bool TryExtractFromJsonScreenPayload(
        string payload,
        out string? window,
        out string? process,
        out string? content)
    {
        window = null;
        process = null;
        content = null;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var root = doc.RootElement;
            content = GetJsonString(root, "content")
                ?? GetJsonString(root, "ocrText")
                ?? GetJsonString(root, "text")
                ?? GetJsonString(root, "result");

            window = GetJsonString(root, "windowTitle")
                ?? GetJsonString(root, "window")
                ?? GetJsonString(root, "title");

            var processName = GetJsonString(root, "process") ?? GetJsonString(root, "processName");
            var pid = GetJsonString(root, "pid") ?? GetJsonString(root, "processId");
            process = !string.IsNullOrWhiteSpace(processName) && !string.IsNullOrWhiteSpace(pid)
                ? $"{processName} (PID {pid})"
                : processName ?? pid;

            return content is not null || window is not null || process is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? GetJsonString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null
        };
    }

    private static string? ExtractScreenReportField(string report, string fieldName)
    {
        foreach (var line in report.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = trimmed[fieldName.Length..].Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? ExtractScreenReportSection(string report, string header, string? terminatorPrefix)
    {
        var start = report.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var body = report[(start + header.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        if (!string.IsNullOrWhiteSpace(terminatorPrefix))
        {
            var terminator = body.IndexOf(terminatorPrefix, StringComparison.OrdinalIgnoreCase);
            if (terminator >= 0)
                body = body[..terminator].Trim();
        }

        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string? ExtractAccessibilityText(string report)
    {
        const string header = "Source: Accessibility Tree";
        var start = report.IndexOf(header, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        var body = report[(start + header.Length)..].Trim();
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var nextSection = body.IndexOf("=== Browser Page Content ===", StringComparison.OrdinalIgnoreCase);
        if (nextSection >= 0)
            body = body[..nextSection].Trim();

        return string.IsNullOrWhiteSpace(body) ? null : body;
    }

    private static string NormalizeScreenExcerpt(string? text, int maxLength = 280)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var singleLine = Regex.Replace(text, "\\s+", " ").Trim();
        if (singleLine.Length <= maxLength)
            return singleLine;

        return singleLine[..(maxLength - 3)].TrimEnd() + "...";
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

    private sealed record UtilityLocationResolution(
        (string Name, string CountryCode, string? RegionCode, double Latitude, double Longitude)? Location,
        string MismatchWarning,
        AgentResponse? EarlyResponse);

    private AgentResponse CreateUtilityResponse(
        string text,
        bool success,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        bool logResponse = false)
    {
        AppendAssistantMessage(text);

        if (logResponse)
            LogEvent("AGENT_RESPONSE", text);

        return new AgentResponse
        {
            Text = text,
            Success = success,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<UtilityLocationResolution> ResolveLocationBackedUtilityAsync(
        string geocodeArgsJson,
        string geocodeFailureText,
        string noLocationText,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        ValidatedSlots? validatedSlots = null)
    {
        var geocodeCall = await CallToolWithAliasAsync(
            WeatherGeocodeToolName, WeatherGeocodeToolNameAlt,
            geocodeArgsJson, cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = geocodeCall.ToolName,
            Arguments = geocodeArgsJson,
            Result = geocodeCall.Result,
            Success = geocodeCall.Success
        });

        if (!geocodeCall.Success)
        {
            return new UtilityLocationResolution(
                null,
                "",
                CreateUtilityResponse(geocodeFailureText, false, toolCallsMade, roundTrips));
        }

        if (!TryParseBestGeocodeCandidate(geocodeCall.Result, out var geo))
        {
            return new UtilityLocationResolution(
                null,
                "",
                CreateUtilityResponse(noLocationText, true, toolCallsMade, roundTrips));
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

                _dialogueStore.Update(activeState with { GeocodeMismatch = true });

                return new UtilityLocationResolution(
                    null,
                    "",
                    CreateUtilityResponse(confirmText, true, toolCallsMade, roundTrips));
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

        return new UtilityLocationResolution(geo, mismatchWarning, null);
    }

    private async Task<AgentResponse> ExecuteSingleCallUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken,
        string missingArgsError,
        string toolFailureText,
        Func<string, string> buildResponse)
    {
        if (utilityResult.McpToolName is null || utilityResult.McpToolArgs is null)
            return AgentResponse.FromError(missingArgsError);

        var toolCall = await CallUtilityToolWithAliasAsync(
            utilityResult.McpToolName,
            utilityResult.McpToolArgs,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = toolCall.ToolName,
            Arguments = utilityResult.McpToolArgs,
            Result = toolCall.Result,
            Success = toolCall.Success
        });

        if (!toolCall.Success)
            return CreateUtilityResponse(toolFailureText, false, toolCallsMade, roundTrips);

        return CreateUtilityResponse(
            buildResponse(toolCall.Result),
            true,
            toolCallsMade,
            roundTrips,
            logResponse: true);
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

        var locationResolution = await ResolveLocationBackedUtilityAsync(
            utilityResult.McpToolArgs,
            "I couldn't resolve that location for weather lookup. Try a city and region like \"Portland, OR\".",
            "I couldn't find coordinates for that location. Try a more specific place name.",
            toolCallsMade,
            roundTrips,
            cancellationToken,
            validatedSlots);

        if (locationResolution.EarlyResponse is not null)
            return locationResolution.EarlyResponse;

        var geo = locationResolution.Location!.Value;
        var mismatchWarning = locationResolution.MismatchWarning;

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
            return CreateUtilityResponse(errorText, false, toolCallsMade, roundTrips);
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

        return CreateUtilityResponse(weatherBrief, true, toolCallsMade, roundTrips, logResponse: true);
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

        var locationResolution = await ResolveLocationBackedUtilityAsync(
            utilityResult.McpToolArgs,
            "I couldn't resolve that location for a timezone lookup.",
            "I couldn't find coordinates for that location. Try a more specific city/country.",
            toolCallsMade,
            roundTrips,
            cancellationToken,
            validatedSlots);

        if (locationResolution.EarlyResponse is not null)
            return locationResolution.EarlyResponse;

        var geo = locationResolution.Location!.Value;
        var mismatchWarning = locationResolution.MismatchWarning;

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
            return CreateUtilityResponse(errorText, false, toolCallsMade, roundTrips);
        }

        var timeBrief = TryBuildTimeBriefFromTimezoneJson(
            timezoneCall.Result, geo.Name, userMessage);

        if (string.IsNullOrWhiteSpace(timeBrief))
        {
            timeBrief = $"I found the location for **{geo.Name}**, but couldn't build a clean time answer yet.";
        }

        if (!string.IsNullOrWhiteSpace(mismatchWarning))
            timeBrief = $"{mismatchWarning}\n\n{timeBrief}";

        return CreateUtilityResponse(timeBrief, true, toolCallsMade, roundTrips, logResponse: true);
    }

    private Task<AgentResponse> ExecuteHolidayUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
        => ExecuteSingleCallUtilityAsync(
            utilityResult,
            toolCallsMade,
            roundTrips,
            cancellationToken,
            "Holiday utility is missing tool args.",
            "I couldn't fetch holiday data right now. Please try again in a moment.",
            toolResult => BuildHolidayUtilityResponse(utilityResult.McpToolName!, toolResult));

    private Task<AgentResponse> ExecuteFeedUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
        => ExecuteSingleCallUtilityAsync(
            utilityResult,
            toolCallsMade,
            roundTrips,
            cancellationToken,
            "Feed utility is missing tool args.",
            "I couldn't fetch that feed right now.",
            BuildFeedUtilityResponse);

    private Task<AgentResponse> ExecuteStatusUtilityAsync(
        UtilityRouter.UtilityResult utilityResult,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
        => ExecuteSingleCallUtilityAsync(
            utilityResult,
            toolCallsMade,
            roundTrips,
            cancellationToken,
            "Status utility is missing tool args.",
            "I couldn't complete that reachability check right now.",
            BuildStatusUtilityResponse);

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

    private static bool TryBuildExplicitFileListArgs(
        string message,
        out string argsJson,
        out string? path)
    {
        argsJson = "{}";
        path = null;

        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        var looksLikeFolderQuery =
            lower.Contains("file_list", StringComparison.Ordinal) ||
            lower.Contains("my personal folder", StringComparison.Ordinal) ||
            lower.Contains("my folder", StringComparison.Ordinal) ||
            lower.Contains("my files", StringComparison.Ordinal) ||
            lower.Contains("this folder", StringComparison.Ordinal) ||
            lower.Contains("folder", StringComparison.Ordinal) ||
            lower.Contains("directory", StringComparison.Ordinal);

        if (!looksLikeFolderQuery)
            return false;

        path = lower.Contains("my personal folder", StringComparison.Ordinal) ? "my personal folder"
            : lower.Contains("my files", StringComparison.Ordinal) ? "my files"
            : lower.Contains("my folder", StringComparison.Ordinal) ? "my folder"
            : lower.Contains("this folder", StringComparison.Ordinal) ? "this folder"
            : null;

        if (path is null)
        {
            var match = Regex.Match(
                message,
                @"\bfile_list\b.*?\bon\s+(?<path>.+?)(?:\s+and\s+|[?.!]|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success)
            {
                var candidate = match.Groups["path"].Value.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(candidate))
                    path = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(path))
            return false;

        argsJson = JsonSerializer.Serialize(new { path });
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

        AppendAssistantMessage(responseText);
        LogEvent("AGENT_RESPONSE", responseText);

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<AgentResponse> ExecuteExplicitFileListAsync(
        string fileListArgsJson,
        string? explicitPath,
        string userMessage,
        List<ToolCallRecord> toolCallsMade,
        int roundTrips,
        CancellationToken cancellationToken)
    {
        var fileListCall = await CallUtilityToolWithAliasAsync(
            ToolNames.FileList,
            fileListArgsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = fileListCall.ToolName,
            Arguments = fileListArgsJson,
            Result = fileListCall.Result,
            Success = fileListCall.Success
        });

        var target = string.IsNullOrWhiteSpace(explicitPath) ? "that folder" : explicitPath;
        var listingText = (fileListCall.Result ?? string.Empty).Trim();

        if (!fileListCall.Success || LooksLikeFileReadFailure(listingText))
        {
            var failureText =
                $"I attempted to list `{target}`, but it failed. " +
                "That folder is likely missing or inaccessible from the current permissions.";

            AppendAssistantMessage(failureText);
            LogEvent("AGENT_RESPONSE", failureText);
            return new AgentResponse
            {
                Text = failureText,
                Success = true,
                ToolCallsMade = toolCallsMade,
                LlmRoundTrips = roundTrips
            };
        }

        var entries = listingText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fileNames = new List<string>();
        var directoryNames = new List<string>();

        foreach (var entry in entries)
        {
            if (entry.StartsWith("[FILE] ", StringComparison.OrdinalIgnoreCase))
                fileNames.Add(entry[7..].Trim());
            else if (entry.StartsWith("[DIR]  ", StringComparison.OrdinalIgnoreCase))
                directoryNames.Add(entry[7..].Trim());
        }

        var responseParts = new List<string>();
        responseParts.Add($"I listed `{target}`.");

        if (entries.Length == 0)
        {
            responseParts.Add("It appears to be empty.");
        }
        else
        {
            responseParts.Add("Contents:");
            responseParts.Add(string.Join("\n", entries.Take(20)));
        }

        var shouldAutoReadSingleFile =
            directoryNames.Count == 0 &&
            fileNames.Count == 1 &&
            UserAskedToReadFolderContents(userMessage);

        if (shouldAutoReadSingleFile)
        {
            var onlyFile = fileNames[0];
            var readCall = await ReadSingleFolderFileAsync(onlyFile, toolCallsMade, cancellationToken);
            if (!string.IsNullOrWhiteSpace(readCall))
                responseParts.Add(readCall);
        }

        var responseText = string.Join("\n\n", responseParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        AppendAssistantMessage(responseText);
        LogEvent("AGENT_RESPONSE", responseText);

        return new AgentResponse
        {
            Text = responseText,
            Success = true,
            ToolCallsMade = toolCallsMade,
            LlmRoundTrips = roundTrips
        };
    }

    private async Task<string?> ReadSingleFolderFileAsync(
        string fileName,
        List<ToolCallRecord> toolCallsMade,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        var useDocumentRead = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ||
                              extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) ||
                              extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                              extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                              extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase);

        var argsJson = JsonSerializer.Serialize(new { path = fileName });
        var toolCall = await CallUtilityToolWithAliasAsync(
            useDocumentRead ? ToolNames.DocumentRead : ToolNames.FileRead,
            argsJson,
            cancellationToken);

        toolCallsMade.Add(new ToolCallRecord
        {
            ToolName = toolCall.ToolName,
            Arguments = argsJson,
            Result = toolCall.Result,
            Success = toolCall.Success
        });

        var payload = (toolCall.Result ?? string.Empty).Trim();
        if (!toolCall.Success || LooksLikeFileReadFailure(payload))
            return null;

        var excerpt = ExtractReadableFileContent(payload);
        if (string.IsNullOrWhiteSpace(excerpt))
            return null;

        if (excerpt.Length > 600)
            excerpt = excerpt[..600].TrimEnd() + "...";

        return $"I also read `{fileName}` because it is the only file there:\n{excerpt}";
    }

    private static string ExtractReadableFileContent(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("textContent", out var textContent) &&
                textContent.ValueKind == JsonValueKind.String)
            {
                return textContent.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Plain file_read results are not JSON.
        }

        return payload;
    }

    private static bool UserAskedToReadFolderContents(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var lower = message.ToLowerInvariant();
        return lower.Contains("read my", StringComparison.Ordinal) ||
               lower.Contains("read this folder", StringComparison.Ordinal) ||
               lower.Contains("what's in there", StringComparison.Ordinal) ||
               lower.Contains("whats in there", StringComparison.Ordinal) ||
               lower.Contains("tell me what's in there", StringComparison.Ordinal) ||
               lower.Contains("tell me whats in there", StringComparison.Ordinal);
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
