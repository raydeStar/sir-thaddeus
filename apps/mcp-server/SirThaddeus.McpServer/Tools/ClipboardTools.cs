using System.ComponentModel;
using System.Threading;
using ModelContextProtocol.Server;

namespace SirThaddeus.McpServer.Tools;

/// <summary>
/// MCP tools for reading from and writing to the Windows system clipboard.
/// Runs clipboard access on an STA thread as required by WinForms.
/// </summary>
[McpServerToolType]
public static class ClipboardTools
{
    internal interface IClipboardAccessor
    {
        bool ContainsText();

        string GetText();

        void SetText(string text);
    }

    internal static IClipboardAccessor Accessor { get; set; } = new WinFormsClipboardAccessor();

    [McpServerTool, Description("Read the current contents of the system clipboard as text")]
    public static Task<string> ClipboardRead()
    {
        if (!ParseClipboardEnabled())
            return Task.FromResult("Clipboard tools are disabled by configuration.");

        return RunStaAsync(() =>
        {
            if (!Accessor.ContainsText())
                return "Clipboard is empty or does not currently contain text.";

            var text = Accessor.GetText();
            return string.IsNullOrWhiteSpace(text)
                ? "Clipboard is empty or does not currently contain text."
                : text;
        });
    }

    [McpServerTool, Description("Write text to the system clipboard")]
    public static Task<string> ClipboardWrite([Description("Text to place on the clipboard")] string text)
    {
        if (!ParseClipboardEnabled())
            return Task.FromResult("Clipboard tools are disabled by configuration.");

        if (text is null)
            return Task.FromResult("Error: text is required.");

        return RunStaAsync(() =>
        {
            Accessor.SetText(text);
            return "Clipboard updated.";
        });
    }

    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    private sealed class WinFormsClipboardAccessor : IClipboardAccessor
    {
        public bool ContainsText() => System.Windows.Forms.Clipboard.ContainsText();

        public string GetText() => System.Windows.Forms.Clipboard.GetText();

        public void SetText(string text) => System.Windows.Forms.Clipboard.SetText(text);
    }

    private static bool ParseClipboardEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("ST_CLIPBOARD_ENABLED");
        return raw?.Trim().ToLowerInvariant() switch
        {
            "0" or "false" or "no" or "off" => false,
            _ => true
        };
    }
}
