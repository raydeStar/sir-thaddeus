using System.Text.Json;

namespace SirThaddeus.Harness.Execution;

public static class ToolResultPayloads
{
    public static string BuildSuccess(string rawResult) => rawResult ?? "";

    public static string BuildErrorJson(string code, string message, bool retriable)
    {
        return JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
                retriable
            }
        });
    }

    public static bool LooksLikeStructuredError(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object &&
                   error.TryGetProperty("code", out _) &&
                   error.TryGetProperty("message", out _);
        }
        catch
        {
            return false;
        }
    }
}
