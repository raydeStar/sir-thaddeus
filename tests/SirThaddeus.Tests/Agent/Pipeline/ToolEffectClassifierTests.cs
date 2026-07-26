using SirThaddeus.Agent.Pipeline;

namespace SirThaddeus.Tests.Agent.Pipeline;

public sealed class ToolEffectClassifierTests
{
    [Fact]
    public void Wiki_update_is_local_mutating_and_revision_reversible()
    {
        var effect = ToolEffectClassifier.Describe(
            "wiki_page_update",
            """{"page_id":"page-7","markdown":"updated"}""");

        Assert.Equal("update", effect.Kind);
        Assert.True(effect.Mutating);
        Assert.True(effect.Reversible);
        Assert.Equal("local", effect.Boundary);
        Assert.Equal("page-7", effect.Target);
        Assert.Equal("wiki-revision", effect.UndoStrategy);
        Assert.Equal("WikiWrite", effect.Capability);
    }

    [Fact]
    public void Web_search_is_read_only_and_crosses_web_boundary()
    {
        var effect = ToolEffectClassifier.Describe(
            "web_search",
            """{"query":"release notes"}""");

        Assert.Equal("read", effect.Kind);
        Assert.False(effect.Mutating);
        Assert.False(effect.Reversible);
        Assert.Equal("web", effect.Boundary);
        Assert.Equal("release notes", effect.Target);
    }

    [Fact]
    public void Successful_wiki_result_is_only_verified_with_versioned_state_evidence()
    {
        var effect = ToolEffectClassifier.Describe(
            "wiki_page_update",
            """{"page_id":"page-7"}""");

        var verified = ToolEffectClassifier.Complete(
            effect,
            "wiki_page_update",
            true,
            """{"document":{"page":{"id":"page-7","version":2}}}""");
        var unverified = ToolEffectClassifier.Complete(
            effect,
            "wiki_page_update",
            true,
            """{"ok":true}""");

        Assert.Equal("applied", verified.Status);
        Assert.True(verified.IndependentlyVerified);
        Assert.Equal("versioned-wiki-state", verified.Evidence);
        Assert.Equal("page-7", verified.ResolvedTarget);
        Assert.False(unverified.IndependentlyVerified);
        Assert.Equal("tool-result", unverified.Evidence);
    }

    [Fact]
    public void Failed_effect_never_claims_verification()
    {
        var effect = ToolEffectClassifier.Describe(
            "memory_store_facts",
            """{"facts":[{"key":"locale"}]}""");

        var outcome = ToolEffectClassifier.Complete(
            effect,
            "memory_store_facts",
            false,
            "permission denied");

        Assert.Equal("failed", outcome.Status);
        Assert.False(outcome.IndependentlyVerified);
        Assert.Equal("tool-error", outcome.Evidence);
    }
}
