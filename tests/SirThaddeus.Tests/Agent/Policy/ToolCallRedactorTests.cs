using SirThaddeus.Agent;

namespace SirThaddeus.Tests;

public class ToolCallRedactorTests
{
    [Fact]
    public void RedactInput_ClipboardWrite_DoesNotLeakRawText()
    {
        const string raw = "my-super-secret-password";
        var args = """{"text":"my-super-secret-password"}""";

        var redacted = ToolCallRedactor.RedactInput("clipboard_write", args);

        Assert.DoesNotContain(raw, redacted, StringComparison.Ordinal);
        Assert.Contains("Clipboard write request", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactOutput_ClipboardRead_DoesNotLeakRawClipboardData()
    {
        const string clipboardText = "otp-code-123456";

        var redacted = ToolCallRedactor.RedactOutput("clipboard_read", clipboardText);

        Assert.DoesNotContain(clipboardText, redacted, StringComparison.Ordinal);
        Assert.Contains("Clipboard content", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256=", redacted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedactOutput_DocumentRead_DoesNotLeakRawDocumentText()
    {
        const string documentText = "Confidential roadmap details for internal use only";

        var redacted = ToolCallRedactor.RedactOutput("document_read", documentText);

        Assert.DoesNotContain(documentText, redacted, StringComparison.Ordinal);
        Assert.Contains("Document content", redacted, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sha256=", redacted, StringComparison.OrdinalIgnoreCase);
    }
}
