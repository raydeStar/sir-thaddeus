using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace SirThaddeus.DocumentReader.Readers;

public sealed class XlsxDocumentReader : IDocumentReader
{
    public Task<DocumentContent> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        using var spreadsheet = SpreadsheetDocument.Open(path, false);
        var workbookPart = spreadsheet.WorkbookPart;
        if (workbookPart is null)
        {
            return Task.FromResult(new DocumentContent(
                Title: Path.GetFileName(path),
                Author: null,
                PageCount: null,
                TextContent: string.Empty,
                Metadata: null,
                Format: DocumentFormat.Xlsx));
        }

        var sb = new StringBuilder();
        var sheets = workbookPart.Workbook.Sheets?.Elements<Sheet>() ?? Enumerable.Empty<Sheet>();

        foreach (var sheet in sheets)
        {
            sb.AppendLine($"# Sheet: {sheet.Name}");
            var worksheetPart = workbookPart.GetPartById(sheet.Id!) as WorksheetPart;
            var rows = worksheetPart?.Worksheet.Descendants<Row>() ?? Enumerable.Empty<Row>();

            foreach (var row in rows)
            {
                var values = row.Elements<Cell>()
                    .Select(cell => ResolveCellValue(workbookPart, cell))
                    .ToArray();

                if (values.Length > 0)
                {
                    sb.AppendLine(string.Join("\t", values));
                }
            }

            sb.AppendLine();
        }

        return Task.FromResult(new DocumentContent(
            Title: Path.GetFileName(path),
            Author: spreadsheet.PackageProperties.Creator,
            PageCount: null,
            TextContent: sb.ToString().Trim(),
            Metadata: new Dictionary<string, string>
            {
                ["sheetCount"] = sheets.Count().ToString()
            },
            Format: DocumentFormat.Xlsx));
    }

    private static string ResolveCellValue(WorkbookPart workbookPart, Cell cell)
    {
        var value = cell.CellValue?.Text ?? string.Empty;

        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index))
        {
            return workbookPart.SharedStringTablePart?.SharedStringTable?.ElementAtOrDefault(index)?.InnerText ?? string.Empty;
        }

        return value;
    }
}
