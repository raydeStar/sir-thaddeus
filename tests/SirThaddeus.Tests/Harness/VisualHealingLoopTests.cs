using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Visual;
using Xunit;

namespace SirThaddeus.Tests;

public class VisualHealingLoopTests
{
    [Fact]
    public async Task Loop_PassesTarget_StopsEarly()
    {
        using var tempDir = CreateTempDir();
        var paths = MakePaths(tempDir.Path);
        var captureService = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Title: Good\nVerified\nSources: Yelp")
        };
        var grader = new VisualGrader();
        var patchApplier = new VisualPatchApplier();
        var loop = new VisualHealingLoop(captureService, grader, patchApplier);

        var spec = new VisualGradeSpec
        {
            CaptureDelayMs = 0,
            ExpectedElements = ["Verified", "Sources:"],
            ForbiddenElements = []
        };
        var options = new VisualHealingOptions
        {
            MaxIterations = 3,
            TargetScore = 8.0
        };

        var report = await loop.ExecuteAsync(
            spec, MakeTest(), "response", [], paths, options, null, CancellationToken.None);

        Assert.Single(report.Iterations);
        Assert.Equal(VisualHealingOutcome.PassedTarget, report.Iterations[0].Outcome);
        Assert.True(report.FinalScore >= 8.0);
    }

    [Fact]
    public async Task Loop_NoFixGenerator_StopsAfterGrade()
    {
        using var tempDir = CreateTempDir();
        var paths = MakePaths(tempDir.Path);
        var captureService = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Partial content")
        };
        var grader = new VisualGrader();
        var patchApplier = new VisualPatchApplier();
        var loop = new VisualHealingLoop(captureService, grader, patchApplier);

        var spec = new VisualGradeSpec
        {
            CaptureDelayMs = 0,
            ExpectedElements = ["Verified", "Sources:", "Phone"],
            ForbiddenElements = []
        };
        var options = new VisualHealingOptions
        {
            MaxIterations = 3,
            TargetScore = 9.0
        };

        var report = await loop.ExecuteAsync(
            spec, MakeTest(), "response", [], paths, options, null, CancellationToken.None);

        // Should stop after first grade since no fix generator
        Assert.Single(report.Iterations);
    }

    [Fact]
    public async Task Loop_FixGeneratorFails_RecordsFailure()
    {
        using var tempDir = CreateTempDir();
        var paths = MakePaths(tempDir.Path);
        var captureService = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Minimal")
        };
        var grader = new VisualGrader();
        var patchApplier = new VisualPatchApplier();
        var loop = new VisualHealingLoop(captureService, grader, patchApplier);

        var spec = new VisualGradeSpec
        {
            CaptureDelayMs = 0,
            ExpectedElements = ["Required"],
            ForbiddenElements = []
        };
        var options = new VisualHealingOptions
        {
            MaxIterations = 3,
            TargetScore = 9.0
        };

        Task<IReadOnlyList<VisualPatchSuggestion>> FailingGenerator(VisualGradeResult _, CancellationToken __) =>
            throw new Exception("Generator offline");

        var report = await loop.ExecuteAsync(
            spec, MakeTest(), "resp", [], paths, options, FailingGenerator, CancellationToken.None);

        Assert.Contains(report.Iterations, i => i.Outcome == VisualHealingOutcome.FixGenerationFailed);
    }

    [Fact]
    public async Task Loop_EmptyFixList_StopsGracefully()
    {
        using var tempDir = CreateTempDir();
        var paths = MakePaths(tempDir.Path);
        var captureService = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Some content")
        };
        var grader = new VisualGrader();
        var patchApplier = new VisualPatchApplier();
        var loop = new VisualHealingLoop(captureService, grader, patchApplier);

        var spec = new VisualGradeSpec
        {
            CaptureDelayMs = 0,
            ExpectedElements = ["Missing"],
            ForbiddenElements = []
        };
        var options = new VisualHealingOptions
        {
            MaxIterations = 3,
            TargetScore = 9.0
        };

        Task<IReadOnlyList<VisualPatchSuggestion>> EmptyGenerator(VisualGradeResult _, CancellationToken __) =>
            Task.FromResult<IReadOnlyList<VisualPatchSuggestion>>([]);

        var report = await loop.ExecuteAsync(
            spec, MakeTest(), "resp", [], paths, options, EmptyGenerator, CancellationToken.None);

        Assert.Contains(report.Iterations, i => i.Outcome == VisualHealingOutcome.NoFixesGenerated);
    }

    [Fact]
    public async Task Loop_RespectsMaxIterations()
    {
        using var tempDir = CreateTempDir();
        var paths = MakePaths(tempDir.Path);
        var captureService = new VisualCaptureService
        {
            CaptureDelegate = (_, _) => Task.FromResult("Nope")
        };
        var grader = new VisualGrader();
        var patchApplier = new VisualPatchApplier();
        var loop = new VisualHealingLoop(captureService, grader, patchApplier);

        var spec = new VisualGradeSpec
        {
            CaptureDelayMs = 0,
            ExpectedElements = ["Never"],
            ForbiddenElements = []
        };
        var options = new VisualHealingOptions
        {
            MaxIterations = 2,
            TargetScore = 10.0
        };

        var report = await loop.ExecuteAsync(
            spec, MakeTest(), "resp", [], paths, options, null, CancellationToken.None);

        Assert.True(report.Iterations.Count <= 2);
    }

    // ─── VisualPatchApplier ───────────────────────────────────────────

    [Fact]
    public void PatchApplier_RespectsAllowlist()
    {
        using var tempDir = CreateTempDir();
        var filePath = Path.Combine(tempDir.Path, "test.cs");
        File.WriteAllText(filePath, "old content here");

        var applier = new VisualPatchApplier();
        var result = applier.Apply(
            [new VisualPatchSuggestion { File = filePath, Find = "old", Replace = "new" }],
            new VisualHealingOptions
            {
                AllowedFiles = ["nonexistent/"], // Doesn't match
                MaxPatchFiles = 5,
                MaxPatchLines = 50
            });

        Assert.Equal(0, result.AppliedCount);
        Assert.Equal("old content here", File.ReadAllText(filePath));
    }

    [Fact]
    public void PatchApplier_Rollback_RestoresOriginal()
    {
        using var tempDir = CreateTempDir();
        var workspace = Directory.GetCurrentDirectory();
        var relativePath = Path.GetRelativePath(workspace, tempDir.Path).Replace('\\', '/');

        var filePath = Path.Combine(tempDir.Path, "code.cs");
        File.WriteAllText(filePath, "original code");

        var applier = new VisualPatchApplier();
        var result = applier.Apply(
            [new VisualPatchSuggestion { File = filePath, Find = "original", Replace = "patched" }],
            new VisualHealingOptions
            {
                AllowedFiles = [relativePath],
                MaxPatchFiles = 5,
                MaxPatchLines = 50
            });

        Assert.Equal(1, result.AppliedCount);
        Assert.Equal("patched code", File.ReadAllText(filePath));

        applier.Rollback(result);
        Assert.Equal("original code", File.ReadAllText(filePath));
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private static HarnessTestCase MakeTest() => new()
    {
        Id = "healing_test",
        Name = "Healing test",
        UserMessage = "Show me restaurant info",
        AllowedTools = [],
        MinScore = 5.0
    };

    private static ArtifactPaths MakePaths(string root) => new()
    {
        RootDirectory = root,
        InputJsonPath = Path.Combine(root, "input.json"),
        StepsJsonlPath = Path.Combine(root, "steps.jsonl"),
        FinalTextPath = Path.Combine(root, "final.txt"),
        ScoreJsonPath = Path.Combine(root, "score.json"),
        DiffMarkdownPath = Path.Combine(root, "diff.md"),
        JudgePacketPath = Path.Combine(root, "judge_packet.json"),
        JudgeResultPath = Path.Combine(root, "judge_result.json")
    };

    private static TempDir CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), $"heal-test-{Guid.NewGuid():N}"[..28]);
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
