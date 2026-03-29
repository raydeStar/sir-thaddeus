using System.Text.Json;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Visual;

/// <summary>
/// Constrained self-healing loop for UI defects. Takes visual grade results,
/// generates fix suggestions, applies patches with strict rails, and validates improvements.
/// Phase 4B: fix generation + patch application with safety rails.
/// </summary>
public sealed class VisualHealingLoop
{
    private readonly VisualCaptureService _captureService;
    private readonly VisualGrader _grader;
    private readonly VisualPatchApplier _patchApplier;

    public VisualHealingLoop(
        VisualCaptureService captureService,
        VisualGrader grader,
        VisualPatchApplier patchApplier)
    {
        _captureService = captureService;
        _grader = grader;
        _patchApplier = patchApplier;
    }

    /// <summary>
    /// Runs the self-healing loop with strict constraints.
    /// </summary>
    public async Task<VisualHealingReport> ExecuteAsync(
        VisualGradeSpec spec,
        HarnessTestCase test,
        string finalResponse,
        IReadOnlyList<ToolCallSnapshot> toolCalls,
        ArtifactPaths paths,
        VisualHealingOptions options,
        Func<VisualGradeResult, CancellationToken, Task<IReadOnlyList<VisualPatchSuggestion>>>? fixGenerator,
        CancellationToken cancellationToken)
    {
        var iterations = new List<VisualHealingIteration>();
        VisualGradeResult? bestGrade = null;
        var bestScore = -1.0;

        for (var iter = 1; iter <= options.MaxIterations; iter++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Capture current state
            var capture = await _captureService.CaptureAsync(spec, paths, cancellationToken);

            // Grade it
            var grade = await _grader.GradeAsync(
                spec, capture, test, finalResponse, toolCalls, cancellationToken);

            var iteration = new VisualHealingIteration
            {
                Iteration = iter,
                Grade = grade,
                PatchesApplied = [],
                Outcome = VisualHealingOutcome.Graded
            };

            if (grade.VisualScore >= options.TargetScore)
            {
                iteration = iteration with { Outcome = VisualHealingOutcome.PassedTarget };
                iterations.Add(iteration);
                bestGrade = grade;
                bestScore = grade.VisualScore;
                break;
            }

            if (grade.VisualScore > bestScore)
            {
                bestScore = grade.VisualScore;
                bestGrade = grade;
            }

            // If this is the last iteration or no fix generator, just record
            if (iter == options.MaxIterations || fixGenerator is null)
            {
                iterations.Add(iteration);
                break;
            }

            // Generate fix suggestions
            IReadOnlyList<VisualPatchSuggestion> suggestions;
            try
            {
                suggestions = await fixGenerator(grade, cancellationToken);
            }
            catch
            {
                iteration = iteration with { Outcome = VisualHealingOutcome.FixGenerationFailed };
                iterations.Add(iteration);
                break;
            }

            if (suggestions.Count == 0)
            {
                iteration = iteration with { Outcome = VisualHealingOutcome.NoFixesGenerated };
                iterations.Add(iteration);
                break;
            }

            // Apply patches with safety rails
            var patchResult = _patchApplier.Apply(suggestions, options);
            iteration = iteration with
            {
                PatchesApplied = patchResult.AppliedFiles,
                Outcome = patchResult.AppliedCount > 0
                    ? VisualHealingOutcome.PatchApplied
                    : VisualHealingOutcome.PatchRejected
            };
            iterations.Add(iteration);

            if (patchResult.AppliedCount == 0)
                break;

            // Verify improvement: re-capture and re-grade
            var verifyCapture = await _captureService.CaptureAsync(spec, paths, cancellationToken);
            var verifyGrade = await _grader.GradeAsync(
                spec, verifyCapture, test, finalResponse, toolCalls, cancellationToken);

            // Regression check: if score didn't improve, rollback
            if (verifyGrade.VisualScore <= grade.VisualScore)
            {
                _patchApplier.Rollback(patchResult);
                iterations.Add(new VisualHealingIteration
                {
                    Iteration = iter,
                    Grade = verifyGrade,
                    PatchesApplied = [],
                    Outcome = VisualHealingOutcome.RolledBack
                });
                break;
            }

            if (verifyGrade.VisualScore > bestScore)
            {
                bestScore = verifyGrade.VisualScore;
                bestGrade = verifyGrade;
            }
        }

        return new VisualHealingReport
        {
            Iterations = iterations,
            BestGrade = bestGrade,
            FinalScore = bestScore
        };
    }
}

