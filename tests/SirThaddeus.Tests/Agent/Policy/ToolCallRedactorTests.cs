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

    [Fact]
    public void RedactOutput_ToolCapabilities_PreservesManifestGroupsAndBoundedNames()
    {
        const string manifest = """
            [
              {"name":"memory_retrieve","category":"memory"},
              {"name":"web_search","category":"web"},
              {"name":"file_read","category":"file"},
              {"name":"tool_ping","category":"meta"}
            ]
            """;

        var redacted = ToolCallRedactor.RedactOutput("tool_list_capabilities", manifest);

        Assert.Contains("4 tool(s)", redacted, StringComparison.Ordinal);
        Assert.Contains("capability groups: file, memory, meta, web", redacted, StringComparison.Ordinal);
        Assert.Contains("memory_retrieve", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("\"category\"", redacted, StringComparison.Ordinal);
    }
}
