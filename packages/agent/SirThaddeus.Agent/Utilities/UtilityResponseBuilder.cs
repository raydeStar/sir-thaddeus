using System.Text.Json;

namespace SirThaddeus.Agent.Utilities;

/// <summary>
/// Pure-function builders for deterministic utility response text:
/// geocode parsing, holiday formatting, feed formatting, and status
/// check formatting. Extracted from AgentOrchestrator.Internal.
/// </summary>
public static class UtilityResponseBuilder
{
    // ── Geocode ──────────────────────────────────────────────────────

    public static bool TryParseBestGeocodeCandidate(
        string geocodeJson,
        out (string Name, string CountryCode, string RegionCode, double Latitude, double Longitude) candidate)
    {
        candidate = default;
        if (string.IsNullOrWhiteSpace(geocodeJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(geocodeJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return false;

            JsonElement? best = null;
            double bestConfidence = double.NegativeInfinity;
            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("latitude", out var latProbeEl) || !latProbeEl.TryGetDouble(out _))
                    continue;
                if (!item.TryGetProperty("longitude", out var lonProbeEl) || !lonProbeEl.TryGetDouble(out _))
                    continue;

                var confidence = item.TryGetProperty("confidence", out var confEl) &&
                                 confEl.ValueKind == JsonValueKind.Number &&
                                 confEl.TryGetDouble(out var conf)
                    ? conf
                    : 0.0;

                if (best is null || confidence > bestConfidence)
                {
                    best = item;
                    bestConfidence = confidence;
                }
            }

            if (best is null)
                return false;

            var r = best.Value;
            if (!r.TryGetProperty("latitude", out var latEl) || !latEl.TryGetDouble(out var lat))
                return false;
            if (!r.TryGetProperty("longitude", out var lonEl) || !lonEl.TryGetDouble(out var lon))
                return false;

            var name = r.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "") : "";
            var countryCode = r.TryGetProperty("countryCode", out var ccEl) ? (ccEl.GetString() ?? "") : "";
            var regionCode =
                r.TryGetProperty("regionCode", out var rcEl) ? (rcEl.GetString() ?? "") :
                (r.TryGetProperty("region", out var regionEl) ? (regionEl.GetString() ?? "") : "");

            candidate = (name, countryCode, regionCode, lat, lon);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Holiday ──────────────────────────────────────────────────────

    public static string BuildHolidayResponse(string toolName, string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't get holiday data from that tool call.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Holiday lookup failed: {err.GetString()}";
            }

            var country = root.TryGetProperty("countryCode", out var ccEl)
                ? (ccEl.GetString() ?? "that country")
                : "that country";
            var region = root.TryGetProperty("regionCode", out var rcEl)
                ? (rcEl.GetString() ?? "")
                : "";
            var scope = string.IsNullOrWhiteSpace(region) ? country : region;

            if (toolName.Equals(ToolNames.HolidaysIsToday, StringComparison.OrdinalIgnoreCase))
            {
                return BuildIsTodayResponse(root, scope);
            }

            if (toolName.Equals(ToolNames.HolidaysNext, StringComparison.OrdinalIgnoreCase))
            {
                return BuildNextHolidayResponse(root, scope);
            }

            // holidays_get
            return BuildHolidayListResponse(root, scope);
        }
        catch
        {
            return "I fetched holiday data, but couldn't parse a clean answer.";
        }
    }

    private static string BuildIsTodayResponse(JsonElement root, string scope)
    {
        var isTodayHoliday = root.TryGetProperty("isPublicHoliday", out var isEl) &&
                             isEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                             isEl.GetBoolean();

        var todayNames = new List<string>();
        if (root.TryGetProperty("holidaysToday", out var todayArr) &&
            todayArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in todayArr.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var nameEl) &&
                    nameEl.ValueKind == JsonValueKind.String)
                {
                    var name = (nameEl.GetString() ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name))
                        todayNames.Add(name);
                }
            }
        }

        string firstLine;
        if (isTodayHoliday)
        {
            var names = todayNames.Count > 0
                ? string.Join(", ", todayNames.Distinct(StringComparer.OrdinalIgnoreCase))
                : "a listed public holiday";
            firstLine = $"Yes — today is a public holiday in **{scope}**: **{names}**.";
        }
        else
        {
            firstLine = $"No — today is not a public holiday in **{scope}**.";
        }

        if (root.TryGetProperty("nextHoliday", out var nextHoliday) &&
            nextHoliday.ValueKind == JsonValueKind.Object)
        {
            var nextName = nextHoliday.TryGetProperty("name", out var nn) ? (nn.GetString() ?? "") : "";
            var nextDate = nextHoliday.TryGetProperty("date", out var nd) ? (nd.GetString() ?? "") : "";
            if (!string.IsNullOrWhiteSpace(nextName) && !string.IsNullOrWhiteSpace(nextDate))
            {
                firstLine += $" Next up: **{nextName}** on **{nextDate}**.";
            }
        }

        return $"{firstLine}\n\nWant the full holiday calendar for the year?";
    }

    private static string BuildNextHolidayResponse(JsonElement root, string scope)
    {
        if (root.TryGetProperty("holidays", out var holidays) &&
            holidays.ValueKind == JsonValueKind.Array &&
            holidays.GetArrayLength() > 0)
        {
            var first = holidays.EnumerateArray().First();
            var name = first.TryGetProperty("name", out var nameEl) ? (nameEl.GetString() ?? "the next holiday") : "the next holiday";
            var date = first.TryGetProperty("date", out var dateEl) ? (dateEl.GetString() ?? "an upcoming date") : "an upcoming date";
            return $"The next public holiday in **{scope}** is **{name}** on **{date}**.\n\nWant the next few after that?";
        }

        return $"I couldn't find upcoming public holidays for **{scope}**.";
    }

    private static string BuildHolidayListResponse(JsonElement root, string scope)
    {
        var year = root.TryGetProperty("year", out var yEl) && yEl.TryGetInt32(out var y)
            ? y
            : DateTime.UtcNow.Year;
        var entries = new List<string>();
        var count = 0;
        if (root.TryGetProperty("holidays", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            count = arr.GetArrayLength();
            foreach (var item in arr.EnumerateArray().Take(4))
            {
                var name = item.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                var date = item.TryGetProperty("date", out var dEl) ? (dEl.GetString() ?? "") : "";
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(date))
                    entries.Add($"{name} ({date})");
            }
        }

        if (count == 0)
            return $"I couldn't find public holidays for **{scope}** in **{year}**.";

        var preview = entries.Count > 0 ? string.Join(", ", entries) : "no preview available";
        return $"I found **{count}** public holidays in **{scope}** for **{year}**. First entries: {preview}.\n\nWant this narrowed to a specific region?";
    }

    // ── Feed ─────────────────────────────────────────────────────────

    public static string BuildFeedResponse(string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't read any feed data from that request.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Feed fetch failed: {err.GetString()}";
            }

            var title = root.TryGetProperty("feedTitle", out var titleEl) ? (titleEl.GetString() ?? "") : "";
            var host = root.TryGetProperty("sourceHost", out var hostEl) ? (hostEl.GetString() ?? "") : "";
            var label = !string.IsNullOrWhiteSpace(title) ? title : host;

            var items = new List<string>();
            var count = 0;
            if (root.TryGetProperty("items", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                count = arr.GetArrayLength();
                foreach (var item in arr.EnumerateArray().Take(3))
                {
                    if (item.TryGetProperty("title", out var itemTitleEl) &&
                        itemTitleEl.ValueKind == JsonValueKind.String)
                    {
                        var itemTitle = (itemTitleEl.GetString() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(itemTitle))
                            items.Add(itemTitle);
                    }
                }
            }

            if (count == 0)
            {
                return $"I reached **{label}**, but there were no recent feed items to show.\n\nWant a retry or a different feed URL?";
            }

            var headlineList = items.Count > 0
                ? string.Join("; ", items.Select((t, i) => $"{i + 1}) {t}"))
                : "recent items were returned";

            return $"I fetched **{count}** recent feed item(s) from **{label}**. Latest: {headlineList}\n\nPick one and I'll summarize it.";
        }
        catch
        {
            return "I fetched feed data, but couldn't parse it into a clean summary.";
        }
    }

    // ── Status Check ─────────────────────────────────────────────────

    public static string BuildStatusResponse(string toolJson)
    {
        if (string.IsNullOrWhiteSpace(toolJson))
            return "I couldn't get a status payload from that check.";

        try
        {
            using var doc = JsonDocument.Parse(toolJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(err.GetString()))
            {
                return $"Status check failed: {err.GetString()}";
            }

            var url = root.TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
            var reachable = root.TryGetProperty("reachable", out var reachEl) &&
                            reachEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                            reachEl.GetBoolean();
            var code = root.TryGetProperty("httpStatus", out var codeEl) && codeEl.TryGetInt32(out var status)
                ? status
                : (int?)null;
            var method = root.TryGetProperty("method", out var methodEl) ? (methodEl.GetString() ?? "probe") : "probe";
            var latency = root.TryGetProperty("latencyMs", out var latencyEl) && latencyEl.TryGetInt32(out var ms)
                ? ms
                : 0;
            var error = root.TryGetProperty("error", out var errEl) ? (errEl.GetString() ?? "") : "";

            var hostLabel = url;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                hostLabel = uri.Host;

            if (reachable)
            {
                var statusText = code.HasValue ? $"HTTP {code.Value}" : "a network response";
                return $"**{hostLabel}** is reachable ({statusText} via {method} in {latency} ms).\n\nNeed a quick re-check in a few seconds?";
            }

            var reason = string.IsNullOrWhiteSpace(error) ? "no response" : error;
            return $"I couldn't reach **{hostLabel}** ({reason}).\n\nWant a retry or a different URL variant?";
        }
        catch
        {
            return "I ran the status check, but couldn't parse the response cleanly.";
        }
    }
}
