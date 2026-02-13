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

                var targetKind = ResolveTargetKind(upstreamUri);
                if (TryExtractEngineStatus(root, targetKind, out var engineStatus))
                {
                    var detail = BuildEngineDetail(engineStatus);
                    return engineStatus.Ready
                        ? BackendReadiness.Ok(detail, engineStatus)
                        : BackendReadiness.NotReady(detail, engineStatus);
                }

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
                return BackendReadiness.NotReady("health_invalid_json");
            }

            return BackendReadiness.NotReady("health_missing_readiness_fields");
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

    private static string ResolveTargetKind(Uri upstreamUri)
    {
        var path = upstreamUri.AbsolutePath.TrimEnd('/').ToLowerInvariant();
        if (path.EndsWith("/asr", StringComparison.Ordinal))
            return "asr";
        if (path.EndsWith("/stt", StringComparison.Ordinal))
            return "asr";
        if (path.EndsWith("/tts", StringComparison.Ordinal))
            return "tts";
        return "";
    }

    private static bool TryExtractEngineStatus(
        JsonElement root,
        string targetKind,
        out BackendEngineStatus status)
    {
        status = BackendEngineStatus.Unknown(
            engine: string.IsNullOrWhiteSpace(targetKind) ? "unknown" : targetKind);

        if (!string.IsNullOrWhiteSpace(targetKind) &&
            root.TryGetProperty(targetKind, out var scopedElement) &&
            scopedElement.ValueKind == JsonValueKind.Object)
        {
            status = ParseEngineStatus(scopedElement, targetKind);
            return true;
        }

        if (root.TryGetProperty("engine", out var engineElement) &&
            engineElement.ValueKind == JsonValueKind.String)
        {
            status = ParseEngineStatus(root, targetKind);
            return true;
        }

        return false;
    }

    private static BackendEngineStatus ParseEngineStatus(JsonElement element, string fallbackEngine)
    {
        var schemaVersion = ReadInt(element, "schemaVersion", 1);
        var ready = ReadBool(element, "ready");
        var engine = ReadString(element, "engine");
        var engineVersion = ReadString(element, "engineVersion");
        var modelId = ReadString(element, "modelId");
        var instanceId = ReadString(element, "instanceId");
        var timestampUtc = ReadString(element, "timestampUtc");
        var details = ParseDetails(element);

        return new BackendEngineStatus(
            SchemaVersion: schemaVersion,
            Ready: ready,
            Engine: string.IsNullOrWhiteSpace(engine) ? fallbackEngine : engine,
            EngineVersion: engineVersion ?? "",
            ModelId: modelId ?? "",
            InstanceId: instanceId ?? "",
            TimestampUtc: string.IsNullOrWhiteSpace(timestampUtc)
                ? DateTimeOffset.UtcNow.ToString("O")
                : timestampUtc!,
            Details: details);
    }

    private static BackendEngineStatusDetails ParseDetails(JsonElement element)
    {
        if (!element.TryGetProperty("details", out var detailsElement) ||
            detailsElement.ValueKind != JsonValueKind.Object)
        {
            return BackendEngineStatusDetails.Unknown();
        }

        var installed = ReadBool(detailsElement, "installed");
        var missing = ReadStringArray(detailsElement, "missing");
        var lastError = ReadString(detailsElement, "lastError") ?? "";
        return new BackendEngineStatusDetails(installed, missing, lastError);
    }

    private static string BuildEngineDetail(BackendEngineStatus status)
    {
        if (status.Ready)
            return $"ready=true engine={status.Engine}";
        if (!string.IsNullOrWhiteSpace(status.Details.LastError))
            return status.Details.LastError;
        if (status.Details.Missing.Count > 0)
            return $"missing={string.Join(",", status.Details.Missing)}";
        return $"ready=false engine={status.Engine}";
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return false;
        return value.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out var value))
            return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var child in value.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.String)
                continue;
            var text = child.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                items.Add(text);
        }

        return items;
    }
}
