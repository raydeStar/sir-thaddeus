using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SirThaddeus.PersonalityEngine.Profiles;

/// <summary>
/// Produces canonical JSON and SHA256 hashes for reproducible profile identity.
/// </summary>
public static class CanonicalJsonHasher
{
    public static string Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = false
            }))
        {
            WriteCanonical(root, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string ComputeHash(JsonElement root)
    {
        var canonical = Canonicalize(root);
        return ComputeHash(canonical);
    }

    public static string ComputeHash(PersonalityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var element = JsonSerializer.SerializeToElement(profile);
        return ComputeHash(element);
    }

    public static string ComputeHash(string canonicalJson)
    {
        var bytes = Encoding.UTF8.GetBytes(canonicalJson ?? "");
        var digest = SHA256.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(prop.Name);
                        WriteCanonical(prop.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                }

            case JsonValueKind.Array:
                {
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteCanonical(item, writer);
                    writer.WriteEndArray();
                    break;
                }

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(element.GetRawText()), skipInputValidation: true);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;

            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static string NormalizeNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
            return i64.ToString(CultureInfo.InvariantCulture);

        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec))
            return dec.ToString("G29", CultureInfo.InvariantCulture);

        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var dbl))
            return dbl.ToString("G17", CultureInfo.InvariantCulture);

        return raw;
    }
}
