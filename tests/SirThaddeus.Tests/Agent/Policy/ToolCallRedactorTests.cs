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
    public void RedactInput_FileWrite_DoesNotLeakContent()
    {
        const string secret = "SERVICE_TOKEN=ultra-secret-value";
        var arguments = $$"""{"path":"service.env","content":"{{secret}}"}""";

        var redacted = ToolCallRedactor.RedactInput("file_write", arguments);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("service.env", redacted, StringComparison.Ordinal);
        Assert.Contains("content_sha256=", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactInput_FileReplace_DoesNotLeakOldOrNewText()
    {
        const string oldSecret = "TOKEN=old-secret";
        const string newSecret = "TOKEN=new-secret";
        var arguments = $$"""{"path":"service.env","oldText":"{{oldSecret}}","newText":"{{newSecret}}"}""";

        var redacted = ToolCallRedactor.RedactInput("file_replace", arguments);

        Assert.DoesNotContain(oldSecret, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(newSecret, redacted, StringComparison.Ordinal);
        Assert.Contains("old_sha256=", redacted, StringComparison.Ordinal);
        Assert.Contains("new_sha256=", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactOutput_FileMutation_DoesNotLeakVerifiedContent()
    {
        const string secret = "SERVICE_TOKEN=verified-secret";
        var output = $$"""{"ok":true,"verified":true,"bytes":37,"post_content":"{{secret}}"}""";

        var redacted = ToolCallRedactor.RedactOutput("file_write", output);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("ok=true", redacted, StringComparison.Ordinal);
        Assert.Contains("verified=true", redacted, StringComparison.Ordinal);
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
