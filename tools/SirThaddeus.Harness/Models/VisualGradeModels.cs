using System.Text.Json.Serialization;

namespace SirThaddeus.Harness.Models;

/// <summary>
/// Test case extension for UI visual grading.
/// Describes what visual state to capture and how to grade it.
/// </summary>
public sealed record VisualGradeSpec
{
    /// <summary>Capture target: "active_window", "full_screen", or a specific window title substring.</summary>
    [JsonPropertyName("capture_target")]
    public string CaptureTarget { get; init; } = "active_window";

    /// <summary>Page/state rubric description for the judge (e.g., "Briefing panel should show verified confidence with source citations").</summary>
    [JsonPropertyName("rubric")]
    public string Rubric { get; init; } = "";

    /// <summary>Expected visual elements that should be present (for soft scoring).</summary>
    [JsonPropertyName("expected_elements")]
    public IReadOnlyList<string> ExpectedElements { get; init; } = [];

    /// <summary>Elements that must NOT be present (error states, empty placeholders).</summary>
    [JsonPropertyName("forbidden_elements")]
    public IReadOnlyList<string> ForbiddenElements { get; init; } = [];

    /// <summary>Delay in ms after test completion before capturing screenshot.</summary>
    [JsonPropertyName("capture_delay_ms")]
    public int CaptureDelayMs { get; init; } = 500;
}

/// <summary>
/// Result of a visual grade assessment.
/// </summary>
public sealed record VisualGradeResult
{
    /// <summary>Path to the captured screenshot or accessibility tree dump.</summary>
    [JsonPropertyName("capture_path")]
    public string CapturePath { get; init; } = "";

    /// <summary>Textual representation of the UI state (accessibility tree text).</summary>
    [JsonPropertyName("ui_text")]
    public string UiText { get; init; } = "";

    /// <summary>Visual grade score (0-10).</summary>
    [JsonPropertyName("visual_score")]
    public double VisualScore { get; init; }

    /// <summary>Grading reasons from the judge.</summary>
    [JsonPropertyName("reasons")]
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>Whether expected elements were found.</summary>
    [JsonPropertyName("elements_found")]
    public IReadOnlyList<string> ElementsFound { get; init; } = [];

    /// <summary>Expected elements that were missing.</summary>
    [JsonPropertyName("elements_missing")]
    public IReadOnlyList<string> ElementsMissing { get; init; } = [];

    /// <summary>Forbidden elements that were detected.</summary>
    [JsonPropertyName("forbidden_detected")]
    public IReadOnlyList<string> ForbiddenDetected { get; init; } = [];

    [JsonPropertyName("captured_at")]
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Extended judge packet that includes visual context.
/// </summary>
public sealed record VisualJudgePacket
{
    [JsonPropertyName("test_id")]
    public string TestId { get; init; } = "";

    [JsonPropertyName("test_name")]
    public string TestName { get; init; } = "";

    [JsonPropertyName("user_message")]
    public string UserMessage { get; init; } = "";

    [JsonPropertyName("final_response")]
    public string FinalResponse { get; init; } = "";

    [JsonPropertyName("ui_text")]
    public string UiText { get; init; } = "";

    [JsonPropertyName("visual_rubric")]
    public string VisualRubric { get; init; } = "";

    [JsonPropertyName("expected_elements")]
    public IReadOnlyList<string> ExpectedElements { get; init; } = [];

    [JsonPropertyName("forbidden_elements")]
    public IReadOnlyList<string> ForbiddenElements { get; init; } = [];

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<ToolCallSnapshot> ToolCalls { get; init; } = [];
}
