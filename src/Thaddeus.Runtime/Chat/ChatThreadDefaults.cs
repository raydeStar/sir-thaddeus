namespace Thaddeus.Runtime.Chat;

/// <summary>
/// Defaults + naming helpers for chat threads. Kept in one place so the store
/// (which stamps the placeholder) and the API (which replaces it on the first
/// user turn) agree on the sentinel string.
/// </summary>
public static class ChatThreadDefaults
{
    /// <summary>Placeholder title stamped on brand-new threads.</summary>
    public const string UntitledTitle = "New conversation";

    /// <summary>
    /// Derives a concise title from the first user message. Returns an empty
    /// string when the message has no usable content.
    /// </summary>
    /// <remarks>
    /// Heuristic: strip newlines, trim, truncate to ~50 chars at the nearest
    /// word boundary. If we clipped, append an ellipsis.
    /// </remarks>
    public static string DeriveTitleFromFirstMessage(string firstUserText)
    {
        if (string.IsNullOrWhiteSpace(firstUserText)) return string.Empty;

        var clean = firstUserText.ReplaceLineEndings(" ").Trim();
        if (clean.Length == 0) return string.Empty;

        const int maxLen = 50;
        if (clean.Length <= maxLen) return clean;

        var head = clean[..maxLen];
        // Back off to the last space so we never clip mid-word. Only use the
        // backoff when it still leaves a reasonable title (>= 20 chars), else
        // just clip at the limit.
        var lastSpace = head.LastIndexOf(' ');
        if (lastSpace >= 20) head = head[..lastSpace];

        return head.TrimEnd(',', '.', ';', ':', '-', '—', ' ') + "…";
    }
}
