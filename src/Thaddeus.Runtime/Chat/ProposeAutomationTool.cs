using System.Text.Json;
using System.Text.RegularExpressions;
using Thaddeus.Runtime.Automations;
using Thaddeus.SharedTypes;
using SirThaddeus.LlmClient;

namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Virtual runtime-side "tool" the assistant can call to request that the
/// chat UI render an inline confirmation card for a proposed automation.
///
/// Phase C — chat-to-automation. When the user says something like
/// "remind me tomorrow at 9 about the meeting", the model calls this tool
/// with a name, ordered steps, and an optional schedule. The tool:
/// <list type="number">
///   <item>Parses and normalizes the arguments (schedule kind, cron, RunAt, timezone).</item>
///   <item>Publishes a <see cref="ChatTurnEvents.AutomationProposed"/> event
///         with the full payload, so the UI can render an editable card.</item>
///   <item>Returns a short confirmation string back to the model so it
///         stops looping and summarizes for the user.</item>
/// </list>
///
/// The proposal is <b>not</b> persisted here. The UI writes to the
/// automation store only when the user clicks <i>Create</i>.
/// </summary>
public static class ProposeAutomationTool
{
    public const string ToolName = "propose_automation";
    private static readonly Regex TwelveHourTimeRegex = new(
        @"\b(?<hour>1[0-2]|0?[1-9])(?::(?<minute>[0-5]\d))?\s*(?<period>a\.?m\.?|p\.?m\.?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TwentyFourHourTimeRegex = new(
        @"\b(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>OpenAI function-calling definition for the virtual tool.</summary>
    public static ToolDefinition BuildDefinition()
    {
        return new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = ToolName,
                Description =
                    "Opens an editable confirmation card in the chat for a new automation " +
                    "the user asked you to save. Call this whenever the user asks you to " +
                    "remember, remind, schedule, or automate a task — for example " +
                    "'remind me tomorrow at 9 about the meeting' or 'every weekday at " +
                    "8:15 AM check the forecast'. Supply a short name, the ordered " +
                    "steps the assistant should run, and (when the user gave a time) a " +
                    "schedule. Use 'one-shot' only for a single future reminder. Use " +
                    "'cron' for recurring requests like daily / every weekday / weekly / " +
                    "monthly. If the user gave an explicit cadence or time, do not omit " +
                    "the schedule. Example: 'every day at 9 AM' should use kind='cron' " +
                    "with cron='0 9 * * *'. Do not call this for one-off questions you can just " +
                    "answer now.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new
                        {
                            type = "string",
                            description = "Short human-readable title, <= 60 chars.",
                        },
                        description = new
                        {
                            type = "string",
                            description = "Optional one-line description.",
                        },
                        steps = new
                        {
                            type = "array",
                            description =
                                "Ordered prompts the assistant will run when the automation fires. " +
                                "At least one step is required.",
                            items = new { type = "string" },
                        },
                        schedule = new
                        {
                            type = "object",
                            description =
                                "Optional trigger. Omit for a manual run-on-demand automation.",
                            properties = new
                            {
                                kind = new
                                {
                                    type = "string",
                                    description = "'off', 'cron', or 'one-shot'. Use 'cron' for recurring schedules and 'one-shot' only for a single future time.",
                                    @enum = new[] { "off", "cron", "one-shot" },
                                },
                                cron = new
                                {
                                    type = "string",
                                    description =
                                        "5-field cron expression (minute hour day-of-month month day-of-week). " +
                                        "Required when kind='cron'. Example: '0 9 * * 1-5' for 9 AM weekdays.",
                                },
                                runAt = new
                                {
                                    type = "string",
                                    description =
                                        "ISO 8601 date-time when the one-shot should fire. Required when kind='one-shot'.",
                                },
                                timezone = new
                                {
                                    type = "string",
                                    description =
                                        "IANA timezone id (e.g. 'America/Los_Angeles'). Optional; defaults to the user's locale.",
                                },
                            },
                        },
                    },
                    required = new[] { "name", "steps" },
                },
            },
        };
    }

    /// <summary>
    /// Parses the arguments the model supplied, publishes the proposal event,
    /// and returns (summary, error) for the tool-loop. <paramref name="error"/>
    /// is non-null only when validation fails; in that case the tool-loop
    /// feeds the error back to the model so it can try again.
    /// </summary>
    public static async Task<(string Summary, string? Error)> HandleAsync(
        string argumentsJson,
        string threadId,
        string messageId,
        string proposalId,
        ChatTurnPublisher publisher,
        string? userText,
        CancellationToken ct)
    {
        string? name;
        string? description;
        var steps = new List<string>();
        var utcNow = DateTimeOffset.UtcNow;
        AutomationSchedule? schedule = null;

        try
        {
            using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = parsed.RootElement;

            name = ReadTrimmedString(root, "name");
            description = ReadTrimmedString(root, "description");

            if (root.TryGetProperty("steps", out var stepsEl) && stepsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in stepsEl.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String)
                    {
                        var s = el.GetString();
                        if (!string.IsNullOrWhiteSpace(s)) steps.Add(s.Trim());
                    }
                }
            }

            if (root.TryGetProperty("schedule", out var schedEl) && schedEl.ValueKind == JsonValueKind.Object)
            {
                var kind = ReadTrimmedString(schedEl, "kind")?.ToLowerInvariant();
                var cron = ReadTrimmedString(schedEl, "cron");
                var tz = ReadTrimmedString(schedEl, "timezone");
                DateTimeOffset? runAt = null;
                if (schedEl.TryGetProperty("runAt", out var runAtEl) &&
                    runAtEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(runAtEl.GetString(), out var parsedRunAt))
                {
                    runAt = parsedRunAt;
                }

                var raw = new AutomationSchedule(
                    Kind: kind ?? "off",
                    Cron: cron,
                    RunAt: runAt,
                    Timezone: tz,
                    NextRunAt: null,
                    LastFiredAt: null);

                // Reuse the same normalizer the store uses so invalid cron,
                // past one-shot times, etc. are handled consistently.
                schedule = ScheduleMath.Normalize(raw, utcNow);
            }
        }
        catch (JsonException ex)
        {
            return ($"Error: could not parse propose_automation arguments — {ex.Message}", ex.Message);
        }

        if (string.IsNullOrWhiteSpace(name))
            return ("Error: propose_automation requires a non-empty 'name'.", "missing_name");
        if (steps.Count == 0)
            return ("Error: propose_automation requires at least one step.", "missing_steps");

        schedule = CoerceSchedule(schedule, userText, utcNow);

        await publisher.PublishAutomationProposedAsync(
            proposalId,
            threadId,
            messageId,
            name!,
            string.IsNullOrWhiteSpace(description) ? null : description,
            steps,
            schedule,
            ct).ConfigureAwait(false);

        // Short, definite feedback so the small local model doesn't loop.
        return (
            "A confirmation card for this automation is now showing in the chat. " +
            "Tell the user it is ready to review and that they can edit the " +
            "name, steps, or schedule before clicking Create.",
            null);
    }

    private static string? ReadTrimmedString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind != JsonValueKind.String) return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static AutomationSchedule? CoerceSchedule(
        AutomationSchedule? schedule,
        string? userText,
        DateTimeOffset utcNow)
    {
        var inferred = InferScheduleFromUserText(userText, utcNow);
        if (inferred is null)
        {
            return schedule;
        }

        if (schedule is null ||
            string.Equals(schedule.Kind, "off", StringComparison.OrdinalIgnoreCase))
        {
            return inferred;
        }

        if (string.Equals(schedule.Kind, inferred.Kind, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(schedule.Cron, inferred.Cron, StringComparison.Ordinal) &&
            schedule.RunAt == inferred.RunAt)
        {
            return schedule;
        }

        return inferred;
    }

    private static AutomationSchedule? InferScheduleFromUserText(string? userText, DateTimeOffset utcNow)
    {
        if (string.IsNullOrWhiteSpace(userText))
        {
            return null;
        }

        var text = userText.Trim().ToLowerInvariant();
        if (!TryExtractTime(text, out var hour, out var minute))
        {
            return null;
        }

        string? cron = null;
        if (text.Contains("every weekday", StringComparison.Ordinal) ||
            text.Contains("each weekday", StringComparison.Ordinal) ||
            text.Contains("weekdays", StringComparison.Ordinal))
        {
            cron = $"{minute} {hour} * * 1-5";
        }
        else if (TryExtractDayOfWeek(text, out var dayOfWeek))
        {
            cron = $"{minute} {hour} * * {dayOfWeek}";
        }
        else if (text.Contains("every day", StringComparison.Ordinal) ||
                 text.Contains("each day", StringComparison.Ordinal) ||
                 text.Contains("daily", StringComparison.Ordinal))
        {
            cron = $"{minute} {hour} * * *";
        }

        if (cron is null)
        {
            return null;
        }

        return ScheduleMath.Normalize(
            new AutomationSchedule(
                Kind: "cron",
                Cron: cron,
                RunAt: null,
                Timezone: null,
                NextRunAt: null,
                LastFiredAt: null),
            utcNow);
    }

    private static bool TryExtractTime(string text, out int hour, out int minute)
    {
        var twelveHourMatch = TwelveHourTimeRegex.Match(text);
        if (twelveHourMatch.Success)
        {
            hour = int.Parse(twelveHourMatch.Groups["hour"].Value);
            minute = twelveHourMatch.Groups["minute"].Success
                ? int.Parse(twelveHourMatch.Groups["minute"].Value)
                : 0;

            var period = twelveHourMatch.Groups["period"].Value;
            if (period.StartsWith("p", StringComparison.OrdinalIgnoreCase) && hour < 12)
            {
                hour += 12;
            }
            else if (period.StartsWith("a", StringComparison.OrdinalIgnoreCase) && hour == 12)
            {
                hour = 0;
            }

            return true;
        }

        var twentyFourHourMatch = TwentyFourHourTimeRegex.Match(text);
        if (twentyFourHourMatch.Success)
        {
            hour = int.Parse(twentyFourHourMatch.Groups["hour"].Value);
            minute = int.Parse(twentyFourHourMatch.Groups["minute"].Value);
            return true;
        }

        hour = 0;
        minute = 0;
        return false;
    }

    private static bool TryExtractDayOfWeek(string text, out int dayOfWeek)
    {
        if (text.Contains("every monday", StringComparison.Ordinal) || text.Contains("each monday", StringComparison.Ordinal))
        {
            dayOfWeek = 1;
            return true;
        }

        if (text.Contains("every tuesday", StringComparison.Ordinal) || text.Contains("each tuesday", StringComparison.Ordinal))
        {
            dayOfWeek = 2;
            return true;
        }

        if (text.Contains("every wednesday", StringComparison.Ordinal) || text.Contains("each wednesday", StringComparison.Ordinal))
        {
            dayOfWeek = 3;
            return true;
        }

        if (text.Contains("every thursday", StringComparison.Ordinal) || text.Contains("each thursday", StringComparison.Ordinal))
        {
            dayOfWeek = 4;
            return true;
        }

        if (text.Contains("every friday", StringComparison.Ordinal) || text.Contains("each friday", StringComparison.Ordinal))
        {
            dayOfWeek = 5;
            return true;
        }

        if (text.Contains("every saturday", StringComparison.Ordinal) || text.Contains("each saturday", StringComparison.Ordinal))
        {
            dayOfWeek = 6;
            return true;
        }

        if (text.Contains("every sunday", StringComparison.Ordinal) || text.Contains("each sunday", StringComparison.Ordinal))
        {
            dayOfWeek = 0;
            return true;
        }

        dayOfWeek = 0;
        return false;
    }
}
