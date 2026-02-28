using System.Text.Json;
using SirThaddeus.LlmClient;
using SirThaddeus.Memory;

namespace SirThaddeus.Agent.Memory;

/// <summary>
/// Service responsible for passively extracting long-term memory facts, events, and nuggets
/// from user messages during conversation, running in the background.
/// </summary>
public interface IAutoMemoryExtractor
{
    /// <summary>
    /// Spawns a background task to extract memories from the user's message and store them.
    /// Does not block the main orchestrator flow.
    /// </summary>
    void FireAndForgetExtraction(
        string userMessage,
        string? activeProfileId,
        string turnId);
}

public sealed class AutoMemoryExtractor : IAutoMemoryExtractor
{
    private readonly ILlmClient _llmClient;
    private readonly IMemoryStore _memoryStore;
    private readonly Action<string, string>? _log;
    private readonly MemoryTelemetry? _telemetry;

    public AutoMemoryExtractor(
        ILlmClient llmClient,
        IMemoryStore memoryStore,
        Action<string, string>? log = null,
        MemoryTelemetry? telemetry = null)
    {
        _llmClient = llmClient ?? throw new ArgumentNullException(nameof(llmClient));
        _memoryStore = memoryStore ?? throw new ArgumentNullException(nameof(memoryStore));
        _log = log;
        _telemetry = telemetry;
    }

    public void FireAndForgetExtraction(
        string userMessage,
        string? activeProfileId,
        string turnId)
    {
        if (!MemoryExtractionPolicy.IsEligibleForExtraction("user", userMessage))
        {
            return;
        }

        // Run as a detached background task
        _ = Task.Run(async () =>
        {
            try
            {
                _telemetry?.RecordExtractionAttempt();
                await ExtractAndStoreAsync(userMessage, activeProfileId, turnId);
            }
            catch (Exception ex)
            {
                _telemetry?.RecordExtractionFailure();
                _log?.Invoke("AUTO_MEMORY_EXTRACT_ERROR", ex.Message);
            }
        });
    }

