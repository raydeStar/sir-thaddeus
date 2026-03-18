using SirThaddeus.DocumentReader;
using SirThaddeus.DocumentReader.Readers;

namespace SirThaddeus.Tests;

public sealed class DocumentReaderFactoryTests
{
    [Theory]
    [InlineData("example.pdf", typeof(PdfDocumentReader))]
    [InlineData("example.docx", typeof(DocxDocumentReader))]
    [InlineData("example.xlsx", typeof(XlsxDocumentReader))]
    [InlineData("example.csv", typeof(CsvDocumentReader))]
    [InlineData("example.rtf", typeof(RtfDocumentReader))]
    [InlineData("example.md", typeof(PlainTextReader))]
    [InlineData("example.txt", typeof(PlainTextReader))]
    public void Resolve_ReturnsExpectedReaderForExtension(string path, Type expectedType)
    {
        var factory = new DocumentReaderFactory();

        var reader = factory.Resolve(path);

        Assert.IsType(expectedType, reader);
    }

    [Fact]
    public void Resolve_UnknownExtension_ReturnsPlainTextReader()
    {
        var factory = new DocumentReaderFactory();

        var reader = factory.Resolve("example.unknown");

        Assert.IsType<PlainTextReader>(reader);
    }
}
