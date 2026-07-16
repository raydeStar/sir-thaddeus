using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;
using SirThaddeus.Harness.Tracing;
using SirThaddeus.PersonalityEngine.Profiles;

namespace SirThaddeus.Harness.Artifacts;

public sealed class HarnessArtifactWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions DiagnosticsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ArtifactPaths CreatePaths(
        string artifactsRoot,
        string runId,
        string suiteName,
        string testId,
        int iteration)
    {
        var rooted = Path.IsPathRooted(artifactsRoot)
            ? artifactsRoot
            : Path.GetFullPath(artifactsRoot, Directory.GetCurrentDirectory());

        var iterationName = $"iter-{iteration:D2}";
        var root = Path.Combine(rooted, runId, suiteName, testId, iterationName);
        Directory.CreateDirectory(root);

        return new ArtifactPaths
        {
            RootDirectory = root,
            InputJsonPath = Path.Combine(root, "input.json"),
            StepsJsonlPath = Path.Combine(root, "steps.jsonl"),
            ObservationsJsonPath = Path.Combine(root, "observations.json"),
            DiagnosticsJsonPath = Path.Combine(root, "diagnostics.json"),
            FinalTextPath = Path.Combine(root, "final.txt"),
            ScoreJsonPath = Path.Combine(root, "score.json"),
            DiffMarkdownPath = Path.Combine(root, "diff.md"),
            JudgePacketPath = Path.Combine(root, "judge_packet.json"),
            JudgeResultPath = Path.Combine(root, "judge_result.json")
        };
    }

    public async Task WriteInputAsync(
        ArtifactPaths paths,
        HarnessCommandOptions options,
        HarnessTestCase test,
        AppSettings settings,
        string? modelName,
        CancellationToken cancellationToken)
    {
        var personalityHash = ComputeActivePersonalityHash(settings);

        var payload = new
        {
            run = new
            {
                command = options.Command.ToString().ToLowerInvariant(),
                mode = options.Mode.ToString().ToLowerInvariant(),
                judge = options.JudgeMode.ToString().ToLowerInvariant(),
                max_iters = options.MaxIterations,
                min_score_override = options.MinScoreOverride
            },
            test = new
            {
                id = test.Id,
                name = test.Name,
                user_message = test.UserMessage,
                allowed_tools = test.AllowedTools,
                state_setup = test.StateSetup,
                observations = test.Observations,
                min_score = test.MinScore
            },
            model_params = new
            {
                model = modelName ?? settings.Llm.Model,
                base_url = settings.Llm.BaseUrl,
                temperature = settings.Llm.Temperature,
                max_tokens = settings.Llm.MaxTokens,
                context_window_tokens = settings.Llm.ContextWindowTokens
            },
            config_hashes = new
            {
                system_prompt = ComputeHash(settings.Llm.SystemPrompt),
                personality = personalityHash,
                router = ComputeFileHash(Path.Combine("packages", "agent", "SirThaddeus.Agent", "Routing", "DefaultRouter.cs")),
                policy = ComputeFileHash(Path.Combine("packages", "agent", "SirThaddeus.Agent", "PolicyGate.cs"))
            }
        };

        await File.WriteAllTextAsync(
            paths.InputJsonPath,
            JsonSerializer.Serialize(payload, JsonOptions),
            cancellationToken);
    }

    public async Task WriteStepsAsync(
        ArtifactPaths paths,
        IReadOnlyList<TraceStep> steps,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(paths.StepsJsonlPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        foreach (var step in steps)
        {
            var line = JsonSerializer.Serialize(step);
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
        }
    }

    public Task WriteFinalAsync(ArtifactPaths paths, string finalResponse, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(paths.FinalTextPath, finalResponse ?? "", cancellationToken);

    public Task WriteObservationsAsync(
        ArtifactPaths paths,
        JsonElement observedState,
        CancellationToken cancellationToken)
        => File.WriteAllTextAsync(
            paths.ObservationsJsonPath,
            JsonSerializer.Serialize(observedState, JsonOptions),
            cancellationToken);

    public Task WriteDiagnosticsAsync(
        ArtifactPaths paths,
        HarnessRuntimeDiagnostics diagnostics,
        CancellationToken cancellationToken)
        => File.WriteAllTextAsync(
            paths.DiagnosticsJsonPath,
            JsonSerializer.Serialize(diagnostics, DiagnosticsJsonOptions),
            cancellationToken);

    public Task WriteScoreAsync(ArtifactPaths paths, ScoreCard score, CancellationToken cancellationToken)
        => File.WriteAllTextAsync(paths.ScoreJsonPath, JsonSerializer.Serialize(score, JsonOptions), cancellationToken);

    public Task WriteDiffAsync(
        ArtifactPaths paths,
        double? previousScore,
        string? previousFinal,
        double currentScore,
        string currentFinal,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine("## Score Delta");
        builder.AppendLine();
        builder.AppendLine($"- Previous: {(previousScore?.ToString("0.00") ?? "n/a")}");
        builder.AppendLine($"- Current: {currentScore:0.00}");
        builder.AppendLine();
        builder.AppendLine("## Previous Final");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(previousFinal ?? "(none)");
        builder.AppendLine("```");
        builder.AppendLine();
        builder.AppendLine("## Current Final");
        builder.AppendLine();
        builder.AppendLine("```");
        builder.AppendLine(currentFinal ?? "");
        builder.AppendLine("```");

        return File.WriteAllTextAsync(paths.DiffMarkdownPath, builder.ToString(), cancellationToken);
    }

    private static string ComputeFileHash(string relativePath)
    {
        var path = Path.GetFullPath(relativePath, Directory.GetCurrentDirectory());
        if (!File.Exists(path))
            return "missing";

        var content = File.ReadAllText(path);
        return ComputeHash(content);
    }

    private static string ComputeHash(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "empty";

        var bytes = Encoding.UTF8.GetBytes(value);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeActivePersonalityHash(AppSettings settings)
    {
        try
        {
            var profilesDirectory = SettingsManager.ResolvePersonalityProfilesDirectory(settings);
            var store = new PersonalityProfileStore();
            store.EnsureBuiltInsInstalled(profilesDirectory);
            var active = store.LoadActive(profilesDirectory, settings.ActivePersonalityId);
            return active.Hash;
        }
        catch
        {
            return "unavailable";
        }
    }
}
