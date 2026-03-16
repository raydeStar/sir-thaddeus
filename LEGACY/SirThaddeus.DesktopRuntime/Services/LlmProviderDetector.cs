using System.Net.Http;
using System.Text.Json;

namespace SirThaddeus.DesktopRuntime.Services;

/// <summary>
/// Probes well-known local LLM endpoints in parallel to detect
/// which providers are running on the user's machine.
/// </summary>
public static class LlmProviderDetector
{
    public sealed record DetectedProvider(
        string Name,
        string BaseUrl,
        string? ModelName,
        bool IsOnline);

    /// <summary>
    /// Well-known local LLM provider endpoints.
    /// All speak the OpenAI-compatible /v1/models API.
    /// </summary>
    private static readonly (string Name, string BaseUrl)[] KnownProviders =
    [
        ("LM Studio", "http://localhost:1234"),
        ("Ollama", "http://localhost:11434"),
        ("text-generation-webui", "http://localhost:5000"),
        ("LocalAI", "http://localhost:8080")
    ];

    /// <summary>
    /// Probes all known endpoints concurrently. Returns results in ~2 seconds.
    /// </summary>
    public static async Task<List<DetectedProvider>> DetectAsync(
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var probeTimeout = timeout ?? TimeSpan.FromSeconds(2);
        var tasks = KnownProviders.Select(p =>
            ProbeEndpointAsync(p.Name, p.BaseUrl, probeTimeout, ct));

        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Probes a single endpoint (known or custom URL).
    /// </summary>
    public static async Task<DetectedProvider> ProbeEndpointAsync(
        string name,
        string baseUrl,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        var probeTimeout = timeout ?? TimeSpan.FromSeconds(2);
        try
        {
            using var http = new HttpClient { Timeout = probeTimeout };
            var cleanUrl = baseUrl.TrimEnd('/');
            var response = await http.GetAsync($"{cleanUrl}/v1/models", ct);

            if (!response.IsSuccessStatusCode)
                return new DetectedProvider(name, baseUrl, null, false);

            var raw = await response.Content.ReadAsStringAsync(ct);
            var modelName = TryParseModelName(raw);

            return new DetectedProvider(name, baseUrl, modelName, true);
        }
        catch
        {
            return new DetectedProvider(name, baseUrl, null, false);
        }
    }

    private static string? TryParseModelName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                return data[0].TryGetProperty("id", out var id)
                    ? id.GetString()
                    : "connected";
            }
            return "connected";
        }
        catch
        {
            return null;
        }
    }
}
