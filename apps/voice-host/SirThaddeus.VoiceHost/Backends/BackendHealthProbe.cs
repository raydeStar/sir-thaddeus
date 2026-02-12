using System.Text.Json;

namespace SirThaddeus.VoiceHost.Backends;

internal static class BackendHealthProbe
{
    public static async Task<BackendReadiness> ProbeAsync(
        HttpClient httpClient,
        Uri upstreamUri,
        CancellationToken cancellationToken)
    {
        try
        {
            var healthUri = DeriveHealthUri(upstreamUri);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1200));

            using var response = await httpClient.GetAsync(healthUri, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return BackendReadiness.NotReady($"health_status={(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (string.IsNullOrWhiteSpace(body))
                return BackendReadiness.NotReady("health_empty");

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("ready", out var readyElement) &&
                    readyElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    var ready = readyElement.GetBoolean();
                    return ready
                        ? BackendReadiness.Ok("ready=true")
                        : BackendReadiness.NotReady("ready=false");
                }

                if (root.TryGetProperty("status", out var statusElement) &&
                    statusElement.ValueKind == JsonValueKind.String)
                {
                    var status = statusElement.GetString() ?? "";
                    return string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase)
                        ? BackendReadiness.Ok("status=ok")
                        : BackendReadiness.NotReady($"status={status}");
                }
            }
            catch
            {
                // Fall through to permissive success on HTTP 200 if no JSON schema is available.
            }

            return BackendReadiness.Ok("http200");
        }
        catch (Exception ex)
        {
            return BackendReadiness.NotReady(ex.Message);
        }
    }

    private static Uri DeriveHealthUri(Uri upstreamUri)
    {
        var path = upstreamUri.AbsolutePath;
        var healthPath = path.EndsWith("/health", StringComparison.OrdinalIgnoreCase)
            ? path
            : "/health";

        return new UriBuilder(upstreamUri)
        {
            Path = healthPath,
            Query = ""
        }.Uri;
    }
}
