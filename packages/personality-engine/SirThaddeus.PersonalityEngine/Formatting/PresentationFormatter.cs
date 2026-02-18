using System.Text;

namespace SirThaddeus.PersonalityEngine.Formatting;

public sealed record PresentationFormatOptions
{
    public bool IncludeSignatureNote { get; init; }
    public string SignatureText { get; init; } = "";
}

/// <summary>
/// Semantics-safe formatting: no sentence deletion, no fact mutation.
/// </summary>
public static class PresentationFormatter
{
    public static string Apply(string text, PresentationFormatOptions options)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? "";

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');

        var lines = normalized.Split('\n');
        var builder = new StringBuilder(normalized.Length + 64);
        var blankRun = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1)
                    continue;
            }
            else
            {
                blankRun = 0;
            }

            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(line);
        }

        var output = builder.ToString().Trim();
        if (options.IncludeSignatureNote &&
            !string.IsNullOrWhiteSpace(options.SignatureText) &&
            !output.Contains(options.SignatureText, StringComparison.Ordinal))
        {
            output = $"{output}\n\n{options.SignatureText.Trim()}";
        }

        return output;
    }
}
