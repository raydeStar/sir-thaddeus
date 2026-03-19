namespace SirThaddeus.DocumentReader;

/// <summary>
/// Supported document formats for the document reader pipeline.
/// </summary>
public enum DocumentFormat
{
    /// <summary>PDF document.</summary>
    Pdf,
    /// <summary>Microsoft Word Open XML document.</summary>
    Docx,
    /// <summary>Microsoft Excel Open XML spreadsheet.</summary>
    Xlsx,
    /// <summary>Microsoft PowerPoint Open XML presentation.</summary>
    Pptx,
    /// <summary>Rich Text Format document.</summary>
    Rtf,
    /// <summary>Markdown text file.</summary>
    Markdown,
    /// <summary>Plain text file.</summary>
    PlainText,
    /// <summary>Comma-separated values file.</summary>
    Csv,
    /// <summary>Format not recognized or not specified.</summary>
    Unknown
}
