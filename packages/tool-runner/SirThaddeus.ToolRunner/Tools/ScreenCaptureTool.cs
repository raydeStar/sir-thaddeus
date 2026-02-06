using SirThaddeus.PermissionBroker;

namespace SirThaddeus.ToolRunner.Tools;

/// <summary>
/// Stub tool for screen capture operations.
/// In production, this would integrate with actual screen capture APIs.
/// </summary>
public sealed class ScreenCaptureTool : ITool
{
    public string Name => "screen_capture";

    public string Description => "Captures a screenshot of the current display or active window.";

    public Capability RequiredCapability => Capability.ScreenRead;

    public Task<object?> ExecuteAsync(ToolExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        // Extract target from call arguments
        var target = context.Call.Arguments?.TryGetValue("target", out var t) == true 
            ? t?.ToString() ?? "active_window" 
            : "active_window";

        // Stub implementation - returns mock data
        var result = new Dictionary<string, object>
        {
            ["captured"] = true,
            ["target"] = target,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["dimensions"] = new { width = 1920, height = 1080 },
            ["format"] = "png",
            ["size_bytes"] = 0  // Stub - no actual image data
        };

        return Task.FromResult<object?>(result);
    }
}
