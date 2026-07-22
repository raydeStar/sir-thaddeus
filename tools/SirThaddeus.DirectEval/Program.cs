using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Tools;
using SirThaddeus.AuditLog;
using SirThaddeus.Config;
using SirThaddeus.LlmClient;
using SirThaddeus.PersonalityEngine;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.DirectEval;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var inputPath = RequirePath(args, "--input");
            var outputPath = RequirePath(args, "--output");
            var toolsDirect = args.Any(value =>
                string.Equals(value, "--tools-direct", StringComparison.OrdinalIgnoreCase));
            var request = JsonSerializer.Deserialize<DirectEvalBatchRequest>(
                await File.ReadAllTextAsync(inputPath).ConfigureAwait(false),
                JsonOptions) ?? throw new InvalidDataException("Direct-eval input is empty.");
            if (request.Items.Count == 0)
                throw new InvalidDataException("Direct-eval input requires at least one item.");
            if (toolsDirect && request.Items.Count != 1)
                throw new InvalidDataException(
                    "The equal-tools control requires exactly one item per process for state isolation.");

            var settings = SettingsManager.Load();
            var personality = new PersonalityRuntime(
                settings.ActivePersonalityId,
                SettingsManager.ResolvePersonalityProfilesDirectory(settings));
            using var llm = new LmStudioClient(RuntimeLlmOptionsFactory.BuildPrimary(settings));
            var results = new List<DirectEvalItemResult>(request.Items.Count);
            var effectiveLocation = settings.GetEffectiveUserLocation(settings.ActiveProfileId);
            var basePrompt = ProductionPromptComposer.ComposeBaseSystemPrompt(
                settings.Llm.SystemPrompt,
                DateTimeOffset.Now,
                effectiveLocation.GetResolvedLabel(),
                effectiveLocation.GetResolvedTimezone(),
                settings.Weather.GetNormalizedUnitSystem());
            var configuredSystemPromptSha256 = HashText(settings.Llm.SystemPrompt);

            RuntimeMcpClientHandle? mcpHandle = null;
            JsonLineAuditLogger? audit = null;
            IReadOnlyList<ToolDefinition> availableTools = [];
            if (toolsDirect)
            {
                var auditPath = Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
                    "same-prompt-tools-direct-audit.jsonl");
                audit = new JsonLineAuditLogger(auditPath);
                mcpHandle = await RuntimeMcpClientFactory.CreateAsync(
                    enableTools: true,
                    allowDegradedStartup: false,
                    overrideServerPath: null,
                    settings,
                    audit,
                    AppContext.BaseDirectory,
                    "SirThaddeus.DirectEval",
                    "1.0",
                    CancellationToken.None).ConfigureAwait(false);
                var builder = new ToolDefinitionBuilder(mcpHandle.Client);
                availableTools = await builder.BuildAsync(
                    settings.Memory.Enabled,
                    settings.RuntimeSafety.PanicMode,
                    settings.RuntimeSafety.SafeMode,
                    logEvent: null,
                    CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                foreach (var item in request.Items)
                {
                    var promptStarted = Stopwatch.GetTimestamp();
                    var messages = ProductionPromptComposer.ApplyPersonality(
                        [ChatMessage.System(basePrompt), ChatMessage.User(item.Prompt)],
                        personality,
                        item.Prompt);
                    var promptBuildMs = Stopwatch.GetElapsedTime(promptStarted).TotalMilliseconds;
                    var callStarted = Stopwatch.GetTimestamp();
                    try
                    {
                        if (toolsDirect)
                        {
                            var filesRoot = settings.DocumentReader.AllowedRoots.SingleOrDefault()
                                ?? throw new InvalidDataException(
                                    "The equal-tools control requires exactly one isolated documentReader.allowedRoots entry.");
                            var state = new DirectEvalState(mcpHandle!.Client, filesRoot);
                            await state.ApplyAsync(item.StateSetup, CancellationToken.None).ConfigureAwait(false);
                            var preflightState = await state.ObserveAsync(
                                item.Observations, CancellationToken.None).ConfigureAwait(false);
                            var preflightPassed = item.PreflightExpectedState is null
                                || JsonElementsEqual(item.PreflightExpectedState.Value, preflightState);
                            if (!preflightPassed)
                                throw new InvalidDataException(
                                    "The isolated state preflight did not match the evaluator-owned expectation.");
                            var loop = new DirectToolLoop(llm, mcpHandle!.Client);
                            var response = await loop.ExecuteAsync(
                                messages,
                                availableTools,
                                item.AllowedTools,
                                Math.Max(1, settings.Llm.MaxTokens),
                                CancellationToken.None).ConfigureAwait(false);
                            var observedState = await state.ObserveAsync(
                                item.Observations, CancellationToken.None).ConfigureAwait(false);
                            results.Add(new DirectEvalItemResult
                            {
                                Id = item.Id,
                                Text = response.Text,
                                PromptBuildMs = promptBuildMs,
                                LatencyMs = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds,
                                CallCount = response.CallCount,
                                PromptTokens = response.PromptTokens,
                                CompletionTokens = response.CompletionTokens,
                                TotalTokens = response.PromptTokens + response.CompletionTokens,
                                ToolCalls = response.ToolCalls,
                                StatePreflight = new DirectStatePreflight(
                                    "process_isolation", preflightPassed, preflightState),
                                ObservedState = observedState,
                                SystemPromptSha256 = HashText(
                                    messages.First(message => message.Role == "system").Content ?? string.Empty),
                                MessagesSha256 = HashText(JsonSerializer.Serialize(messages, JsonOptions)),
                                RuntimeError = response.RuntimeError
                            });
                        }
                        else
                        {
                            var response = await llm.ChatAsync(
                                messages,
                                tools: null,
                                maxTokensOverride: Math.Max(1, settings.Llm.MaxTokens),
                                cancellationToken: CancellationToken.None).ConfigureAwait(false);
                            results.Add(new DirectEvalItemResult
                            {
                                Id = item.Id,
                                Text = response.Content,
                                PromptBuildMs = promptBuildMs,
                                LatencyMs = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds,
                                CallCount = 1,
                                PromptTokens = response.Usage?.PromptTokens,
                                CompletionTokens = response.Usage?.CompletionTokens,
                                TotalTokens = response.Usage?.TotalTokens,
                                SystemPromptSha256 = HashText(
                                    messages.First(message => message.Role == "system").Content ?? string.Empty),
                                MessagesSha256 = HashText(JsonSerializer.Serialize(messages, JsonOptions))
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        results.Add(new DirectEvalItemResult
                        {
                            Id = item.Id,
                            PromptBuildMs = promptBuildMs,
                            LatencyMs = Stopwatch.GetElapsedTime(callStarted).TotalMilliseconds,
                            CallCount = 1,
                            RuntimeError = $"{ex.GetType().Name}: {ex.Message}"
                        });
                    }
                }
            }
            finally
            {
                if (mcpHandle is not null)
                    await mcpHandle.Scope.DisposeAsync().ConfigureAwait(false);
                audit?.Dispose();
            }

            var output = new DirectEvalBatchResult
            {
                Model = settings.Llm.Model,
                MaxOutputTokens = settings.Llm.MaxTokens,
                ContextTokens = settings.Llm.ContextWindowTokens,
                Temperature = settings.Llm.Temperature,
                PersonalityId = settings.ActivePersonalityId,
                PersonalitySha256 = personality.Snapshot.ProfileHash,
                ConfiguredSystemPromptSha256 = configuredSystemPromptSha256,
                SettingsSha256 = HashFile(SettingsManager.GetSettingsPath()),
                Profile = toolsDirect ? "same_prompt_tools_direct" : "production_prompt_no_tools",
                Results = results
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(output, JsonOptions),
                Encoding.UTF8).ConfigureAwait(false);
            return results.Any(result => result.RuntimeError is not null) ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Direct evaluation failed: {ex.Message}");
            return 2;
        }
    }

    private static string RequirePath(string[] args, string option)
    {
        var index = Array.FindIndex(args, value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"Usage: SirThaddeus.DirectEval --input <json> --output <json>. Missing {option}.");
        return Path.GetFullPath(args[index + 1]);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static bool JsonElementsEqual(JsonElement expected, object? actual)
    {
        var expectedNode = JsonNode.Parse(expected.GetRawText());
        var actualNode = JsonSerializer.SerializeToNode(actual, JsonOptions);
        return JsonNode.DeepEquals(expectedNode, actualNode);
    }
}

internal sealed record DirectEvalBatchRequest
{
    public List<DirectEvalItem> Items { get; init; } = [];
}

internal sealed record DirectEvalItem
{
    public required string Id { get; init; }
    public required string Prompt { get; init; }
    public List<string> AllowedTools { get; init; } = [];
    public DirectStateSetup StateSetup { get; init; } = new();
    public JsonElement? PreflightExpectedState { get; init; }
    public List<DirectObservation> Observations { get; init; } = [];
}

internal sealed record DirectEvalBatchResult
{
    public required string Model { get; init; }
    public int MaxOutputTokens { get; init; }
    public int ContextTokens { get; init; }
    public double Temperature { get; init; }
    public required string PersonalityId { get; init; }
    public required string PersonalitySha256 { get; init; }
    public required string ConfiguredSystemPromptSha256 { get; init; }
    public required string SettingsSha256 { get; init; }
    public required string Profile { get; init; }
    public List<DirectEvalItemResult> Results { get; init; } = [];
}

internal sealed record DirectEvalItemResult
{
    public required string Id { get; init; }
    public string? Text { get; init; }
    public double PromptBuildMs { get; init; }
    public double LatencyMs { get; init; }
    public int CallCount { get; init; }
    public int? PromptTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int? TotalTokens { get; init; }
    public IReadOnlyList<DirectToolCallTrace> ToolCalls { get; init; } = [];
    public DirectStatePreflight? StatePreflight { get; init; }
    public Dictionary<string, object>? ObservedState { get; init; }
    public string? SystemPromptSha256 { get; init; }
    public string? MessagesSha256 { get; init; }
    public string? RuntimeError { get; init; }
}

internal sealed record DirectStatePreflight(
    string Mode,
    bool Passed,
    Dictionary<string, object>? ObservedState);
