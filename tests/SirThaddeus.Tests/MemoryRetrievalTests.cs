using SirThaddeus.Memory;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Memory Retrieval Tests
//
// Covers: intent classification, relevance gating, scoring, cosine
// similarity, dedupe, caps, and a golden test for MemoryPack formatting.
//
// Uses an in-memory FakeMemoryStore to avoid any SQLite dependency.
// ─────────────────────────────────────────────────────────────────────────

#region ── Intent Classification (rules-based) ────────────────────────────

public class IntentClassifierTests
{
    [Theory]
    [InlineData("How do I fix this null reference exception?", MemoryIntent.Technical)]
    [InlineData("debug the build error in pipeline",          MemoryIntent.Technical)]
    [InlineData("I feel so anxious about tomorrow",           MemoryIntent.Personal)]
    [InlineData("my relationship with my family",             MemoryIntent.Personal)]
    [InlineData("schedule a meeting for next week",           MemoryIntent.Planning)]
    [InlineData("remind me to buy groceries tomorrow",        MemoryIntent.Planning)]
    [InlineData("hello how are you",                          MemoryIntent.General)]
    [InlineData("tell me something interesting",              MemoryIntent.General)]
    [InlineData("",                                           MemoryIntent.General)]
    public void ClassifiesIntent_FromKeywords(string query, MemoryIntent expected)
    {
        var result = IntentClassifier.Classify(query);
        Assert.Equal(expected, result);
    }
}

#endregion

#region ── Relevance Gate ─────────────────────────────────────────────────

public class RelevanceGateTests
{
    [Fact]
    public void SecretItems_AlwaysBlocked()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Secret, MemoryIntent.Personal,
            lexicalScore: 1.0, similarityScore: 1.0);

        Assert.Equal(RelevanceDecision.Block, result);
    }

    [Fact]
    public void PublicItems_AlwaysAllowed()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Public, MemoryIntent.Technical,
            lexicalScore: 0.1, similarityScore: 0.0);

        Assert.Equal(RelevanceDecision.Allow, result);
    }

    [Fact]
    public void PersonalItems_BlockedDuringTechnical_WhenLowRelevance()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Personal, MemoryIntent.Technical,
            lexicalScore: 0.2, similarityScore: 0.1);

        Assert.Equal(RelevanceDecision.Block, result);
    }

    [Fact]
    public void PersonalItems_AllowedDuringTechnical_WhenHighLexical()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Personal, MemoryIntent.Technical,
            lexicalScore: 0.70, similarityScore: 0.0);

        Assert.Equal(RelevanceDecision.Allow, result);
    }

    [Fact]
    public void PersonalItems_AllowedDuringTechnical_WhenHighSimilarity()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Personal, MemoryIntent.Technical,
            lexicalScore: 0.1, similarityScore: 0.85);

        Assert.Equal(RelevanceDecision.Allow, result);
    }

    [Fact]
    public void PersonalItems_AllowedDuringPersonalQueries()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Personal, MemoryIntent.Personal,
            lexicalScore: 0.3, similarityScore: 0.0);

        Assert.Equal(RelevanceDecision.Allow, result);
    }

    [Fact]
    public void PersonalItems_AllowSilent_DuringGeneral_WhenLowRelevance()
    {
        var result = RelevanceGate.Evaluate(
            Sensitivity.Personal, MemoryIntent.General,
            lexicalScore: 0.3, similarityScore: 0.3);

        Assert.Equal(RelevanceDecision.AllowSilent, result);
    }
}

#endregion

#region ── Scoring ────────────────────────────────────────────────────────

public class ScoringTests
{
    [Fact]
    public void RecencyScore_FullForRecentItems()
    {
        var recent = DateTimeOffset.UtcNow.AddDays(-2);
        var score  = Scoring.ComputeRecency(recent);
        Assert.Equal(1.0, score);
    }

