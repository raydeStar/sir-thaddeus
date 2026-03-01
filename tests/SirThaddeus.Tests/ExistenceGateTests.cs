using SirThaddeus.WebSearch;

namespace SirThaddeus.Tests;

public class ExistenceGateTests
{
    [Fact]
    public void QueryBundleBuilder_BuildsSeasonEpisodeBundle()
    {
        var bundle = QueryBundleBuilder.Build("What is the plot of Episode 1 of Season 3 of Stargate Universe?");

        Assert.Equal(4, bundle.Count);
        Assert.Contains("stargate universe season 3 cancelled", string.Join(" ", bundle).ToLowerInvariant());
    }

    [Fact]
    public void ExistenceGate_FlagsMissingSeasonAsDoesNotExist()
    {
        var evidence = new List<SearchResult>
        {
            new()
            {
                Title = "Stargate Universe cancelled after two seasons",
                Url = "https://en.wikipedia.org/wiki/Stargate_Universe",
                Snippet = "The series ended after season 2 and was never renewed for season 3.",
                Source = "wikipedia.org"
            },
            new()
            {
                Title = "Why SGU ended",
                Url = "https://www.tvguide.com/news/stargate-universe-canceled",
                Snippet = "Syfy canceled Stargate Universe and no season 3 was produced.",
                Source = "tvguide.com"
            }
        };

        var result = ExistenceGate.Evaluate("What is the plot of Episode 1 of Season 3 of Stargate Universe?", evidence);

        Assert.Equal(ExistenceVerdict.DoesNotExist, result.Verdict);
        Assert.True(result.Score <= -30);
        Assert.NotEmpty(result.Evidence);
    }
}
