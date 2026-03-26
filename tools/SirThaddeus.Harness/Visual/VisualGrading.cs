using System.Text;
using System.Text.Json;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Visual;

/// <summary>
/// Captures UI state for visual grading. Uses the accessibility tree (text-based)
/// since the harness runs against the headless runtime which may not have a visible window.
/// For Avalonia UI testing, this captures the accessibility tree via UIA or a provided delegate.
/// </summary>
public sealed class VisualCaptureService
{
    /// <summary>
    /// Delegate that captures the current UI text. Implementations may use UIA, HTTP endpoint, or mock.
    /// Returns the accessibility tree text or structured text representation.
    /// </summary>
    public Func<string, CancellationToken, Task<string>>? CaptureDelegate { get; set; }

    /// <summary>
    /// Captures UI state for a visual grade spec.
    /// </summary>
    public async Task<VisualCaptureResult> CaptureAsync(
        VisualGradeSpec spec,
        ArtifactPaths paths,
        CancellationToken cancellationToken)
    {
        if (spec.CaptureDelayMs > 0)
            await Task.Delay(spec.CaptureDelayMs, cancellationToken);

        var uiText = "";
        if (CaptureDelegate is not null)
        {
            try
            {
                uiText = await CaptureDelegate(spec.CaptureTarget, cancellationToken);
            }
            catch (Exception ex)
            {
                uiText = $"[capture failed: {ex.Message}]";
            }
        }

        // Write capture artifact
        var capturePath = Path.Combine(paths.RootDirectory, "visual_capture.txt");
        await File.WriteAllTextAsync(capturePath, uiText, cancellationToken);

        return new VisualCaptureResult(uiText, capturePath, DateTimeOffset.UtcNow);
    }
}

public sealed record VisualCaptureResult(
    string UiText,
    string CapturePath,
    DateTimeOffset CapturedAt);

/// <summary>
/// Grade-only visual assessor. Evaluates captured UI state against expected elements
/// without attempting any fixes. This is Phase 4A — observation only.
/// </summary>
public sealed class VisualGrader
{
    private readonly Func<VisualJudgePacket, CancellationToken, Task<VisualGradeResult>>? _judgeDelegate;

    /// <summary>
    /// Creates a grader. If judgeDelegate is null, uses deterministic element matching only.
    /// If provided, the judge (LLM) evaluates visual quality alongside element checks.
    /// </summary>
    public VisualGrader(
        Func<VisualJudgePacket, CancellationToken, Task<VisualGradeResult>>? judgeDelegate = null)
    {
        _judgeDelegate = judgeDelegate;
    }

    /// <summary>
    /// Grades captured UI state against the visual spec.
    /// Returns a deterministic grade based on element presence/absence,
    /// optionally enhanced by an LLM judge.
    /// </summary>
    public async Task<VisualGradeResult> GradeAsync(
        VisualGradeSpec spec,
        VisualCaptureResult capture,
        HarnessTestCase test,
        string finalResponse,
        IReadOnlyList<ToolCallSnapshot> toolCalls,
        CancellationToken cancellationToken)
    {
        // Deterministic element checks
        var found = new List<string>();
        var missing = new List<string>();
        var forbiddenDetected = new List<string>();

        foreach (var expected in spec.ExpectedElements)
        {
            if (capture.UiText.Contains(expected, StringComparison.OrdinalIgnoreCase))
                found.Add(expected);
            else
                missing.Add(expected);
        }

        foreach (var forbidden in spec.ForbiddenElements)
        {
            if (capture.UiText.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                forbiddenDetected.Add(forbidden);
        }

        // Deterministic score: presence ratio minus forbidden penalty
        var total = spec.ExpectedElements.Count + spec.ForbiddenElements.Count;
        double deterministicScore = total > 0
            ? 10.0 * (found.Count - forbiddenDetected.Count * 2.0) / Math.Max(total, 1)
            : 5.0; // No spec elements → neutral
        deterministicScore = Math.Clamp(deterministicScore, 0, 10);

        var reasons = new List<string>();
        if (found.Count > 0)
            reasons.Add($"Found {found.Count}/{spec.ExpectedElements.Count} expected elements");
        if (missing.Count > 0)
            reasons.Add($"Missing elements: {string.Join(", ", missing)}");
        if (forbiddenDetected.Count > 0)
            reasons.Add($"Forbidden elements detected: {string.Join(", ", forbiddenDetected)}");

        // If we have a judge, let it refine the score
        if (_judgeDelegate is not null)
        {
            var packet = new VisualJudgePacket
            {
                TestId = test.Id,
                TestName = test.Name,
                UserMessage = test.UserMessage,
                FinalResponse = finalResponse,
                UiText = capture.UiText,
                VisualRubric = spec.Rubric,
                ExpectedElements = spec.ExpectedElements,
                ForbiddenElements = spec.ForbiddenElements,
                ToolCalls = toolCalls
            };

            try
            {
                var judgeResult = await _judgeDelegate(packet, cancellationToken);
                // Blend: 40% deterministic + 60% judge
                var blendedScore = deterministicScore * 0.4 + judgeResult.VisualScore * 0.6;
                return judgeResult with
                {
                    VisualScore = Math.Clamp(blendedScore, 0, 10),
                    ElementsFound = found,
                    ElementsMissing = missing,
                    ForbiddenDetected = forbiddenDetected,
                    CapturePath = capture.CapturePath,
                    CapturedAt = capture.CapturedAt
                };
            }
            catch
            {
                reasons.Add("Judge evaluation failed; using deterministic score only");
            }
        }

        return new VisualGradeResult
        {
            CapturePath = capture.CapturePath,
            UiText = TruncateForArtifact(capture.UiText, 8000),
            VisualScore = deterministicScore,
            Reasons = reasons,
            ElementsFound = found,
            ElementsMissing = missing,
            ForbiddenDetected = forbiddenDetected,
            CapturedAt = capture.CapturedAt
        };
    }

    private static string TruncateForArtifact(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...[truncated]";
}

/// <summary>
/// Extends HarnessArtifactWriter with visual grade artifact support.
/// </summary>
public static class VisualArtifactExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// Writes visual grade result as an artifact.
    /// </summary>
    public static async Task WriteVisualGradeAsync(
        ArtifactPaths paths,
        VisualGradeResult grade,
        CancellationToken cancellationToken)
    {
        var gradePath = Path.Combine(paths.RootDirectory, "visual_grade.json");
        var json = JsonSerializer.Serialize(grade, JsonOptions);
        await File.WriteAllTextAsync(gradePath, json, cancellationToken);
    }

    /// <summary>
    /// Writes visual judge packet as an artifact.
    /// </summary>
    public static async Task WriteVisualJudgePacketAsync(
        ArtifactPaths paths,
        VisualJudgePacket packet,
        CancellationToken cancellationToken)
    {
        var packetPath = Path.Combine(paths.RootDirectory, "visual_judge_packet.json");
        var json = JsonSerializer.Serialize(packet, JsonOptions);
        await File.WriteAllTextAsync(packetPath, json, cancellationToken);
    }
}
