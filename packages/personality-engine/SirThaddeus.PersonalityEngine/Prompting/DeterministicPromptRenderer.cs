using System.Text;

namespace SirThaddeus.PersonalityEngine.Prompting;

/// <summary>
/// Converts prompt blocks into stable prompt bytes.
/// Ordering, dedupe, and newline behavior are deterministic.
/// </summary>
public static class DeterministicPromptRenderer
{
    public static string Render(IEnumerable<PromptBlock> blocks)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var normalized = blocks
            .Where(static b => b is not null && !string.IsNullOrWhiteSpace(b.Text))
            .Select(static b => new PromptBlock
            {
                Id = (b.Id ?? "").Trim(),
                Priority = b.Priority,
                Kind = b.Kind,
                Text = NormalizeLineEndings(b.Text ?? ""),
                MaxTokensHint = b.MaxTokensHint,
                Hash = b.Hash
            })
            .Where(static b => !string.IsNullOrWhiteSpace(b.Id) && !string.IsNullOrWhiteSpace(b.Text))
            .OrderBy(static b => b.Priority)
            .ThenBy(static b => (int)b.Kind)
            .ThenBy(static b => b.Id, StringComparer.Ordinal)
            .ToList();

        if (normalized.Count == 0)
            return "";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var builder = new StringBuilder(capacity: 2048);

        foreach (var block in normalized)
        {
            // Dedupe by exact normalized content and id.
            var dedupeKey = $"{block.Id}\n{block.Text}";
            if (!seen.Add(dedupeKey))
                continue;

            if (builder.Length > 0)
                builder.Append("\n\n");

            builder.Append('[');
            builder.Append(block.Kind);
            builder.Append(':');
            builder.Append(block.Id);
            builder.Append(']');
            builder.Append('\n');
            builder.Append(block.Text.Trim());
            builder.Append('\n');
            builder.Append("[/");
            builder.Append(block.Kind);
            builder.Append(':');
            builder.Append(block.Id);
            builder.Append(']');
        }

        return builder.ToString().Trim();
    }

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
             .Replace('\r', '\n');
}
