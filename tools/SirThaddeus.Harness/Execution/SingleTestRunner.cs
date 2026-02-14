using SirThaddeus.Agent;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.Harness.Artifacts;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Scoring;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Harness.Execution;

public sealed class SingleTestRunner
{
    private readonly SuiteRunContext _context;
    private readonly HarnessArtifactWriter _artifactWriter;
    private readonly FixtureStore _fixtureStore;
    private readonly ScoringEngine _scoringEngine;
    private readonly CursorJudgeClient _judgeClient;

    public SingleTestRunner(
        SuiteRunContext context,
        HarnessArtifactWriter artifactWriter,
        FixtureStore fixtureStore,
        ScoringEngine scoringEngine,
        CursorJudgeClient judgeClient)
    {
        _context = context;
        _artifactWriter = artifactWriter;
        _fixtureStore = fixtureStore;
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
        var traceRecorder = new TraceRecorder();
        var mode = ResolveMode(_context.Options, test);
        var artifacts = _artifactWriter.CreatePaths(
            _context.Options.ArtifactsRoot,
            _context.RunId,
            _context.SuiteName,
            test.Id,
            iteration);

        HarnessFixture? loadedFixture = null;
        RecordingLlmClient? recordingLlmClient = null;
        ILlmClient llmClient;

        IToolExecutor baseExecutor;
        if (mode is HarnessExecutionMode.Replay or HarnessExecutionMode.Stub)
        {
            loadedFixture = await _fixtureStore.LoadAsync(
                _context.Options.FixturesRoot,
                _context.SuiteName,
                test.Id,
                cancellationToken);
        }

        switch (mode)
        {
            case HarnessExecutionMode.Live:
            {
                llmClient = BuildLiveLlmClient(settings, traceRecorder, out recordingLlmClient);
                baseExecutor = new LiveToolExecutor(settings);
                break;
            }
            case HarnessExecutionMode.Replay:
            {
                llmClient = new ReplayLlmClient(loadedFixture!, traceRecorder);
                baseExecutor = new ReplayToolExecutor(loadedFixture!);
                break;
            }
            case HarnessExecutionMode.Stub:
            {
                llmClient = new ReplayLlmClient(loadedFixture!, traceRecorder);
                var replayExecutor = new ReplayToolExecutor(loadedFixture!);
                baseExecutor = new StubToolExecutor(test.Stub, test.AllowedTools, replayExecutor);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported mode: {mode}");
        }

        await using var recordingExecutor = new RecordingToolExecutor(baseExecutor);
        var mcpClient = new ExecutorBackedMcpClient(recordingExecutor, traceRecorder, test.AllowedTools);
        var modelName = await llmClient.GetModelNameAsync(cancellationToken);

        await _artifactWriter.WriteInputAsync(
            artifacts,
            _context.Options,
            test,
            settings,
            modelName,
            cancellationToken);

        var orchestrator = new AgentOrchestrator(
            llmClient,
            mcpClient,
            new TestAuditLogger(),
            settings.Llm.SystemPrompt);

        var response = await orchestrator.ProcessAsync(test.UserMessage, cancellationToken);
        traceRecorder.RecordFinal(response.Text);
        var steps = traceRecorder.Snapshot();

        var preliminary = _scoringEngine.Score(test, response, steps, judgeResult: null);
        var judgePacket = BuildJudgePacket(test, response, recordingExecutor.RecordedTurns, preliminary);
        var judgeResult = await _judgeClient.ExecuteAsync(
            _context.Options.JudgeMode,
            judgePacket,
            artifacts.JudgePacketPath,
            artifacts.JudgeResultPath,
            _context.Options.JudgeTimeoutMs,
            _context.Options.JudgeRequired,
            cancellationToken);

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

        HarnessFixture? capturedFixture = null;
        if (_context.ShouldRecordFixtures && mode == HarnessExecutionMode.Live)
        {
            capturedFixture = BuildFixture(test, settings, modelName, recordingLlmClient, recordingExecutor);
            await _fixtureStore.SaveAsync(_context.Options.FixturesRoot, _context.SuiteName, capturedFixture, cancellationToken);
        }

        return new SingleRunResult
        {
            Response = response,
            Score = score,
            JudgeResult = judgeResult,
            ArtifactPaths = artifacts,
            Steps = steps,
            Fixture = capturedFixture,
            ModelName = modelName
        };
    }

    private static ILlmClient BuildLiveLlmClient(
        AppSettings settings,
        TraceRecorder traceRecorder,
        out RecordingLlmClient recordingClient)
    {
        var options = new LlmClientOptions
        {
            BaseUrl = settings.Llm.BaseUrl,
            Model = settings.Llm.Model,
            MaxTokens = settings.Llm.MaxTokens,
            ContextWindowTokens = settings.Llm.ContextWindowTokens,
            Temperature = settings.Llm.Temperature
        };

        var inner = new LmStudioClient(options);
        recordingClient = new RecordingLlmClient(inner, traceRecorder);
        return recordingClient;
    }

    private static HarnessExecutionMode ResolveMode(HarnessCommandOptions options, HarnessTestCase test)
    {
        if (options.Command is HarnessCommandKind.Record or HarnessCommandKind.Replay or HarnessCommandKind.Smoke)
            return options.Mode;

        if (options.ModeExplicitlySet)
            return options.Mode;

        return test.Mode.Trim().ToLowerInvariant() switch
        {
            "live" => HarnessExecutionMode.Live,
            "replay" => HarnessExecutionMode.Replay,
            "stub" => HarnessExecutionMode.Stub,
            _ => options.Mode
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

    private static HarnessFixture BuildFixture(
        HarnessTestCase test,
        AppSettings settings,
        string? modelName,
        RecordingLlmClient? recordingLlm,
        RecordingToolExecutor recordingExecutor)
    {
        return new HarnessFixture
        {
            TestId = test.Id,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            AvailableTools = recordingExecutor.AvailableTools,
            LlmTurns = recordingLlm?.RecordedTurns ?? [],
            ToolTurns = recordingExecutor.RecordedTurns,
            Metadata = new HarnessFixtureMetadata
            {
                Model = modelName ?? settings.Llm.Model,
                BaseUrl = settings.Llm.BaseUrl,
                Temperature = settings.Llm.Temperature,
                MaxTokens = settings.Llm.MaxTokens
            }
        };
    }
}

public sealed record SuiteRunContext
{
    public required HarnessCommandOptions Options { get; init; }
    public required string SuiteName { get; init; }
    public required string RunId { get; init; }
    public bool ShouldRecordFixtures { get; init; }
}
