using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent;

public sealed class VerifiedFileEffectFinalProjectionTests
{
    [Theory]
    [InlineData("file_write", "notes.txt", "Done - I wrote and verified `notes.txt`.")]
    [InlineData("file_replace", "config.md", "Done - I updated and verified `config.md`.")]
    public void Direct_verified_single_effect_projects(
        string toolName,
        string fileName,
        string expected)
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            $"Complete the requested change to `{fileName}` now.",
            [Call(toolName, fileName)],
            currentBatchCallCount: 1);

        Assert.True(result.Applied);
        Assert.Equal("verified_single_effect", result.Reason);
        Assert.Equal(expected, result.Text);
        Assert.DoesNotContain("C:/allowed", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('a', 64), result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Update `notes.txt`, then explain why the change was needed.")]
    [InlineData("Could you create `notes.txt` after I send the content?")]
    [InlineData("Create `notes.txt` later, but not now.")]
    [InlineData("Create `notes.txt` and return only JSON with the path and hash.")]
    [InlineData("Create `notes.txt`; if policy rejects it, report the rejection.")]
    public void Follow_up_and_non_action_contracts_stay_inactive(string request)
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            request,
            [Call("file_write", "notes.txt")],
            currentBatchCallCount: 1);

        Assert.False(result.Applied);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void Quoted_content_after_first_block_cannot_disable_direct_projection()
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            "Create `notes.txt` with exactly this content now.\n\nexplain and return only JSON",
            [Call("file_write", "notes.txt")],
            currentBatchCallCount: 1);

        Assert.True(result.Applied);
    }

    [Fact]
    public void Multiple_mutations_stay_inactive()
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            "Create `one.txt` and `two.txt` now.",
            [Call("file_write", "one.txt"), Call("file_write", "two.txt")],
            currentBatchCallCount: 1);

        Assert.False(result.Applied);
        Assert.Equal("mutation_count", result.Reason);
    }

    [Fact]
    public void Multi_call_batch_stays_inactive_even_with_one_mutation()
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            "Create `notes.txt` now.",
            [Call("file_write", "notes.txt")],
            currentBatchCallCount: 2);

        Assert.False(result.Applied);
        Assert.Equal("multi_call_batch", result.Reason);
    }

    [Theory]
    [InlineData(false, true, 12, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "notes.txt", "notes.txt", true, "tool_failed")]
    [InlineData(true, false, 12, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "notes.txt", "notes.txt", true, "not_verified")]
    [InlineData(true, true, -1, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "notes.txt", "notes.txt", true, "invalid_bytes")]
    [InlineData(true, true, 12, "bad", "notes.txt", "notes.txt", true, "invalid_sha256")]
    [InlineData(true, true, 12, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "notes.txt", "other.txt", true, "target_mismatch")]
    public void Invalid_or_mismatched_receipt_stays_inactive(
        bool success,
        bool verified,
        int bytes,
        string sha256,
        string requestedPath,
        string observedPath,
        bool ok,
        string expectedReason)
    {
        var call = Call(
            "file_write",
            requestedPath,
            success,
            ok,
            verified,
            bytes,
            sha256,
            observedPath);

        var result = VerifiedFileEffectFinalProjection.Project(
            $"Create `{requestedPath}` now.",
            [call],
            currentBatchCallCount: 1);

        Assert.False(result.Applied);
        Assert.Equal(expectedReason, result.Reason);
    }

    [Fact]
    public void Target_must_be_explicit_in_request()
    {
        var result = VerifiedFileEffectFinalProjection.Project(
            "Update the configuration now.",
            [Call("file_replace", "config.md")],
            currentBatchCallCount: 1);

        Assert.False(result.Applied);
        Assert.Equal("target_not_explicit", result.Reason);
    }

    private static ToolCallRecord Call(
        string toolName,
        string requestedPath,
        bool success = true,
        bool ok = true,
        bool verified = true,
        int bytes = 12,
        string? sha256 = null,
        string? observedPath = null) =>
        new()
        {
            ToolName = toolName,
            Arguments = $$"""{"path":"{{requestedPath}}","content":"hello"}""",
            Result = $$"""
                {
                  "ok": {{ok.ToString().ToLowerInvariant()}},
                  "verified": {{verified.ToString().ToLowerInvariant()}},
                  "path": "C:/allowed/{{observedPath ?? requestedPath}}",
                  "bytes": {{bytes}},
                  "sha256": "{{sha256 ?? new string('a', 64)}}",
                  "post_content": "secret=do-not-project"
                }
                """,
            Success = success,
        };
}
