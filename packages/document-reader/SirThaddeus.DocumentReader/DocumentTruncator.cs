namespace SirThaddeus.DocumentReader;

/// <summary>
/// Truncates extracted document text to a configurable character budget
/// and appends a notice when content is clipped.
/// </summary>
public static class DocumentTruncator
{
    /// <summary>
    /// Returns <paramref name="textContent"/> as-is if within budget,
    /// otherwise truncates and appends a "[...truncated]" notice.
    /// </summary>
    /// <param name="textContent">The full extracted text to truncate.</param>
    /// <param name="maxChars">Maximum characters to keep before truncation.</param>
    /// <returns>The original or truncated text with a notice appended when clipped.</returns>
    public static string TruncateWithNotice(string textContent, int maxChars)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), "maxChars must be greater than zero.");
        }

        if (textContent.Length <= maxChars)
        {
            return textContent;
        }

        return textContent[..maxChars] +
               $"\n[...truncated — {textContent.Length} chars total, showing first {maxChars}]";
    }
}
