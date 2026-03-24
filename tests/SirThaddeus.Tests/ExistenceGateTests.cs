using SirThaddeus.WebSearch;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests;

public class ExistenceGateTests
{
    [Fact]
    public void ReleasedProductExistenceAnswer_ConfirmedModel_UsesCautiousPositiveWording()
    {
        var sources = new List<SourceItem>
        {
            new()
            {
                Url = "https://support.apple.com/en-us/111831",
                Title = "iPhone 15 - Tech Specs - Apple Support",
                Domain = "support.apple.com",
                Snippet = "Year introduced: 2023. iPhone 15 - Tech Specs.",
                SourceId = SourceItem.ComputeSourceId("https://support.apple.com/en-us/111831")
            },
            new()
            {
                Url = "https://www.apple.com/iphone/compare/?modelList=iphone-15",
                Title = "iPhone 15 vs iPhone 15 Pro vs iPhone 15 Plus - Apple",
                Domain = "apple.com",
                Snippet = "Compare iPhone models including iPhone 15.",
                SourceId = SourceItem.ComputeSourceId("https://www.apple.com/iphone/compare/?modelList=iphone-15")
            }
        };

        var answer = SearchOrchestrator.BuildReleasedProductExistenceAnswer(
            "Does iPhone 15 exist as a released product?",
            sources);

        Assert.NotNull(answer);
        Assert.Contains("could not verify definitive proof", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iPhone 15 exists as a released product", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleasedProductExistenceAnswer_MissingModelInGenericLists_UsesUnclearWording()
    {
        var sources = new List<SourceItem>
        {
            new()
            {
                Url = "https://en.wikipedia.org/wiki/List_of_iPhone_models",
                Title = "List of iPhone models - Wikipedia",
                Domain = "en.wikipedia.org",
                Snippet = "The iPhone is a line of smartphones developed by Apple.",
                SourceId = SourceItem.ComputeSourceId("https://en.wikipedia.org/wiki/List_of_iPhone_models")
            },
            new()
            {
                Url = "https://www.digitaltrends.com/phones/every-iphone-release-in-chronological-order/",
                Title = "Every iPhone release in chronological order: 2007-2025",
                Domain = "digitaltrends.com",
                Snippet = "Every iPhone release in chronological order.",
                SourceId = SourceItem.ComputeSourceId("https://www.digitaltrends.com/phones/every-iphone-release-in-chronological-order/")
            }
        };

        var answer = SearchOrchestrator.BuildReleasedProductExistenceAnswer(
            "Does iPhone 99 exist as a released product?",
            sources);

        Assert.NotNull(answer);
        Assert.Contains("could not confirm from the returned snippets", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iPhone 99", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleasedProductExistenceAnswer_ExplicitNegativeSignals_UsesLikelyDoesNotExistWording()
    {
        var sources = new List<SourceItem>
        {
            new()
            {
                Url = "https://example.com/iphone-99-rumor-roundup",
                Title = "iPhone 99 rumor roundup",
                Domain = "example.com",
                Snippet = "iPhone 99 remains a rumor concept and was not released.",
                SourceId = SourceItem.ComputeSourceId("https://example.com/iphone-99-rumor-roundup")
            },
            new()
            {
                Url = "https://example.net/is-iphone-99-real",
                Title = "Is iPhone 99 real?",
                Domain = "example.net",
                Snippet = "There is no such released model; it is an unreleased rumor.",
                SourceId = SourceItem.ComputeSourceId("https://example.net/is-iphone-99-real")
            }
        };

        var answer = SearchOrchestrator.BuildReleasedProductExistenceAnswer(
            "Does iPhone 99 exist as a released product?",
            sources);

        Assert.NotNull(answer);
        Assert.Contains("likely does not exist", answer, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("iPhone 99", answer, StringComparison.OrdinalIgnoreCase);
    }

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
