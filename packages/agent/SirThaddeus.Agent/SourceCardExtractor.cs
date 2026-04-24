using System.Text.Json;

namespace SirThaddeus.Agent;

/// <summary>
/// Parses the <c>&lt;!-- SOURCES_JSON --&gt;</c> block the
/// <c>web_search</c> MCP tool appends to every successful result, turning
/// it into a list of <see cref="AgentSource"/> records for the UI layer.
///
/// <para>The web_search tool emits an optional sources-JSON block AFTER
/// the user-visible prose. The LLM is instructed to ignore it. This
/// extractor runs over the raw tool output so the runtime can hand
/// structured cards to the chat UI — thumbnails, favicons, domain
/// badges — without leaking that metadata back into the model's context.</para>
///
/// <para>Fail-open: malformed JSON, missing fields, and unrecognized
/// payload shapes yield an empty list instead of throwing. Chat messages
/// stay plain text if the block can't be parsed.</para>
/// </summary>
public static class SourceCardExtractor
{
    public const string SourcesDelimiter = "<!-- SOURCES_JSON -->";

    /// <summary>
    /// Extracts source cards from a single tool result. Returns an empty
    /// list when no delimiter is present or parsing fails.
    /// </summary>
    public static IReadOnlyList<AgentSource> Extract(string? toolResult)
    {
        if (string.IsNullOrWhiteSpace(toolResult))
            return Array.Empty<AgentSource>();

        var idx = toolResult.IndexOf(SourcesDelimiter, StringComparison.Ordinal);
        if (idx < 0)
            return Array.Empty<AgentSource>();

        var jsonStart = idx + SourcesDelimiter.Length;
        if (jsonStart >= toolResult.Length)
            return Array.Empty<AgentSource>();

        var jsonSegment = toolResult[jsonStart..].TrimStart();

        try
        {
            using var doc = JsonDocument.Parse(jsonSegment);
            return ExtractFromRoot(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<AgentSource>();
        }
    }

    /// <summary>
    /// Merge-extract across many tool results, de-duplicating by URL.
    /// Each call to a source-producing tool in the same turn contributes,
    /// and later references to the same URL don't overwrite the first
    /// entry's thumbnail / favicon / excerpt.
    /// </summary>
    public static IReadOnlyList<AgentSource> ExtractMerged(IEnumerable<string?> toolResults)
    {
        ArgumentNullException.ThrowIfNull(toolResults);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<AgentSource>();
        foreach (var result in toolResults)
        {
            foreach (var src in Extract(result))
            {
                if (string.IsNullOrWhiteSpace(src.Url)) continue;
                if (seen.Add(src.Url))
                    merged.Add(src);
            }
        }
        return merged;
    }

    private static IReadOnlyList<AgentSource> ExtractFromRoot(JsonElement root)
    {
        // The SOURCES_JSON payload comes in two observed shapes:
        //   (a) Bare array of source objects.
        //   (b) Envelope object with a `sources` array and a sibling
        //       `diagnostics` object (the usual shape since WebSearch
        //       started logging provider health).
        // Handle both so one contract change doesn't silently drop cards.
        JsonElement arrayEl;
        if (root.ValueKind == JsonValueKind.Array)
        {
            arrayEl = root;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("sources", out var inner) &&
                 inner.ValueKind == JsonValueKind.Array)
        {
            arrayEl = inner;
        }
        else
        {
            return Array.Empty<AgentSource>();
        }

        var list = new List<AgentSource>(arrayEl.GetArrayLength());
        foreach (var item in arrayEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var url = TryGetString(item, "url");
            if (string.IsNullOrWhiteSpace(url)) continue;

            list.Add(new AgentSource
            {
                Url = url!,
                Title = NullIfEmpty(TryGetString(item, "title")),
                Domain = NullIfEmpty(TryGetString(item, "domain")),
                Excerpt = NullIfEmpty(TryGetString(item, "excerpt")),
                Favicon = NullIfEmpty(TryGetString(item, "favicon")),
                Thumbnail = NullIfEmpty(TryGetString(item, "thumbnail")),
                PublishedAt = NullIfEmpty(TryGetString(item, "publishedAt")),
            });
        }
        return list;
    }

    private static string? TryGetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
