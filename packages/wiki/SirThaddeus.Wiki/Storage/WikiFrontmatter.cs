using System.Globalization;
using System.Text;

namespace SirThaddeus.Wiki.Storage;

internal static class WikiFrontmatter
{
    public static string Write(WikiPage page, string markdown)
    {
        var builder = new StringBuilder();
        builder.AppendLine("---");
        builder.AppendLine(CultureInfo.InvariantCulture, $"id: {page.Id}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"rootId: {page.RootId}");
        if (!string.IsNullOrWhiteSpace(page.FolderId))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"folderId: {page.FolderId}");
        }
        builder.AppendLine(CultureInfo.InvariantCulture, $"title: {Escape(page.Title)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"version: {page.Version}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"createdAt: {page.CreatedAt:O}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"updatedAt: {page.UpdatedAt:O}");
        if (page.DeletedAt.HasValue)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"deletedAt: {page.DeletedAt.Value:O}");
        }
        builder.AppendLine("---");
        builder.Append(NormalizeBody(markdown));
        return builder.ToString();
    }

    public static string Strip(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return text;

        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? text : normalized[(end + "\n---\n".Length)..];
    }

    private static string NormalizeBody(string markdown)
    {
        var body = (markdown ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        return body.TrimStart('\ufeff', '\n', '\r');
    }

    private static string Escape(string value) => value.Replace("\n", " ", StringComparison.Ordinal).Trim();
}