using SirThaddeus.Agent.Search;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.Agent.Pipeline.Steps;

/// <summary>
/// Terminal pipeline step that converts the accumulated
/// <see cref="TurnContext"/> into an <see cref="AgentResponse"/> for the
/// facade to stream + persist. Always returns
/// <see cref="StepResult.Terminate"/>.
///
/// <para>Reads:</para>
/// <list type="bullet">
///   <item><see cref="TurnContext.AssistantDraft"/> — final text to return.
///         Falls back to a deterministic empty-reply marker if null/blank.</item>
///   <item><see cref="TurnContext.ToolCallsMade"/> — carried through to
///         <see cref="AgentResponse.ToolCallsMade"/>.</item>
/// </list>
///
/// <para>This step exists so post-processing steps can operate on the
/// draft before it becomes a final response, and so the tool-loop step
/// can stay focused on the loop itself rather than response assembly.</para>
/// </summary>
public sealed class ResponseComposerStep : ITurnStep
{
    private const string EmptyResponseMarker = "(The model returned an empty response.)";
    private const string RootCreateToolName = "wiki_root_create";
    private readonly Action<string, string>? _log;

    public ResponseComposerStep(Action<string, string>? log = null)
    {
        _log = log;
    }

    public string Name => "ResponseComposer";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var blankDraft = string.IsNullOrWhiteSpace(context.AssistantDraft);
        var receipt = blankDraft ? TryBuildBlankWikiOutcomeReceipt(context) : null;
        LogBlankWikiOutcomeReceiptActivation(context, receipt is not null);
        var text = receipt ?? (blankDraft ? EmptyResponseMarker : context.AssistantDraft!);

        if (LooksLikeBareCancelled(text) &&
            SearchOrchestrator.TryBuildMediaInstallmentFallback(context.UserText ?? string.Empty) is { Length: > 0 } mediaFallback)
        {
            text = mediaFallback;
        }

        if (ToolBackedResponseQualityGuards.TryBuildReleasedProductExistenceResponse(
                context.UserText ?? string.Empty,
                context.ToolCallsMade) is { Length: > 0 } existenceFallback)
        {
            text = existenceFallback;
        }
        else if (ToolBackedResponseQualityGuards.TryBuildCurrentTimeInLocationFallback(
                context.UserText ?? string.Empty,
                context.ToolCallsMade) is { Length: > 0 } currentTimeFallback)
        {
            text = currentTimeFallback;
        }
        else
        {
            text = AppendTimezoneEvidenceIfMissing(text, context);
        }

        // Extract citation cards from every successful tool result's
        // trailing SOURCES_JSON block (currently emitted by web_search).
        // Merged + de-duped by URL so a follow-up call in the same turn
        // doesn't double-count the same article.
        var sources = SourceCardExtractor.ExtractMerged(
            context.ToolCallsMade
                .Where(call => call.Success)
                .Select(call => call.Result));

        var response = new AgentResponse
        {
            Text = text,
            Success = true,
            ToolCallsMade = context.ToolCallsMade,
            Sources = sources,
        };

