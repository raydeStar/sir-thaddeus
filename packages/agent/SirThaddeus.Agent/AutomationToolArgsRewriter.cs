using System.Text;
using System.Text.Json;

namespace SirThaddeus.Agent;

/// <summary>
/// Small helpers that adjust tool-call arguments on the way to the MCP tool
/// when the caller is a scripted automation run. Split out from the runtime
/// chat pipeline so the pure logic is unit-testable.
/// </summary>
public static class AutomationToolArgsRewriter
{
    /// <summary>
    /// If an automation's <c>web_search</c> call omits recency (or sets it to
    /// <c>"any"</c>), rewrite the arguments JSON to pin <c>recency="week"</c>.
    /// Unattended runs for "check the price of X", "has Y been released?", or
    /// "news about Z" almost always want fresh results, and small models
    /// rarely remember to set recency themselves. If the model explicitly set
    /// a non-<c>"any"</c> recency, we respect that. Malformed JSON is returned
    /// unchanged so the downstream tool surfaces a clearer error.
    /// </summary>
    public static string ApplySearchRecencyDefault(string? argsJson)
    {
        var json = string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson!;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return json;

            if (root.TryGetProperty("recency", out var recencyEl) &&
                recencyEl.ValueKind == JsonValueKind.String)
            {
                var existing = recencyEl.GetString();
                if (!string.IsNullOrWhiteSpace(existing) &&
                    !string.Equals(existing, "any", StringComparison.OrdinalIgnoreCase))
                {
                    return json;
                }
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var wroteRecency = false;
                foreach (var prop in root.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "recency", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteString("recency", "week");
                        wroteRecency = true;
                    }
                    else
                    {
                        prop.WriteTo(writer);
                    }
                }
                if (!wroteRecency) writer.WriteString("recency", "week");
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            return json;
        }
    }
}
