using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Iteration;

public sealed class AutoIterationEngine
{
    private readonly WorkspacePatchApplier _patchApplier;

    public AutoIterationEngine(WorkspacePatchApplier patchApplier)
    {
        _patchApplier = patchApplier;
    }

    public async Task<IReadOnlyList<TestAttemptResult>> ExecuteAsync(
        HarnessCommandOptions options,
        HarnessTestCase test,
        Func<int, Task<TestAttemptResult>> runIterationAsync,
        CancellationToken cancellationToken)
    {
        var attempts = new List<TestAttemptResult>();

        var baseline = await runIterationAsync(1);
        attempts.Add(baseline);

        var best = baseline;
        var minScore = options.MinScoreOverride ?? test.MinScore;
        if (baseline.Score.FinalScore >= minScore || options.MaxIterations <= 1)
            return attempts;
        if (!options.AllowWorkspaceEdits)
            return attempts;

        for (var iteration = 2; iteration <= options.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var allowedTargets = ResolveTargets(test.PatchTargets, iteration, best.JudgeResult);
            var suggestions = best.JudgeResult?.Patches ?? [];
            var patchResult = _patchApplier.Apply(
                suggestions,
                allowedTargets,
                options.PatchBudgetFiles,
                options.PatchBudgetLines);

            if (patchResult.AppliedCount == 0)
                break;

            var attempt = await runIterationAsync(iteration);
            attempts.Add(attempt with { AppliedPatches = patchResult.ChangedFiles });

            if (attempt.Score.FinalScore > best.Score.FinalScore)
            {
                best = attempt;
                if (best.Score.FinalScore >= minScore)
                    break;
            }
            else
            {
                _patchApplier.Rollback(patchResult);
                break;
            }
        }

        return attempts;
    }

    private static IReadOnlyList<string> ResolveTargets(
        HarnessPatchTargets targets,
        int iteration,
        CursorJudgeResult? judgeResult)
    {
        if (iteration <= 2 && targets.Tier1Targets.Count > 0)
            return targets.Tier1Targets;
        if (iteration >= 3 && targets.Tier2Targets.Count > 0)
            return targets.Tier2Targets;
        if (iteration >= 4 && targets.Tier3Targets.Count > 0 && HasLogicDefectSignal(judgeResult))
            return targets.Tier3Targets;

        if (iteration <= 2)
        {
            return
            [
                "packages/agent/SirThaddeus.Agent/Routing/",
                "packages/agent/SirThaddeus.Agent/PolicyGate.cs",
                "packages/config/SirThaddeus.Config/"
            ];
        }

        if (iteration >= 3)
        {
            return
            [
                "packages/agent/SirThaddeus.Agent/",
                "tools/SirThaddeus.Harness/"
            ];
        }

        if (HasLogicDefectSignal(judgeResult))
        {
            return
            [
                "packages/",
                "apps/",
                "tools/"
            ];
        }

        return
        [
            "packages/agent/SirThaddeus.Agent/",
            "tools/SirThaddeus.Harness/"
        ];
    }

    private static bool HasLogicDefectSignal(CursorJudgeResult? judgeResult)
    {
        if (judgeResult is null)
            return false;

        var text = string.Join(" ", judgeResult.Reasons.Concat(judgeResult.Suggestions));
        return text.Contains("logic", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("defect", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("bug", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record TestAttemptResult
{
    public int Iteration { get; init; }
    public required ScoreCard Score { get; init; }
    public required string FinalResponse { get; init; }
    public required string ArtifactDirectory { get; init; }
    public required CursorJudgeResult? JudgeResult { get; init; }
    public IReadOnlyList<string> AppliedPatches { get; init; } = [];
}