    [Fact]
    public void RecencyScore_ZeroForOldItems()
    {
        var old   = DateTimeOffset.UtcNow.AddDays(-100);
        var score = Scoring.ComputeRecency(old);
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void RecencyScore_DecaysBetween()
    {
        var midAge = DateTimeOffset.UtcNow.AddDays(-48);
        var score  = Scoring.ComputeRecency(midAge);
        Assert.InRange(score, 0.01, 0.99);
    }

    [Fact]
    public void RecencyScore_NullTimestamp_ReturnsZero()
    {
        Assert.Equal(0.0, Scoring.ComputeRecency(null));
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_ReturnsOne()
    {
        float[] a = [1f, 2f, 3f];
        float[] b = [1f, 2f, 3f];
        var sim = Scoring.CosineSimilarity(a, b);
        Assert.InRange(sim, 0.99, 1.01);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_ReturnsZero()
    {
        float[] a = [1f, 0f];
        float[] b = [0f, 1f];
        var sim = Scoring.CosineSimilarity(a, b);
        Assert.InRange(sim, -0.01, 0.01);
    }

    [Fact]
    public void CosineSimilarity_NullInput_ReturnsZero()
    {
        Assert.Equal(0.0, Scoring.CosineSimilarity(null, [1f, 2f]));
        Assert.Equal(0.0, Scoring.CosineSimilarity([1f, 2f], null));
    }

    [Fact]
    public void CosineSimilarity_MismatchedDimensions_ReturnsZero()
    {
        Assert.Equal(0.0, Scoring.CosineSimilarity([1f, 2f], [1f, 2f, 3f]));
    }

    [Fact]
    public void FactScore_CombinesWeightsCorrectly()
    {
        // lex=1.0, recency=1.0, conf=1.0 → max possible score
        var score = Scoring.ScoreFactOrEvent(1.0, 1.0, 1.0);
        Assert.Equal(1.0, score, precision: 4);
    }

    [Fact]
    public void ChunkScore_CombinesWeightsCorrectly()
    {
        var score = Scoring.ScoreChunk(1.0, 1.0);
        Assert.Equal(1.0, score, precision: 4);
    }
}

#endregion

#region ── Retriever Pipeline ─────────────────────────────────────────────

public class MemoryRetrieverTests
{
    [Fact]
    public async Task EmptyQuery_ReturnsEmptyPack()
    {
        var store     = new FakeMemoryStore();
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("   ");

        Assert.False(pack.HasContent);
        Assert.Equal("", pack.PackText);
        Assert.Contains("Empty query", pack.Notes);
    }

    [Fact]
    public async Task NoCandidates_ReturnsEmptyPack()
    {
        var store     = new FakeMemoryStore();
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("something with no matches");

        Assert.False(pack.HasContent);
        Assert.Contains("No relevant memory", pack.Notes);
    }

    [Fact]
    public async Task Caps_MaxFacts()
    {
        // Return more facts than the injection cap
        var store = new FakeMemoryStore
        {
            FactsToReturn = Enumerable.Range(0, 20).Select(i =>
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId  = $"f{i}",
                        Subject   = "user",
                        Predicate = "knows",
                        Object    = $"fact-{i}",
                        Sensitivity = Sensitivity.Public
                    }, 0.8))
                .ToList()
        };
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("knows");

        Assert.True(pack.Facts.Count <= Thresholds.FactsInject);
    }

    [Fact]
    public async Task Caps_MaxEvents()
    {
        var store = new FakeMemoryStore
        {
            EventsToReturn = Enumerable.Range(0, 20).Select(i =>
                new StoreCandidate<MemoryEvent>(
                    new MemoryEvent
                    {
                        EventId = $"e{i}",
                        Type    = "meeting",
                        Title   = $"meeting-{i}",
                        Sensitivity = Sensitivity.Public
                    }, 0.8))
                .ToList()
        };
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("meeting");

        Assert.True(pack.Events.Count <= Thresholds.EventsInject);
    }

    [Fact]
    public async Task Caps_MaxChunks()
    {
        var store = new FakeMemoryStore
        {
            ChunksToReturn = Enumerable.Range(0, 25).Select(i =>
                new StoreCandidate<MemoryChunk>(
                    new MemoryChunk
                    {
                        ChunkId    = $"c{i}",
                        SourceType = "doc",
                        Text       = $"chunk text {i}",
                        Sensitivity = Sensitivity.Public
                    }, 0.8))
                .ToList()
        };
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("chunk text");

        Assert.True(pack.Chunks.Count <= Thresholds.ChunksInject);
    }

    [Fact]
    public async Task Dedupe_SameSourceRef_KeepsFirst()
    {
        var store = new FakeMemoryStore
        {
            FactsToReturn =
            [
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId = "f1", Subject = "user", Predicate = "likes",
                        Object = "coffee", SourceRef = "conv-001",
                        Sensitivity = Sensitivity.Public
                    }, 0.9),
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId = "f2", Subject = "user", Predicate = "likes",
                        Object = "espresso", SourceRef = "conv-001",
                        Sensitivity = Sensitivity.Public
                    }, 0.8)
            ]
        };
        var retriever = new MemoryRetriever(store);

        var pack = await retriever.BuildMemoryPackAsync("coffee likes");

        // Both facts share source_ref "conv-001" → only the first (higher score) remains
        Assert.Single(pack.Facts);
        Assert.Equal("coffee", pack.Facts[0].Object);
    }

    [Fact]
    public async Task Filter_PersonalBlocked_DuringTechnical()
    {
        var store = new FakeMemoryStore
        {
            FactsToReturn =
            [
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId = "f1", Subject = "user", Predicate = "feels",
                        Object = "anxious", Sensitivity = Sensitivity.Personal
                    }, 0.3),
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId = "f2", Subject = "project", Predicate = "uses",
                        Object = "csharp", Sensitivity = Sensitivity.Public
                    }, 0.6)
            ]
        };
        var retriever = new MemoryRetriever(store);

        // "code debug" triggers Technical intent
        var pack = await retriever.BuildMemoryPackAsync("code debug");

        // Public fact should be present, personal fact should be blocked
        Assert.DoesNotContain(pack.Facts, f => f.Object == "anxious");
        Assert.Contains(pack.Facts, f => f.Object == "csharp");
    }

    [Fact]
    public async Task GoldenTest_FixedCandidates_ProducesExpectedPackText()
    {
        var now = new DateTimeOffset(2026, 2, 6, 12, 0, 0, TimeSpan.Zero);

        var store = new FakeMemoryStore
        {
            FactsToReturn =
            [
                new StoreCandidate<MemoryFact>(
                    new MemoryFact
                    {
                        MemoryId = "f1", Subject = "user",
                        Predicate = "prefers", Object = "dark mode",
                        Confidence = 0.95, Sensitivity = Sensitivity.Public,
                        UpdatedAt = now.AddDays(-1), SourceRef = "conv-42"
                    }, 0.9)
            ],
            EventsToReturn =
            [
                new StoreCandidate<MemoryEvent>(
                    new MemoryEvent
                    {
                        EventId = "e1", Type = "deadline",
                        Title = "Project Alpha launch",
                        Summary = "Beta freeze Feb 10",
                        WhenIso = now.AddDays(4),
                        Confidence = 1.0, Sensitivity = Sensitivity.Public,
                        SourceRef = "conv-50"
                    }, 0.85)
            ],
            ChunksToReturn =
            [
                new StoreCandidate<MemoryChunk>(
                    new MemoryChunk
                    {
                        ChunkId = "c1", SourceType = "conversation",
                        Text = "We agreed on a dark theme with blue accents.",
                        WhenIso = now.AddDays(-2),
                        Sensitivity = Sensitivity.Public,
                        SourceRef = "conv-42"
                    }, 0.75)
            ]
        };

        var retriever = new MemoryRetriever(store);
        var pack = await retriever.BuildMemoryPackAsync("dark mode preferences");

        Assert.True(pack.HasContent);
        Assert.Contains("[MEMORY CONTEXT]", pack.PackText);
        Assert.Contains("[/MEMORY CONTEXT]", pack.PackText);
        Assert.Contains("user prefers dark mode", pack.PackText);
        Assert.Contains("deadline: Project Alpha launch", pack.PackText);
        Assert.Contains("dark theme with blue accents", pack.PackText);
        Assert.Contains("CITATIONS:", pack.PackText);
        Assert.Contains("conv-42", pack.PackText);
        Assert.Contains("conv-50", pack.PackText);
    }
}

