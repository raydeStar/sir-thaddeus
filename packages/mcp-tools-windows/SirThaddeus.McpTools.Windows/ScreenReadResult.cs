using System.Text;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// A butler always presents findings on a silver tray, not dumped on the floor.
/// </summary>
public sealed class ScreenReadResult
{
    /// <summary>Brief context: app name, window title, detected content type.</summary>
    public string WindowContext { get; set; } = string.Empty;

    /// <summary>What type of content we detected (WebPage, Document, Code, Image, Math, Terminal, Self, Unknown).</summary>
    public string ContentType { get; set; } = "Unknown";

    /// <summary>The main text content in reading order — this is the meat of it.</summary>
    public string ReadableContent { get; set; } = string.Empty;

    /// <summary>Any secondary/sidebar content that might be relevant.</summary>
    public string SecondaryContent { get; set; } = string.Empty;

    /// <summary>Interactive elements available (summarized, not enumerated).</summary>
    public string AvailableActions { get; set; } = string.Empty;

    /// <summary>Anything we couldn't extract well (images without alt text, canvas elements, etc.).</summary>
    public string Limitations { get; set; } = string.Empty;

    public string ToPromptText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Screen Read]");

        if (!string.IsNullOrWhiteSpace(WindowContext))
            sb.AppendLine($"Window: {WindowContext}");

        sb.AppendLine($"Content Type: {ContentType}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(ReadableContent))
        {
            sb.AppendLine("Content:");
            sb.AppendLine(ReadableContent);
        }
        else
        {
            sb.AppendLine("Content: (no readable text content detected)");
        }

        if (!string.IsNullOrWhiteSpace(SecondaryContent))
        {
            sb.AppendLine();
            sb.AppendLine("Secondary:");
            sb.AppendLine(SecondaryContent);
        }

        if (!string.IsNullOrWhiteSpace(AvailableActions))
        {
            sb.AppendLine();
            sb.AppendLine($"Available Actions: {AvailableActions}");
        }

        if (!string.IsNullOrWhiteSpace(Limitations))
        {
            sb.AppendLine();
            sb.AppendLine($"Limitations: {Limitations}");
        }

        return sb.ToString().TrimEnd();
    }
}
