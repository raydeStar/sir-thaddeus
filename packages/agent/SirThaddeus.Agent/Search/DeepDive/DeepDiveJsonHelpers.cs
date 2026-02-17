using System.Text.Json;

namespace SirThaddeus.Agent.Search.DeepDive;

/// <summary>
/// Shared JSON element accessors for deep-dive payload parsing.
/// Pulled out of the coordinator to keep it thin and focused on orchestration.
/// </summary>
internal static class DeepDiveJsonHelpers
{
    public static string GetString(JsonElement element, string property, string fallback)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return fallback;

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text!;
    }

    public static bool? GetBoolean(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.True  => true,
            JsonValueKind.False => false,
            _                   => null
        };
    }

    public static double? GetNullableDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.TryGetDouble(out var number) ? number : null;
    }

    public static int? GetNullableInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.TryGetInt32(out var number) ? number : null;
    }

    public static IReadOnlyList<string> GetStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];

        var values = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(item.GetString()))
            {
                values.Add(item.GetString()!);
            }
        }

        return values;
    }

    public static List<string> GetReviews(JsonElement place)
    {
        var list = new List<string>();
        if (!place.TryGetProperty("reviews", out var reviews) ||
            reviews.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var review in reviews.EnumerateArray())
        {
            var text = GetString(review, "text", "");
            if (!string.IsNullOrWhiteSpace(text))
                list.Add(text.Trim());
        }

        return list;
    }

    public static bool TryGetMap(JsonElement place, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (!place.TryGetProperty("geometry", out var geometry) ||
            geometry.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!geometry.TryGetProperty("lat", out var latEl) ||
            !geometry.TryGetProperty("lng", out var lngEl))
        {
            return false;
        }

        if (!latEl.TryGetDouble(out latitude) || !lngEl.TryGetDouble(out longitude))
            return false;

        return true;
    }

    public static bool ContainsAny(string text, params string[] signals)
    {
        foreach (var signal in signals)
        {
            if (text.Contains(signal, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
