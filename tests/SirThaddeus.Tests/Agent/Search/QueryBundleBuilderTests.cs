using SirThaddeus.WebSearch;

namespace SirThaddeus.Tests.Agent.Search;

public class QueryBundleBuilderTests
{
    // ── TryBroaden: positive cases ──────────────────────────────────────

    [Theory]
    [InlineData(
        "What is the latest stable version of QuantaScript as of 2025? Answer in exactly two lines: Line 1 starts with 'Answer:' and Line 2 starts with 'Commentary:'. Keep it concise.",
        "latest stable version")]
    [InlineData(
        "Who is the current CEO of OpenAI as of today",
        "CEO")]
    [InlineData(
        "what happened in 2024 that was \"significant\"",
        "what happened")]
    public void TryBroaden_strips_over_specific_modifiers(string input, string expectedFragment)
    {
        var broadened = QueryBundleBuilder.TryBroaden(input);
        Assert.NotNull(broadened);
        Assert.Contains(expectedFragment, broadened!, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exactly", broadened, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("please", broadened, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBroaden_removes_trailing_question_marks_and_punctuation()
    {
        var broadened = QueryBundleBuilder.TryBroaden("what is Bitcoin right now?");
        Assert.NotNull(broadened);
        Assert.DoesNotContain("right now", broadened!);
        Assert.False(broadened.EndsWith("?"), $"expected no trailing '?', got: '{broadened}'");
    }

    // ── TryBroaden: negative / guard cases ──────────────────────────────

    [Fact]
    public void TryBroaden_returns_null_when_nothing_to_drop()
    {
        // A query with no format/date/politeness markers already is as
        // broad as we can make it without changing the topic.
        Assert.Null(QueryBundleBuilder.TryBroaden("iPhone 15 release date"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?")]
    public void TryBroaden_returns_null_on_empty_or_trivial(string input)
    {
        Assert.Null(QueryBundleBuilder.TryBroaden(input));
    }

    [Fact]
    public void TryBroaden_never_returns_same_string()
    {
        // Required contract: caller uses null to decide whether to retry;
        // returning the original query unchanged would cause double-queries.
        var inputs = new[]
        {
            "please tell me the latest news",
            "kindly explain this concept",
            "what is \"foo\"",
            "explain concisely",
        };
        foreach (var input in inputs)
        {
            var broadened = QueryBundleBuilder.TryBroaden(input);
            if (broadened is not null)
            {
                Assert.NotEqual(input.Trim(), broadened, System.StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    // ── Regression: existing TV-show bundle behavior is untouched ───────

    [Fact]
    public void Build_with_season_episode_query_produces_bundle()
    {
        var bundle = QueryBundleBuilder.Build("What happens in season 3 episode 5 of Severance?");
        Assert.NotEmpty(bundle);
        Assert.Contains(bundle, q => q.Contains("season 3 episode 5"));
    }

    [Fact]
    public void Build_with_generic_query_returns_single_entry()
    {
        var bundle = QueryBundleBuilder.Build("what's the weather today");
        Assert.Single(bundle);
    }
}
