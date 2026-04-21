using Cronos;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Automations;

/// <summary>
/// Pure helpers for normalizing <see cref="AutomationSchedule"/> records
/// and computing their next fire time. Keeps the scheduler + store
/// thin — they only ever ask "when does this next run?" or "have I
/// already fired the one-shot?".
/// </summary>
public static class ScheduleMath
{
    /// <summary>
    /// Returns a schedule that is safe to persist: invalid cron / unknown
    /// timezone / past one-shot times are corrected or cleared. Populates
    /// <see cref="AutomationSchedule.NextRunAt"/> using <paramref name="utcNow"/>.
    /// </summary>
    public static AutomationSchedule Normalize(AutomationSchedule? schedule, DateTimeOffset utcNow)
    {
        if (schedule is null)
        {
            return new AutomationSchedule(
                Kind: "off", Cron: null, RunAt: null, Timezone: null,
                NextRunAt: null, LastFiredAt: null);
        }

        var kind = (schedule.Kind ?? "off").Trim().ToLowerInvariant();
        switch (kind)
        {
            case "off":
                return schedule with { Kind = "off", NextRunAt = null };

            case "cron":
                var cron = (schedule.Cron ?? "").Trim();
                var tz = ResolveTimezone(schedule.Timezone);
                if (!TryParseCron(cron, out var parsed))
                {
                    // Invalid cron → collapse to "off" so the scheduler doesn't trip on it.
                    return schedule with { Kind = "off", Cron = cron, NextRunAt = null };
                }
                var next = parsed!.GetNextOccurrence(utcNow.UtcDateTime, tz);
                return schedule with
                {
                    Kind = "cron",
                    Cron = cron,
                    Timezone = tz.Id,
                    NextRunAt = next.HasValue
                        ? new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc))
                        : null,
                };

            case "one-shot":
                if (schedule.RunAt is null)
                {
                    return schedule with { Kind = "off", NextRunAt = null };
                }
                // Only future one-shots are live. Past times stay on the record
                // so the UI can show "fired at X" without separate bookkeeping.
                var runAt = schedule.RunAt.Value;
                var isFuture = runAt > utcNow;
                return schedule with
                {
                    Kind = "one-shot",
                    NextRunAt = isFuture ? runAt : null,
                };

            default:
                return schedule with { Kind = "off", NextRunAt = null };
        }
    }

    /// <summary>Advances a schedule after it fires. One-shots auto-disable; cron rolls forward.</summary>
    public static AutomationSchedule RecordFired(AutomationSchedule schedule, DateTimeOffset firedAtUtc)
    {
        var kind = (schedule.Kind ?? "off").Trim().ToLowerInvariant();
        if (kind == "cron" && !string.IsNullOrWhiteSpace(schedule.Cron) &&
            TryParseCron(schedule.Cron!, out var parsed))
        {
            var tz = ResolveTimezone(schedule.Timezone);
            // Advance *past* the moment we fired so we don't fire again on
            // the same minute.
            var from = firedAtUtc.UtcDateTime.AddSeconds(1);
            var next = parsed!.GetNextOccurrence(from, tz);
            return schedule with
            {
                LastFiredAt = firedAtUtc,
                NextRunAt = next.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(next.Value, DateTimeKind.Utc))
                    : null,
            };
        }

        if (kind == "one-shot")
        {
            return schedule with
            {
                Kind = "off",
                LastFiredAt = firedAtUtc,
                NextRunAt = null,
            };
        }

        return schedule with { LastFiredAt = firedAtUtc, NextRunAt = null };
    }

    /// <summary>Returns true when the schedule is due relative to <paramref name="utcNow"/>.</summary>
    public static bool IsDue(AutomationSchedule? schedule, DateTimeOffset utcNow)
    {
        if (schedule is null) return false;
        if (schedule.NextRunAt is null) return false;
        return utcNow >= schedule.NextRunAt.Value;
    }

    private static bool TryParseCron(string expression, out CronExpression? parsed)
    {
        try
        {
            parsed = CronExpression.Parse(expression);
            return true;
        }
        catch
        {
            parsed = null;
            return false;
        }
    }

    private static TimeZoneInfo ResolveTimezone(string? tzId)
    {
        if (string.IsNullOrWhiteSpace(tzId)) return TimeZoneInfo.Local;
        try { return TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { return TimeZoneInfo.Local; }
    }
}