        return Task.FromResult<StepResult>(new StepResult.Terminate(response));
    }

    private static string? TryBuildBlankWikiOutcomeReceipt(TurnContext context)
    {
        if (!IsBlankWikiReceiptEnabled())
            return null;

        var rootCalls = context.ToolCallsMade
            .Where(call => string.Equals(
                call.ToolName,
                RootCreateToolName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var successful = rootCalls.LastOrDefault(call => call.Success);
        if (successful is not null && TryReadSuccessfulRootName(successful.Result, out var createdName))
            return $"Created the Wiki root **{createdName}**.";

        if (rootCalls.Length > 0)
        {
            var attemptedName = TryReadStringProperty(rootCalls[^1].Arguments, "name");
            var subject = string.IsNullOrWhiteSpace(attemptedName)
                ? "that Wiki root"
                : $"the Wiki root **{attemptedName}**";
            return $"I couldn't create {subject}, so no changes were made.";
        }

        if (WikiRootCreateSelectionPolicy.IsExplicitNonActionRequest(context.UserText) ||
            WikiRootTemporalDeferralToolPolicy.IsDeferredRootCreateRequest(context.UserText))
        {
            return "No changes were made; I haven't created that Wiki root.";
        }

        return null;
    }

    private static bool TryReadSuccessfulRootName(string? result, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(result))
            return false;

        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty("root", out var rootObject) || rootObject.ValueKind != JsonValueKind.Object ||
                !rootObject.TryGetProperty("name", out var nameElement) || nameElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            name = nameElement.GetString()?.Trim() ?? string.Empty;
            return name.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string TryReadStringProperty(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static bool IsBlankWikiReceiptEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_EXPERIMENT_BLANK_WIKI_RECEIPT");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }

    private void LogBlankWikiOutcomeReceiptActivation(TurnContext context, bool activated)
    {
        if (!IsLatencyTracingEnabled() || _log is null)
            return;

        _log(
            "EXPERIMENT_ACTIVATION",
            $"thread_id={context.ThreadId} turn_id={context.MessageId} " +
            "event=blank_wiki_outcome_receipt " +
            $"decision={(activated ? "activated" : "inactive")}");
    }

    private static bool IsLatencyTracingEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBareCancelled(string text)
    {
        var trimmed = text.Trim().TrimEnd('.', '!', '?');
        return trimmed.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendTimezoneEvidenceIfMissing(string text, TurnContext context)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var userText = context.UserText ?? string.Empty;
        var lowerUser = userText.ToLowerInvariant();
        var lowerText = text.ToLowerInvariant();
        if (!lowerUser.Contains("time", StringComparison.Ordinal) ||
            (!lowerUser.Contains(" now", StringComparison.Ordinal) &&
             !lowerUser.Contains("right now", StringComparison.Ordinal) &&
             !lowerText.Contains("current time", StringComparison.Ordinal)))
        {
            return text;
        }

        if (lowerText.Contains("lookup details:", StringComparison.Ordinal) ||
            lowerText.Contains("time_now=", StringComparison.Ordinal))
        {
            return text;
        }

        var timezoneCall = context.ToolCallsMade.LastOrDefault(call =>
            call.ToolName.Equals("resolve_timezone", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(call.Result));
        if (timezoneCall is null)
            return text;

        var timezoneId = ExtractToolValue(timezoneCall.Result, "timezone");
        if (string.IsNullOrWhiteSpace(timezoneId))
            return text;

        var details = new List<string>();
        var geocodeCall = context.ToolCallsMade.LastOrDefault(call =>
            call.ToolName.Equals("weather_geocode", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(call.Result));
        var geocodeSource = ExtractToolValue(geocodeCall?.Result ?? string.Empty, "source");
        if (!string.IsNullOrWhiteSpace(geocodeSource))
            details.Add($"weather_geocode source={geocodeSource}");

        details.Add($"resolve_timezone timezone={timezoneId}");

        var timezoneSource = ExtractToolValue(timezoneCall.Result, "source");
        if (!string.IsNullOrWhiteSpace(timezoneSource))
            details.Add($"timezone source={timezoneSource}");

        var timeNowCall = context.ToolCallsMade.LastOrDefault(call =>
            call.ToolName.Equals("time_now", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(call.Result));
        var iso = ExtractJsonString(timeNowCall?.Result ?? string.Empty, "iso");
        if (!string.IsNullOrWhiteSpace(iso))
            details.Add($"time_now={iso}");

        return text.TrimEnd() + "\n\nLookup details: " + string.Join("; ", details) + ".";
    }

    private static string ExtractToolValue(string result, string key)
    {
        if (string.IsNullOrWhiteSpace(result) || string.IsNullOrWhiteSpace(key))
            return string.Empty;

        var match = Regex.Match(
            result,
            $@"\b{Regex.Escape(key)}=(?<value>[^,\]\r\n]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string ExtractJsonString(string json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(propertyName))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}
