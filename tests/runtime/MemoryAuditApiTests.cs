using SirThaddeus.Memory;
using Thaddeus.Runtime.Api;

namespace Thaddeus.Runtime.Tests;

public sealed class MemoryAuditApiTests
{
    [Fact]
    public void ApplyFactCorrection_preserves_provenance_and_recomputes_dedupe_key()
    {
        var createdAt = new DateTimeOffset(2026, 5, 11, 10, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(1);
        var existing = new MemoryFact
        {
            MemoryId = "fact-1",
            ProfileId = "user",
            Subject = "user",
            Predicate = "likes",
            Object = "coffee",
            Confidence = 0.62,
            Weight = 0.65,
            Sensitivity = Sensitivity.Public,
            SourceTurnId = "msg-source-1",
            SourceHash = "hash-1",
            DedupeKey = "old-key",
            Origin = "user_auto_extract",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            SourceRef = "conv:thread-memory-source",
        };
        var request = new UpdateFactRequest(" user ", " prefers ", " earl grey ");
        var now = updatedAt.AddMinutes(5);

        var corrected = MemoryAuditApi.ApplyFactCorrection(existing, request, now);

        Assert.Equal("user", corrected.Subject);
        Assert.Equal("prefers", corrected.Predicate);
        Assert.Equal("earl grey", corrected.Object);
        Assert.Equal(Math.Max(existing.Confidence, 0.95), corrected.Confidence);
        Assert.Equal(existing.ProfileId, corrected.ProfileId);
        Assert.Equal(existing.SourceTurnId, corrected.SourceTurnId);
        Assert.Equal(existing.SourceRef, corrected.SourceRef);
        Assert.Equal(existing.Origin, corrected.Origin);
        Assert.Equal(existing.CreatedAt, corrected.CreatedAt);
        Assert.Equal(now, corrected.UpdatedAt);
        Assert.NotEqual(existing.DedupeKey, corrected.DedupeKey);
        Assert.Equal(
            MemoryAuditApi.ComputeHash("user|prefers"),
            corrected.DedupeKey);
    }

    [Fact]
    public void ApplyNuggetCorrection_normalizes_tags_and_preserves_source_metadata()
    {
        var createdAt = new DateTimeOffset(2026, 5, 11, 9, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddMinutes(2);
        var existing = new MemoryNugget
        {
            NuggetId = "nugget-1",
            Text = "User prefers the desk lamp on.",
            Tags = ";routine;lighting;",
            Weight = 0.7,
            PinLevel = 1,
            Sensitivity = NuggetSensitivity.Low,
            SourceTurnId = "msg-source-1",
            SourceHash = "hash-2",
            DedupeKey = "old-key",
            Origin = "user_auto_extract",
            ChunkCitation = "desk lamp on",
            UseCount = 4,
            LastUsedAt = createdAt.AddDays(1),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };
        var request = new UpdateNuggetRequest(
            Text: " User prefers the office lamp dimmed. ",
            Tags: "lighting, routine; lighting ;focus",
            TagsProvided: true);
        var now = updatedAt.AddMinutes(10);

        var corrected = MemoryAuditApi.ApplyNuggetCorrection(existing, request, now);

        Assert.Equal("User prefers the office lamp dimmed.", corrected.Text);
        Assert.Equal(";lighting;routine;focus;", corrected.Tags);
        Assert.Equal(existing.SourceTurnId, corrected.SourceTurnId);
        Assert.Equal(existing.Origin, corrected.Origin);
        Assert.Equal(existing.PinLevel, corrected.PinLevel);
        Assert.Equal(existing.CreatedAt, corrected.CreatedAt);
        Assert.Equal(now, corrected.UpdatedAt);
        Assert.NotEqual(existing.DedupeKey, corrected.DedupeKey);
        Assert.Equal(
            MemoryAuditApi.ComputeHash("user prefers the office lamp dimmed."),
            corrected.DedupeKey);
    }

    [Fact]
    public void NormalizeTags_dedupes_case_insensitively_and_wraps_result()
    {
        var normalized = MemoryAuditApi.NormalizeTags("focus, Routine; focus\nlighting\tROUTINE");

        Assert.Equal(";focus;Routine;lighting;", normalized);
    }
}