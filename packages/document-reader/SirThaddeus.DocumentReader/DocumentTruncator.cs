namespace SirThaddeus.DocumentReader;

public static class DocumentTruncator
{
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
