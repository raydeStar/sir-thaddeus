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

    [Fact]
    public void ExistenceGate_FlagsDiscontinuedProductAsDoesNotExist()
    {
        var evidence = new List<SearchResult>
        {
            new()
            {
                Title = "Product X2 canceled before release",
                Url = "https://example.com/news/product-x2-canceled",
                Snippet = "The vendor confirmed Product X2 was canceled and never released.",
                Source = "example.com"
            },
            new()
            {
                Title = "No official release page for Product X2",
                Url = "https://docs.vendor.com/roadmap",
                Snippet = "Product X2 was removed from the roadmap and no release was announced.",
                Source = "docs.vendor.com"
            }
        };

        var result = ExistenceGate.Evaluate("Is Product X2 available yet?", evidence);

        Assert.Equal(ExistenceVerdict.DoesNotExist, result.Verdict);
        Assert.True(result.Score <= -18);
    }

    [Fact]
    public void ExistenceGate_FlagsConfirmedReleaseAsExists()
    {
        var evidence = new List<SearchResult>
        {
            new()
            {
                Title = "Vendor announces Product Z1 release",
                Url = "https://vendor.com/product-z1",
                Snippet = "Official product page confirms Product Z1 is available now.",
                Source = "vendor.com"
            },
            new()
            {
                Title = "Product Z1 specifications",
                Url = "https://docs.vendor.com/product-z1/specifications",
                Snippet = "Documentation and release notes are published.",
                Source = "docs.vendor.com"
            }
        };

        var result = ExistenceGate.Evaluate("Does Product Z1 exist?", evidence);

        Assert.Equal(ExistenceVerdict.Exists, result.Verdict);
        Assert.True(result.Score >= 18);
    }
}
