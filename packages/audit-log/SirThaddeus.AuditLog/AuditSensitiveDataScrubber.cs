using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SirThaddeus.AuditLog;

/// <summary>
/// Centralized scrubber for persisted audit events.
/// Ensures common secret-bearing keys and token-like values are redacted
/// before any event is written to disk.
/// </summary>
public static class AuditSensitiveDataScrubber
{
    private const string RedactedValue = "[REDACTED]";
    private const string RedactedJwtValue = "[REDACTED_JWT]";
    private const string RedactedSecretValue = "[REDACTED_SECRET]";

    // Heuristic knobs for long random secret detection.
    internal const int LongRandomMinLength = 40;
    internal const double LongRandomMinEntropy = 3.5d;

    private static readonly HashSet<string> SensitiveKeyNormalized = new(StringComparer.OrdinalIgnoreCase)
    {
        "token",
        "apikey",
        "authorization",
        "cookie",
        "password",
        "secret",
        "connectionstring",
        "connstring",
        "bearer"
    };

    // Typical JWT structure: base64url.base64url.base64url
    private static readonly Regex JwtValueRegex = new(
        @"\b[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\b",
        RegexOptions.Compiled);

    private static readonly Regex BearerTokenRegex = new(
        @"\bBearer\s+[A-Za-z0-9._~+/-]{8,}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LongTokenCandidateRegex = new(
        @"\b[A-Za-z0-9_+=/-]{40,}\b",
        RegexOptions.Compiled);

    public static AuditEvent Scrub(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        return auditEvent with
        {
            Result = ScrubString(auditEvent.Result, keyHint: null),
            Details = ScrubDetails(auditEvent.Details)
        };
    }

    /// <summary>
    /// Scrubs a standalone text payload using the same key/value heuristics
    /// used for persisted audit events.
    /// </summary>
    public static string ScrubText(string text)
        => ScrubString(text ?? "", keyHint: null);

    private static Dictionary<string, object>? ScrubDetails(Dictionary<string, object>? details)
    {
        if (details is null)
            return null;

        var scrubbed = new Dictionary<string, object>(details.Count);
        foreach (var (key, value) in details)
        {
            scrubbed[key] = IsSensitiveKey(key)
                ? RedactedValue
                : ScrubValue(value, keyHint: key) ?? "";
        }

        return scrubbed;
    }

    private static object? ScrubValue(object? value, string? keyHint)
    {
        if (IsSensitiveKey(keyHint))
            return RedactedValue;

        if (value is null)
            return null;

        return value switch
        {
            string s => ScrubString(s, keyHint),
            JsonElement elem => ScrubJsonElement(elem, keyHint),
            IDictionary<string, object> dict => ScrubDictionary(dict),
            IDictionary dictionary => ScrubUntypedDictionary(dictionary),
            IEnumerable sequence when value is not string => ScrubEnumerable(sequence),
            _ => value
        };
    }

    private static Dictionary<string, object> ScrubDictionary(IDictionary<string, object> dictionary)
    {
        var scrubbed = new Dictionary<string, object>(dictionary.Count);
        foreach (var (key, value) in dictionary)
        {
            scrubbed[key] = IsSensitiveKey(key)
                ? RedactedValue
                : ScrubValue(value, keyHint: key) ?? "";
        }

        return scrubbed;
    }

    private static Dictionary<string, object> ScrubUntypedDictionary(IDictionary dictionary)
    {
        var scrubbed = new Dictionary<string, object>(dictionary.Count);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key?.ToString() ?? "";
            scrubbed[key] = IsSensitiveKey(key)
                ? RedactedValue
                : ScrubValue(entry.Value, keyHint: key) ?? "";
        }

        return scrubbed;
    }

    private static List<object> ScrubEnumerable(IEnumerable sequence)
    {
        var scrubbed = new List<object>();
        foreach (var item in sequence)
            scrubbed.Add(ScrubValue(item, keyHint: null) ?? "");

        return scrubbed;
    }

    private static object ScrubJsonElement(JsonElement element, string? keyHint)
    {
        if (IsSensitiveKey(keyHint))
            return RedactedValue;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var scrubbed = new Dictionary<string, object>();
                foreach (var property in element.EnumerateObject())
                {
                    scrubbed[property.Name] = IsSensitiveKey(property.Name)
                        ? RedactedValue
                        : ScrubJsonElement(property.Value, property.Name);
                }

                return scrubbed;
            }
            case JsonValueKind.Array:
            {
                var scrubbed = new List<object>();
                foreach (var item in element.EnumerateArray())
                    scrubbed.Add(ScrubJsonElement(item, keyHint: null));
                return scrubbed;
            }
            case JsonValueKind.String:
                return ScrubString(element.GetString() ?? "", keyHint);
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var int64))
                    return int64;
                if (element.TryGetDouble(out var dbl))
                    return dbl;
                return element.GetRawText();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return "";
        }
    }

    private static string ScrubString(string value, string? keyHint)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        if (IsSensitiveKey(keyHint))
            return RedactedValue;

        // First pass: if the string itself is JSON, recurse over keys.
        if (TryScrubEmbeddedJson(value, out var scrubbedJson))
            return scrubbedJson;

        var scrubbed = BearerTokenRegex.Replace(value, "Bearer " + RedactedSecretValue);
        scrubbed = JwtValueRegex.Replace(scrubbed, RedactedJwtValue);
        scrubbed = LongTokenCandidateRegex.Replace(scrubbed, match =>
            LooksLikeLongRandomSecret(match.Value) ? RedactedSecretValue : match.Value);

        return scrubbed;
    }

    private static bool TryScrubEmbeddedJson(string value, out string scrubbedJson)
    {
        scrubbedJson = value;
        var trimmed = value.Trim();
        if (!LooksLikeJson(trimmed))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var scrubbedObject = ScrubJsonElement(doc.RootElement, keyHint: null);
            scrubbedJson = JsonSerializer.Serialize(scrubbedObject);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeJson(string value) =>
        (value.StartsWith('{') && value.EndsWith('}')) ||
        (value.StartsWith('[') && value.EndsWith(']'));

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var normalized = NormalizeKey(key);
        return SensitiveKeyNormalized.Contains(normalized);
    }

    private static string NormalizeKey(string key)
    {
        var chars = key.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static bool LooksLikeLongRandomSecret(string value)
    {
        if (value.Length < LongRandomMinLength)
            return false;

        if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var hasLetter = value.Any(char.IsLetter);
        var hasDigit = value.Any(char.IsDigit);
        if (!hasLetter || !hasDigit)
            return false;

        var uniqueRatio = value.Distinct().Count() / (double)value.Length;
        if (uniqueRatio < 0.25d)
            return false;

        return ComputeShannonEntropy(value) >= LongRandomMinEntropy;
    }

    private static double ComputeShannonEntropy(string value)
    {
        var frequency = new Dictionary<char, int>();
        foreach (var c in value)
        {
            if (frequency.TryGetValue(c, out var count))
                frequency[c] = count + 1;
            else
                frequency[c] = 1;
        }

        var len = (double)value.Length;
        var entropy = 0d;
        foreach (var count in frequency.Values)
        {
            var p = count / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
