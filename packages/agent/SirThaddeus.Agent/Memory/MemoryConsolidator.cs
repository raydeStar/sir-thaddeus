using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SirThaddeus.LlmClient;
using SirThaddeus.Memory;

namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Background service that periodically consolidates memories (facts, events, chunks) 
/// into high-value personal nuggets, requiring exact chunk citations to prevent hallucinations.
/// </summary>
public sealed class MemoryConsolidator : BackgroundService
{
    private readonly IMemoryStore _memoryStore;
    private readonly ILlmClient _llmClient;
    private readonly ILogger<MemoryConsolidator> _logger;
    private readonly MemoryTelemetry? _telemetry;
    private readonly TimeSpan _interval = TimeSpan.FromHours(12);

    public MemoryConsolidator(
        IMemoryStore memoryStore,
        ILlmClient llmClient,
        ILogger<MemoryConsolidator> logger,
        MemoryTelemetry? telemetry = null)
    {
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _telemetry = telemetry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Memory Consolidator started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsolidateAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during memory consolidation.");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task ConsolidateAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting routine memory consolidation...");

        // Strategy: Pull recent facts/events/chunks (e.g. last 100), cluster, and extract nuggets. 
        // For this V1, we'll just pull a batch of recent items to form context, and ask the LLM
        // to produce newly consolidated nuggets with chunk citations.
        
        var (facts, _) = await _memoryStore.ListFactsAsync(null, 0, 50, ct);
        var (events, _) = await _memoryStore.ListEventsAsync(null, 0, 50, ct);
        
        if (facts.Count == 0 && events.Count == 0)
        {
             _logger.LogInformation("No recent memories to consolidate.");
             return;
        }

        var prompt = BuildConsolidationPrompt(facts, events);
        
        var messages = new[]
        {
            ChatMessage.System("You are a memory consolidation expert. Your goal is to review raw records and synthesize overarching personal nuggets about the user."),
            ChatMessage.User(prompt)
        };

        var response = await _llmClient.ChatAsync(messages, tools: null, maxTokensOverride: 1500, cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(response.Content))
            return;

        try
        {
            var result = JsonSerializer.Deserialize<ConsolidationResult>(response.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result?.Nuggets == null) return;

            int produced = 0, rejected = 0;
            foreach (var nugget in result.Nuggets)
            {
                if (string.IsNullOrWhiteSpace(nugget.Text) || string.IsNullOrWhiteSpace(nugget.ChunkCitation))
                {
                    rejected++;
                    continue; // Strict: ignore hallucinated nuggets without citation!
                }

                var dedupeKey = ComputeHash(nugget.Text.Trim().ToLowerInvariant());

                var memNugget = new MemoryNugget
                {
                    NuggetId = Guid.NewGuid().ToString("N"),
                    Text = nugget.Text.Trim(),
                    Tags = string.IsNullOrWhiteSpace(nugget.Tags) ? null : $";{nugget.Tags.Trim().Trim(';')};",
                    Weight = 0.70, // Consolidated nuggets are slightly higher weight
                    PinLevel = 0,
                    Sensitivity = "low",
                    Origin = "service_consolidator",
                    ChunkCitation = nugget.ChunkCitation.Trim(),
                    DedupeKey = dedupeKey,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await _memoryStore.StoreNuggetAsync(memNugget, ct);
                produced++;
            }

            _telemetry?.RecordConsolidationRun(produced, rejected);
            _logger.LogInformation(
                "Consolidation complete: {Produced} nuggets stored, {Rejected} rejected (no citation).",
                produced, rejected);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse consolidation result from LLM.");
        }
    }

    private string BuildConsolidationPrompt(IReadOnlyList<MemoryFact> facts, IReadOnlyList<MemoryEvent> events)
    {
        var context = "RECENT FACTS:\n" + string.Join("\n", facts.Select(f => $"- {f.Subject} {f.Predicate} {f.Object}")) + "\n\n" +
                      "RECENT EVENTS:\n" + string.Join("\n", events.Select(e => $"- {e.Title}: {e.Summary}"));

        return $$"""
        Review the following raw facts and events. Find overarching patterns, preferences, routines, or identity details about the user that can be summarized into a few high-value, generalized "nuggets" (e.g. "User likes practical examples", "User works late on Tuesdays", "User is building a game").
        
        MANDATORY ANTI-HALLUCINATION RULE:
        For every nugget you create, you MUST provide an EXACT substring from the raw facts or events as the "chunk_citation" to prove where you learned it. If you cannot cite it, do not output the nugget.

        Output ONLY valid JSON in this exact structure:
        {
            "nuggets": [
                {
                    "text": "Self-contained sentence summarizing the pattern or detail.",
                    "tags": "identity;preference;routine",
                    "chunk_citation": "Exact substring from the provided text that proves this."
                }
            ]
        }

        RAW MEMORY CONTEXT:
        {{context}}
        """;
    }

    private string ComputeHash(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        return Convert.ToHexString(sha256.ComputeHash(bytes)).ToLowerInvariant();
    }

    private class ConsolidationResult
    {
        public List<ConsolidatedNugget>? Nuggets { get; set; }
    }

    private class ConsolidatedNugget
    {
        public string? Text { get; set; }
        public string? Tags { get; set; }
        public string? ChunkCitation { get; set; }
    }
}
