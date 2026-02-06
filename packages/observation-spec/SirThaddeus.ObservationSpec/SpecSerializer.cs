using System.Text.Json;
using System.Text.Json.Serialization;

namespace SirThaddeus.ObservationSpec;

/// <summary>
/// JSON serialization helpers for Observation Specifications.
/// Uses snake_case naming to match the spec format.
/// </summary>
public static class SpecSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
        }
    };

    /// <summary>
    /// Serializes an observation spec to JSON.
    /// </summary>
    public static string Serialize(ObservationSpecDocument spec)
    {
        return JsonSerializer.Serialize(spec, SerializerOptions);
    }

    /// <summary>
    /// Deserializes an observation spec from JSON.
    /// </summary>
    public static ObservationSpecDocument? Deserialize(string json)
    {
        return JsonSerializer.Deserialize<ObservationSpecDocument>(json, DeserializerOptions);
    }

    /// <summary>
    /// Tries to deserialize an observation spec from JSON.
    /// </summary>
    public static bool TryDeserialize(string json, out ObservationSpecDocument? spec, out string? error)
    {
        try
        {
            spec = JsonSerializer.Deserialize<ObservationSpecDocument>(json, DeserializerOptions);
            error = null;
            return spec != null;
        }
        catch (JsonException ex)
        {
            spec = null;
            error = $"JSON parse error: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Creates a template JSON string for a new observation spec.
    /// </summary>
    public static string CreateTemplateJson()
    {
        return Serialize(ObservationSpecDocument.CreateTemplate());
    }
}