    private async Task ExtractAndStoreAsync(
        string userMessage,
        string? activeProfileId,
        string turnId)
    {
        var prompt = BuildExtractionPrompt(userMessage);

        var messages = new[]
        {
            ChatMessage.System(MemoryExtractionPolicy.ExtractionGuardrails),
            ChatMessage.User(prompt)
        };

        // We use maxTokensOverride to ensure extraction doesn't babble endlessly
        var response = await _llmClient.ChatAsync(messages, tools: null, maxTokensOverride: 1000);
        if (string.IsNullOrWhiteSpace(response.Content))
        {
            return;
        }

        try
        {
            var result = JsonSerializer.Deserialize<ExtractionResult>(response.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (result == null) return;

            // Generate deterministic hashes for the source and chunks
            var sourceHash = ComputeHash(userMessage);

            if (result.Facts != null)
            {
                foreach (var fact in result.Facts)
                {
                    if (string.IsNullOrWhiteSpace(fact.Subject) || string.IsNullOrWhiteSpace(fact.Predicate) || string.IsNullOrWhiteSpace(fact.Object))
                        continue;
                    
                    var dedupeStr = $"{fact.Subject.Trim().ToLowerInvariant()}|{fact.Predicate.Trim().ToLowerInvariant()}";
                    var dedupeKey = ComputeHash(dedupeStr);
                    
                    var memFact = new MemoryFact
                    {
                        MemoryId = Guid.NewGuid().ToString("N"),
                        ProfileId = activeProfileId,
                        Subject = fact.Subject.Trim(),
                        Predicate = fact.Predicate.Trim(),
                        Object = fact.Object.Trim(),
                        Confidence = 0.9,
                        Weight = 0.65,
                        Sensitivity = Sensitivity.Public,
                        SourceTurnId = turnId,
                        SourceHash = sourceHash,
                        DedupeKey = dedupeKey,
                        Origin = "user_auto_extract",
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await _memoryStore.StoreFactAsync(memFact);
                }
            }

            if (result.Events != null)
            {
                foreach (var evt in result.Events)
                {
                    if (string.IsNullOrWhiteSpace(evt.Type) || string.IsNullOrWhiteSpace(evt.Title))
                        continue;
                        
                    var dedupeStr = $"{evt.Type.Trim().ToLowerInvariant()}|{evt.Title.Trim().ToLowerInvariant()}";
                    var dedupeKey = ComputeHash(dedupeStr);
                    
                    var memEvent = new MemoryEvent
                    {
                        EventId = Guid.NewGuid().ToString("N"),
                        ProfileId = activeProfileId,
                        Type = evt.Type.Trim(),
                        Title = evt.Title.Trim(),
                        Summary = evt.Summary?.Trim(),
                        WhenIso = null, // passive extraction usually doesn't have exact datetime parsing unless requested
                        Confidence = 0.9,
                        Weight = 0.65,
                        Sensitivity = Sensitivity.Public,
                        SourceTurnId = turnId,
                        SourceHash = sourceHash,
                        DedupeKey = dedupeKey,
                        Origin = "user_auto_extract",
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow
                    };
                    await _memoryStore.StoreEventAsync(memEvent);
                }
            }

            if (result.Nuggets != null)
            {
                foreach (var nugget in result.Nuggets)
                {
                    if (string.IsNullOrWhiteSpace(nugget.Text))
                        continue;

                    var dedupeKey = ComputeHash(nugget.Text.Trim().ToLowerInvariant());

                    var memNugget = new MemoryNugget
                    {
                        NuggetId = Guid.NewGuid().ToString("N"),
                        Text = nugget.Text.Trim(),
                        Tags = string.IsNullOrWhiteSpace(nugget.Tags) ? null : $";{nugget.Tags.Trim().Trim(';')};",
                        Weight = 0.65,
                        PinLevel = 0,
                        Sensitivity = "low",
                        SourceTurnId = turnId,
                        SourceHash = sourceHash,
                        DedupeKey = dedupeKey,
                        Origin = "user_auto_extract",
                        UseCount = 0,
                        LastUsedAt = null,
                        CreatedAt = DateTimeOffset.UtcNow,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ChunkCitation = nugget.ChunkCitation?.Trim()
                    };
                    await _memoryStore.StoreNuggetAsync(memNugget);
                }
            }

            _telemetry?.RecordExtractionSuccess(
                result.Facts?.Count ?? 0,
                result.Events?.Count ?? 0,
                result.Nuggets?.Count ?? 0);
        }
        catch (JsonException ex)
        {
            _log?.Invoke("AUTO_MEMORY_EXTRACT_JSON_ERROR", ex.Message);
        }
    }

    private string ComputeHash(string input)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(input);
        var hashBytes = sha256.ComputeHash(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private string BuildExtractionPrompt(string userMessage)
    {
        return $$"""
        You are a background memory extraction service. 
        Analyze the following recent user message and extract long-term facts, events, and personal nuggets.
        If there is nothing worth remembering long-term (e.g. general questions, greetings, transient debugging info), return empty arrays.
        Output ONLY valid JSON.

        {
            "facts": [
                {
                    "subject": "user name or entity",
                    "predicate": "relationship or attribute (e.g. 'likes', 'works on')",
                    "object": "target value"
                }
            ],
            "events": [
                {
                    "type": "event type (e.g. 'meeting', 'milestone', 'life_event')",
                    "title": "short title",
                    "summary": "longer summary of the event"
                }
            ],
            "nuggets": [
                {
                    "text": "Self-contained sentence describing a user preference, routine, or identity detail.",
                    "tags": "identity;preference;routine",
                    "chunk_citation": "Exact substring from the user message that proves this nugget (mandatory to prevent hallucination)"
                }
            ]
        }

        USER MESSAGE:
        {{userMessage}}
        """;
    }

    private class ExtractionResult
    {
        public List<FactDto>? Facts { get; set; }
        public List<EventDto>? Events { get; set; }
        public List<NuggetDto>? Nuggets { get; set; }
    }

    private class FactDto
    {
        public string? Subject { get; set; }
        public string? Predicate { get; set; }
        public string? Object { get; set; }
    }

    private class EventDto
    {
        public string? Type { get; set; }
        public string? Title { get; set; }
        public string? Summary { get; set; }
    }

    private class NuggetDto
    {
        public string? Text { get; set; }
        public string? Tags { get; set; }
        public string? ChunkCitation { get; set; }
    }
}
