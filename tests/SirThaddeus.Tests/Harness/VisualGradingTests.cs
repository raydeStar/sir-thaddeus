using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Visual;
using Xunit;

namespace SirThaddeus.Tests;

public class VisualGradingTests
{
    // ─── VisualGrader Deterministic Scoring ───────────────────────────

    [Fact]
    public async Task Grader_AllExpectedFound_ScoresHigh()
    {
        var grader = new VisualGrader();
        var spec = new VisualGradeSpec
        {
            ExpectedElements = ["Verified", "Sources:", "Open Mon-Fri"],
            ForbiddenElements = []
        };
        var capture = new VisualCaptureResult(
            "Title: Pizza Palace\nConfidence: Verified\nSources: Yelp, Google\nHours: Open Mon-Fri",
            "/tmp/cap.txt",
            DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        Assert.Equal(10.0, result.VisualScore, 0.1);
        Assert.Equal(3, result.ElementsFound.Count);
        Assert.Empty(result.ElementsMissing);
    }

    [Fact]
    public async Task Grader_MissingElements_ScoresLower()
    {
        var grader = new VisualGrader();
        var spec = new VisualGradeSpec
        {
            ExpectedElements = ["Verified", "Sources:", "Phone Number"],
            ForbiddenElements = []
        };
        var capture = new VisualCaptureResult(
            "Title: Pizza Palace\nConfidence: Verified",
            "/tmp/cap.txt",
            DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        Assert.True(result.VisualScore < 10.0);
        Assert.Single(result.ElementsFound);
        Assert.Equal(2, result.ElementsMissing.Count);
    }

    [Fact]
    public async Task Grader_ForbiddenDetected_PenalizesScore()
    {
        var grader = new VisualGrader();
        var spec = new VisualGradeSpec
        {
            ExpectedElements = ["Data"],
            ForbiddenElements = ["Error", "Loading..."]
        };
        var capture = new VisualCaptureResult(
            "Data loaded\nError: connection failed",
            "/tmp/cap.txt",
            DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        Assert.True(result.VisualScore < 5.0);
        Assert.Single(result.ForbiddenDetected);
        Assert.Contains("Error", result.ForbiddenDetected);
    }

    [Fact]
    public async Task Grader_NoSpec_ReturnsNeutral()
    {
        var grader = new VisualGrader();
        var spec = new VisualGradeSpec
        {
            ExpectedElements = [],
            ForbiddenElements = []
        };
        var capture = new VisualCaptureResult("Any UI content", "/tmp/cap.txt", DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        Assert.Equal(5.0, result.VisualScore);
    }

    [Fact]
    public async Task Grader_WithJudge_BlendsScores()
    {
        var judgeResult = new VisualGradeResult
        {
            VisualScore = 8.0,
            Reasons = ["Good layout"],
        };
        var grader = new VisualGrader(
            judgeDelegate: (_, _) => Task.FromResult(judgeResult));

        var spec = new VisualGradeSpec
        {
            ExpectedElements = ["Title"],
            ForbiddenElements = []
        };
        var capture = new VisualCaptureResult("Title visible", "/tmp/cap.txt", DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        // Deterministic = 10, Judge = 8, Blended = 10*0.4 + 8*0.6 = 8.8
        Assert.Equal(8.8, result.VisualScore, 0.1);
    }

    [Fact]
    public async Task Grader_JudgeFails_FallsToDeterministic()
    {
        var grader = new VisualGrader(
            judgeDelegate: (_, _) => throw new Exception("Judge offline"));

        var spec = new VisualGradeSpec
        {
            ExpectedElements = ["Title"],
            ForbiddenElements = []
        };
        var capture = new VisualCaptureResult("Title here", "/tmp/cap.txt", DateTimeOffset.UtcNow);

        var result = await grader.GradeAsync(spec, capture, MakeTest(), "response", [], CancellationToken.None);

        Assert.Equal(10.0, result.VisualScore, 0.1);
        Assert.Contains(result.Reasons, r => r.Contains("Judge evaluation failed"));
    }

    // ─── VisualCaptureService ─────────────────────────────────────────

    [Fact]
    public async Task CaptureService_WritesArtifact()
    {
        using var tempDir = CreateTempDir();
        var paths = new ArtifactPaths
        {
            RootDirectory = tempDir.Path,
            InputJsonPath = Path.Combine(tempDir.Path, "input.json"),
            StepsJsonlPath = Path.Combine(tempDir.Path, "steps.jsonl"),
            FinalTextPath = Path.Combine(tempDir.Path, "final.txt"),
            ScoreJsonPath = Path.Combine(tempDir.Path, "score.json"),
            DiffMarkdownPath = Path.Combine(tempDir.Path, "diff.md"),
            JudgePacketPath = Path.Combine(tempDir.Path, "judge_packet.json"),
            JudgeResultPath = Path.Combine(tempDir.Path, "judge_result.json")
        };

        var service = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Button: OK\nLabel: Ready")
        };

        var spec = new VisualGradeSpec { CaptureDelayMs = 0 };
        var result = await service.CaptureAsync(spec, paths, CancellationToken.None);

        Assert.Equal("Button: OK\nLabel: Ready", result.UiText);
        Assert.True(File.Exists(result.CapturePath));
        Assert.Equal("Button: OK\nLabel: Ready", File.ReadAllText(result.CapturePath));
    }

    [Fact]
    public async Task CaptureService_NullDelegate_ReturnsEmpty()
    {
        using var tempDir = CreateTempDir();
        var paths = new ArtifactPaths
        {
            RootDirectory = tempDir.Path,
            InputJsonPath = Path.Combine(tempDir.Path, "input.json"),
            StepsJsonlPath = Path.Combine(tempDir.Path, "steps.jsonl"),
            FinalTextPath = Path.Combine(tempDir.Path, "final.txt"),
            ScoreJsonPath = Path.Combine(tempDir.Path, "score.json"),
            DiffMarkdownPath = Path.Combine(tempDir.Path, "diff.md"),
            JudgePacketPath = Path.Combine(tempDir.Path, "judge_packet.json"),
            JudgeResultPath = Path.Combine(tempDir.Path, "judge_result.json")
        };

        var service = new VisualCaptureService();
        var spec = new VisualGradeSpec { CaptureDelayMs = 0 };
        var result = await service.CaptureAsync(spec, paths, CancellationToken.None);

        Assert.Equal("", result.UiText);
    }

    // ─── Artifact Extensions ──────────────────────────────────────────

    [Fact]
    public async Task WriteVisualGrade_CreatesJsonFile()
    {
        using var tempDir = CreateTempDir();
        var paths = new ArtifactPaths
        {
            RootDirectory = tempDir.Path,
            InputJsonPath = "", StepsJsonlPath = "", FinalTextPath = "",
            ScoreJsonPath = "", DiffMarkdownPath = "",
            JudgePacketPath = "", JudgeResultPath = ""
        };

        var grade = new VisualGradeResult
        {
            VisualScore = 7.5,
            Reasons = ["Good structure"],
            ElementsFound = ["Title"]
        };

        await VisualArtifactExtensions.WriteVisualGradeAsync(paths, grade, CancellationToken.None);

        var jsonPath = Path.Combine(tempDir.Path, "visual_grade.json");
        Assert.True(File.Exists(jsonPath));
        var content = File.ReadAllText(jsonPath);
        Assert.Contains("7.5", content);
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static HarnessTestCase MakeTest() => new()
    {
        Id = "visual_test_01",
        Name = "Visual test",
        UserMessage = "Show me info about the restaurant",
        AllowedTools = [],
        MinScore = 5.0
    };

    private static TempDir CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"visual-test-{Guid.NewGuid():N}"[..30]);
        Directory.CreateDirectory(path);
        return new TempDir(path);
    }

    private sealed record TempDir(string Path) : IDisposable
    {
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
            catch { /* best effort */ }
        }
    }
}
