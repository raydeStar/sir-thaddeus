using SirThaddeus.Agent;
using SirThaddeus.Config;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Scoring;
using SirThaddeus.Harness.Tracing;

namespace SirThaddeus.Harness.Execution;

internal sealed class SingleTestRunner
{
    private readonly SuiteRunContext _context;
    private readonly HarnessArtifactWriter _artifactWriter;
    private readonly ScoringEngine _scoringEngine;
    private readonly CursorJudgeClient _judgeClient;

    public SingleTestRunner(
        SuiteRunContext context,
        HarnessArtifactWriter artifactWriter,
        ScoringEngine scoringEngine,
        CursorJudgeClient judgeClient)
    {
        _context = context;
        _artifactWriter = artifactWriter;
        _scoringEngine = scoringEngine;
        _judgeClient = judgeClient;
    }

    public async Task<SingleRunResult> RunAsync(
        HarnessTestCase test,
        int iteration,
        double? previousBestScore,
        string? previousBestFinal,
        CancellationToken cancellationToken)
    {
        var settings = SettingsManager.Load();
        var artifacts = _artifactWriter.CreatePaths(
            _context.Options.ArtifactsRoot,
            _context.RunId,
            _context.SuiteName,
            test.Id,
            iteration);
        await _context.HeadlessClient.InitializeAsync(cancellationToken);
        var headlessResult = await _context.HeadlessClient.ExecuteAsync(test, cancellationToken);
        var modelName = settings.Llm.Model;

        await _artifactWriter.WriteInputAsync(
            artifacts,
            _context.Options,
            test,
            settings,
            modelName,
            cancellationToken);

        var response = headlessResult.Response;
        var steps = headlessResult.Steps;
        var judgeToolTurns = headlessResult.ToolTurns;

        var preliminary = _scoringEngine.Score(test, response, steps, judgeResult: null);
        var judgePacket = BuildJudgePacket(test, response, judgeToolTurns, preliminary);
        var judgeResult = await _judgeClient.ExecuteAsync(
            _context.Options.JudgeMode,
            judgePacket,
            artifacts.JudgePacketPath,
            artifacts.JudgeResultPath,
            _context.Options.JudgeTimeoutMs,
            _context.Options.JudgeRequired,
            cancellationToken,
            steps: steps);

        var score = _scoringEngine.Score(test, response, steps, judgeResult);

        await _artifactWriter.WriteStepsAsync(artifacts, steps, cancellationToken);
        await _artifactWriter.WriteFinalAsync(artifacts, response.Text, cancellationToken);
        await _artifactWriter.WriteScoreAsync(artifacts, score, cancellationToken);
        await _artifactWriter.WriteDiffAsync(
            artifacts,
            previousBestScore,
            previousBestFinal,
            score.FinalScore,
            response.Text,
            cancellationToken);

        return new SingleRunResult
        {
            Response = response,
            Score = score,
            JudgeResult = judgeResult,
            ArtifactPaths = artifacts,
            Steps = steps,
            ModelName = modelName
        };
    }

    private static CursorJudgePacket BuildJudgePacket(
        HarnessTestCase test,
        AgentResponse response,
        IReadOnlyList<RecordedToolTurn> recordedToolTurns,
        ScoreCard preliminary)
    {
        return new CursorJudgePacket
        {
            TestId = test.Id,
            TestName = test.Name,
            UserMessage = test.UserMessage,
            AllowedTools = test.AllowedTools,
            FinalResponse = response.Text,
            HardFailures = preliminary.HardFailures,
            SoftScore = preliminary.SoftScore,
            MinScore = test.MinScore,
            ToolCalls = recordedToolTurns
                .Select(turn => new ToolCallSnapshot
                {
                    ToolName = turn.ToolName,
                    Arguments = turn.ArgumentsJson,
                    Result = turn.ResultText,
                    Success = turn.Success
                })
                .ToList()
        };
    }

}

internal sealed record HeadlessExecutionResult
{
    public required AgentResponse Response { get; init; }
    public required IReadOnlyList<TraceStep> Steps { get; init; }
    public required IReadOnlyList<RecordedToolTurn> ToolTurns { get; init; }
}

internal sealed record SuiteRunContext
{
    public required HarnessCommandOptions Options { get; init; }
    public required string SuiteName { get; init; }
    public required string RunId { get; init; }
    public required HeadlessRuntimeHarnessClient HeadlessClient { get; init; }
}
