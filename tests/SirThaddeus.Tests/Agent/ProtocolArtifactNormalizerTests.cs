using global::SirThaddeus.Agent;
using global::SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent;

public sealed class ProtocolArtifactNormalizerTests
{
    private static readonly WikiMutationTarget SelectedStatusUpdate = new(
        WikiMutationTargetKind.Page,
        RootId: "root-1",
        RootName: "Work",
        PageId: "page-1",
        PageTitle: "Status",
        Operation: WikiMutationOperation.PageUpdate);

    [Fact]
    public void Strips_malformed_channel_markers_and_preserves_body()
    {
        var result = ProtocolArtifactNormalizer.Normalize(
            "<|channel>thought\n<channel|>KITE-348",
            target: null,
            toolCalls: []);

        Assert.True(result.Applied);
        Assert.Equal("channel-markers-stripped", result.Reason);
        Assert.Equal("KITE-348", result.Text);
    }

    [Fact]
    public void Builds_receipt_for_protocol_only_draft_with_verified_selected_update()
    {
        var result = ProtocolArtifactNormalizer.Normalize(
            "<|tool_call>_call:file.write{path: \"Status.md\"}<tool_call|>",
            SelectedStatusUpdate,
            [VerifiedUpdate()]);

        Assert.True(result.Applied);
        Assert.Equal("verified-wiki-page-update-receipt", result.Reason);
        Assert.Equal("Updated the selected Wiki page.", result.Text);
    }

    [Fact]
    public void Leaves_protocol_artifact_unchanged_without_typed_target()
    {
        const string leaked = "<|tool_call>_call:file.write{}<tool_call|>";

        var result = ProtocolArtifactNormalizer.Normalize(leaked, null, [VerifiedUpdate()]);

        Assert.False(result.Applied);
        Assert.Equal(leaked, result.Text);
    }

    [Fact]
    public void Leaves_protocol_artifact_unchanged_when_persisted_content_differs()
    {
        const string leaked = "<|tool_call>_call:file.write{}<tool_call|>";
        var call = VerifiedUpdate() with
        {
            Result = """
                {"ok":true,"document":{"page":{"title":"Status","version":2},"markdown":"DIFFERENT"}}
                """
        };

        var result = ProtocolArtifactNormalizer.Normalize(leaked, SelectedStatusUpdate, [call]);

        Assert.False(result.Applied);
        Assert.Equal("protocol-artifact-without-proof", result.Reason);
    }

    [Fact]
    public void Leaves_protocol_artifact_unchanged_when_updates_are_ambiguous()
    {
        const string leaked = "<|tool_call>_call:file.write{}<tool_call|>";

        var result = ProtocolArtifactNormalizer.Normalize(
            leaked,
            SelectedStatusUpdate,
            [VerifiedUpdate(), VerifiedUpdate()]);

        Assert.False(result.Applied);
        Assert.Equal(leaked, result.Text);
    }

    [Fact]
    public void Leaves_failed_update_unchanged()
    {
        const string leaked = "<|tool_call>_call:file.write{}<tool_call|>";
        var failed = VerifiedUpdate() with { Success = false };

        var result = ProtocolArtifactNormalizer.Normalize(leaked, SelectedStatusUpdate, [failed]);

        Assert.False(result.Applied);
        Assert.Equal(leaked, result.Text);
    }

    [Theory]
    [InlineData("The selected page was updated.")]
    [InlineData("Here is the literal token `<|channel>thought<channel|>` you requested.")]
    [InlineData("```text\n<|channel>thought<channel|>\n```")]
    public void Leaves_ordinary_or_quoted_text_unchanged(string text)
    {
        var result = ProtocolArtifactNormalizer.Normalize(text, SelectedStatusUpdate, [VerifiedUpdate()]);

        Assert.False(result.Applied);
        Assert.Equal(text, result.Text);
    }

    private static ToolCallRecord VerifiedUpdate() => new()
    {
        ToolName = "wiki_page_update_by_name",
        Arguments = """
            {"rootName":"Work","pageTitle":"Status","markdown":"READY"}
            """,
        Result = """
            {"ok":true,"document":{"page":{"title":"Status","version":2},"markdown":"READY"}}
            """,
        Success = true,
    };
}