/// <summary>
/// Options governing the self-healing loop.
/// </summary>
public sealed record VisualHealingOptions
{
    /// <summary>Maximum iterations before stopping.</summary>
    public int MaxIterations { get; init; } = 3;

    /// <summary>Target score at which the loop stops.</summary>
    public double TargetScore { get; init; } = 8.0;

    /// <summary>File allowlist — only these files/paths may be patched.</summary>
    public IReadOnlyList<string> AllowedFiles { get; init; } = [];

    /// <summary>Maximum number of files to patch per iteration.</summary>
    public int MaxPatchFiles { get; init; } = 3;

    /// <summary>Maximum total lines changed per iteration.</summary>
    public int MaxPatchLines { get; init; } = 50;

    /// <summary>If true, always rollback on build failure after patch.</summary>
    public bool RollbackOnBuildFailure { get; init; } = true;
}

/// <summary>
/// A suggested UI fix from the judge.
/// </summary>
public sealed record VisualPatchSuggestion
{
    public string File { get; init; } = "";
    public string Find { get; init; } = "";
    public string Replace { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>
/// Applies visual patches with strict rails.
/// </summary>
public sealed class VisualPatchApplier
{
    /// <summary>
    /// Applies suggestions respecting the allowlist, file count, and line budget.
    /// </summary>
    public VisualPatchResult Apply(
        IReadOnlyList<VisualPatchSuggestion> suggestions,
        VisualHealingOptions options)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var appliedFiles = new List<string>();
        var lineBudget = 0;

        foreach (var suggestion in suggestions)
        {
            if (appliedFiles.Count >= options.MaxPatchFiles)
                break;

            if (string.IsNullOrWhiteSpace(suggestion.File) ||
                string.IsNullOrWhiteSpace(suggestion.Find))
                continue;

            var fullPath = ToAbsolutePath(suggestion.File);
            if (!System.IO.File.Exists(fullPath))
                continue;

            if (!IsAllowed(fullPath, options.AllowedFiles))
                continue;

            var original = System.IO.File.ReadAllText(fullPath);
            var idx = original.IndexOf(suggestion.Find, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var replacement = suggestion.Replace ?? "";
            var lineDelta = Math.Abs(CountLines(replacement) - CountLines(suggestion.Find));
            if (lineBudget + lineDelta > options.MaxPatchLines)
                continue;

            if (!snapshots.ContainsKey(fullPath))
                snapshots[fullPath] = original;

            var updated = original.Remove(idx, suggestion.Find.Length).Insert(idx, replacement);
            System.IO.File.WriteAllText(fullPath, updated);
            appliedFiles.Add(fullPath);
            lineBudget += lineDelta;
        }

        return new VisualPatchResult
        {
            AppliedCount = appliedFiles.Count,
            AppliedFiles = appliedFiles,
            OriginalSnapshots = snapshots
        };
    }

    /// <summary>
    /// Rolls back all changes from a patch result.
    /// </summary>
    public void Rollback(VisualPatchResult result)
    {
        foreach (var (path, content) in result.OriginalSnapshots)
            System.IO.File.WriteAllText(path, content);
    }

    private static bool IsAllowed(string absolutePath, IReadOnlyList<string> allowedFiles)
    {
        if (allowedFiles.Count == 0)
            return false;

        var workspace = Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(workspace, absolutePath).Replace('\\', '/');
        return allowedFiles.Any(pattern =>
            relative.StartsWith(pattern.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    private static int CountLines(string value) => value.Count(c => c == '\n') + 1;

    private static string ToAbsolutePath(string path)
        => Path.IsPathRooted(path) ? path : Path.GetFullPath(path, Directory.GetCurrentDirectory());
}

public sealed record VisualPatchResult
{
    public int AppliedCount { get; init; }
    public IReadOnlyList<string> AppliedFiles { get; init; } = [];
    public IReadOnlyDictionary<string, string> OriginalSnapshots { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public enum VisualHealingOutcome
{
    Graded,
    PassedTarget,
    PatchApplied,
    PatchRejected,
    RolledBack,
    FixGenerationFailed,
    NoFixesGenerated
}

public sealed record VisualHealingIteration
{
    public int Iteration { get; init; }
    public VisualGradeResult? Grade { get; init; }
    public IReadOnlyList<string> PatchesApplied { get; init; } = [];
    public VisualHealingOutcome Outcome { get; init; }
}

public sealed record VisualHealingReport
{
    public IReadOnlyList<VisualHealingIteration> Iterations { get; init; } = [];
    public VisualGradeResult? BestGrade { get; init; }
    public double FinalScore { get; init; }
}
