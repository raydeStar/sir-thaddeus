using System.Text.Json;
using SirThaddeus.AuditLog;

internal static partial class RuntimeApiServer
{
    private static string BuildAuditMessage(AuditEvent auditEvent)
    {
        if (string.Equals(auditEvent.Action, "WEB_SEARCH_PROVIDER_TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return BuildWebSearchProviderTraceMessage(auditEvent);
        }

        if (string.Equals(auditEvent.Action, "SEARXNG_AUTOSTART", StringComparison.OrdinalIgnoreCase))
        {
            return BuildSearxngAutostartMessage(auditEvent);
        }

        if (string.Equals(auditEvent.Action, "LOCAL_NEWS_QUERY_RETRY_ABORTED", StringComparison.OrdinalIgnoreCase))
        {
            return BuildLocalNewsRetryAbortedMessage(auditEvent);
        }

        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
        {
            return $"{auditEvent.Action} -> {auditEvent.Target} ({auditEvent.Result})";
        }

        return $"{auditEvent.Action} ({auditEvent.Result})";
    }

    private static string BuildWebSearchProviderTraceMessage(AuditEvent auditEvent)
    {
        var requestedQuery = ReadAuditDetail(auditEvent, "requested_query");
        var effectiveQuery = ReadAuditDetail(auditEvent, "effective_query");
        var provider = ReadAuditDetail(auditEvent, "provider");
        var pathSummary = NormalizeAuditValue(ReadAuditDetail(auditEvent, "path_summary"));
        var sourceCount = ReadAuditDetail(auditEvent, "source_count");
        var failureCode = ReadAuditDetail(auditEvent, "failure_code");
        var failureMessage = NormalizeAuditValue(ReadAuditDetail(auditEvent, "failure_message"));
        var failure = !string.IsNullOrWhiteSpace(failureCode) ? failureCode : failureMessage;

        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(requestedQuery))
        {
            if (!string.IsNullOrWhiteSpace(effectiveQuery) &&
                !string.Equals(requestedQuery, effectiveQuery, StringComparison.Ordinal))
            {
                parts.Add($"query=\"{requestedQuery}\" -> \"{effectiveQuery}\"");
            }
            else
            {
                parts.Add($"query=\"{requestedQuery}\"");
            }
        }

        if (!string.IsNullOrWhiteSpace(provider))
            parts.Add($"provider={provider}");

        if (!string.IsNullOrWhiteSpace(sourceCount))
            parts.Add($"sources={sourceCount}");

        if (!string.IsNullOrWhiteSpace(failure))
            parts.Add($"failure={failure}");

        if (!string.IsNullOrWhiteSpace(pathSummary))
            parts.Add($"path={pathSummary}");

        return parts.Count == 0
            ? "Web search provider trace."
            : "Web search provider trace: " + string.Join(" | ", parts);
    }

    private static string BuildSearxngAutostartMessage(AuditEvent auditEvent)
    {
        var status = ReadAuditDetail(auditEvent, "status");
        var mode = ReadAuditDetail(auditEvent, "mode");
        var message = NormalizeAuditValue(ReadAuditDetail(auditEvent, "message"));
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(mode))
            parts.Add($"mode={mode}");

        if (!string.IsNullOrWhiteSpace(auditEvent.Target))
            parts.Add($"url={auditEvent.Target}");

        if (!string.IsNullOrWhiteSpace(status))
            parts.Add($"status={status}");

        if (!string.IsNullOrWhiteSpace(message))
            parts.Add(message);

        return parts.Count == 0
            ? $"SearxNG autostart ({auditEvent.Result})"
            : "SearxNG autostart: " + string.Join(" | ", parts);
    }

    private static string BuildLocalNewsRetryAbortedMessage(AuditEvent auditEvent)
    {
        var query = ReadAuditDetail(auditEvent, "query");
        var recency = ReadAuditDetail(auditEvent, "recency");
        var budget = ReadAuditDetail(auditEvent, "budget");
        var limit = ReadAuditDetail(auditEvent, "limit");
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(query))
            parts.Add($"query=\"{query}\"");

        if (!string.IsNullOrWhiteSpace(recency))
            parts.Add($"recency={recency}");

        if (!string.IsNullOrWhiteSpace(budget))
            parts.Add($"budget={budget}");

        if (!string.IsNullOrWhiteSpace(limit))
            parts.Add($"limit={limit}");

        return parts.Count == 0
            ? "Local news retry aborted."
            : "Local news retry aborted: " + string.Join(" | ", parts);
    }

    private static string? ReadAuditDetail(AuditEvent auditEvent, string key)
    {
        if (auditEvent.Details is null ||
            !auditEvent.Details.TryGetValue(key, out var value) ||
            value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            JsonElement jsonElement => jsonElement.ValueKind switch
            {
                JsonValueKind.String => jsonElement.GetString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => null,
                JsonValueKind.Undefined => null,
                _ => jsonElement.GetRawText()
            },
            _ => value.ToString()
        };
    }

    private static string? NormalizeAuditValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
