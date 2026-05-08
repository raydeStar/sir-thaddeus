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
    public string Name => "ResponseComposer";

    public Task<StepResult> ExecuteAsync(TurnContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var text = string.IsNullOrWhiteSpace(context.AssistantDraft)
            ? "(The model returned an empty response.)"
            : context.AssistantDraft!;

        if (LooksLikeBareCancelled(text) &&
            SearchOrchestrator.TryBuildMediaInstallmentFallback(context.UserText ?? string.Empty) is { Length: > 0 } mediaFallback)
        {
            text = mediaFallback;
        }

        if (ToolBackedResponseQualityGuards.TryBuildCurrentTimeInLocationFallback(
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
