using System.Collections;
using System.Reflection;
using System.Text.Json;

namespace SirThaddeus.Core.Caching;

/// <summary>
/// Builds deterministic cache keys from a tool name and its arguments
/// by normalizing and JSON-serializing the arguments.
/// </summary>
public static class CacheKeyBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>
    /// Creates a composite key in the form <c>toolname:{json-args}</c>.
    /// Arguments are deeply sorted and normalized for consistency.
    /// </summary>
    public static string Build(string toolName, object? args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var normalized = Normalize(args);
        var payload = JsonSerializer.Serialize(normalized, SerializerOptions);
        return $"{toolName.Trim().ToLowerInvariant()}:{payload}";
    }

    private static object? Normalize(object? value)
    {
        if (value is null)
            return null;

        var type = value.GetType();
        if (type.IsPrimitive || value is string || value is decimal || value is DateTime || value is DateTimeOffset || value is Guid)
            return value;

        if (value is IDictionary dictionary)
        {
            var sorted = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString() ?? string.Empty;
                sorted[key] = Normalize(entry.Value);
            }

            return sorted;
        }

        if (value is IEnumerable enumerable)
        {
            var list = new List<object?>();
            foreach (var item in enumerable)
                list.Add(Normalize(item));
            return list;
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        var obj = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            obj[property.Name] = Normalize(property.GetValue(value));
        }

        return obj;
    }
}