#endregion

#region ── Test Doubles ───────────────────────────────────────────────────

/// <summary>
/// In-memory IMemoryStore that returns pre-configured candidates.
/// No SQLite required.
/// </summary>
internal sealed class FakeMemoryStore : IMemoryStore
{
    public List<StoreCandidate<MemoryFact>>  FactsToReturn  { get; set; } = [];
    public List<StoreCandidate<MemoryEvent>> EventsToReturn { get; set; } = [];
    public List<StoreCandidate<MemoryChunk>> ChunksToReturn { get; set; } = [];

    public Task<IReadOnlyList<StoreCandidate<MemoryFact>>> SearchFactsAsync(
        string query, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoreCandidate<MemoryFact>>>(
            FactsToReturn.Take(maxResults).ToList());

    public Task<IReadOnlyList<StoreCandidate<MemoryEvent>>> SearchEventsAsync(
        string query, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoreCandidate<MemoryEvent>>>(
            EventsToReturn.Take(maxResults).ToList());

    public Task<IReadOnlyList<StoreCandidate<MemoryChunk>>> SearchChunksAsync(
        string query, int maxResults, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<StoreCandidate<MemoryChunk>>>(
            ChunksToReturn.Take(maxResults).ToList());

    public List<MemoryFact>  StoredFacts  { get; } = [];
    public List<MemoryEvent> StoredEvents { get; } = [];
    public List<MemoryChunk> StoredChunks { get; } = [];

    public Task StoreFactAsync(MemoryFact fact, CancellationToken ct = default)
    { StoredFacts.Add(fact); return Task.CompletedTask; }

    public Task StoreEventAsync(MemoryEvent evt, CancellationToken ct = default)
    { StoredEvents.Add(evt); return Task.CompletedTask; }

    public Task StoreChunkAsync(MemoryChunk chunk, CancellationToken ct = default)
    { StoredChunks.Add(chunk); return Task.CompletedTask; }

    // ── Browse stubs (not exercised by retrieval tests) ────────────
    public Task<(IReadOnlyList<MemoryFact> Items, int TotalCount)> ListFactsAsync(
        string? filter, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<MemoryFact>, int)>(([], 0));

    public Task<(IReadOnlyList<MemoryEvent> Items, int TotalCount)> ListEventsAsync(
        string? filter, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<MemoryEvent>, int)>(([], 0));

    public Task<(IReadOnlyList<MemoryChunk> Items, int TotalCount)> ListChunksAsync(
        string? filter, int skip, int take, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<MemoryChunk>, int)>(([], 0));

    // ── Delete stubs ───────────────────────────────────────────────
    public List<string> DeletedFactIds  { get; } = [];
    public List<string> DeletedEventIds { get; } = [];
    public List<string> DeletedChunkIds { get; } = [];

    public Task DeleteFactAsync(string memoryId, CancellationToken ct = default)
    { DeletedFactIds.Add(memoryId); return Task.CompletedTask; }

    public Task DeleteEventAsync(string eventId, CancellationToken ct = default)
    { DeletedEventIds.Add(eventId); return Task.CompletedTask; }

    public Task DeleteChunkAsync(string chunkId, CancellationToken ct = default)
    { DeletedChunkIds.Add(chunkId); return Task.CompletedTask; }

    public Task EnsureSchemaAsync(CancellationToken ct = default) =>
        Task.CompletedTask;
}

#endregion
