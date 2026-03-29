using SirThaddeus.Harness.Suites;

namespace SirThaddeus.Tests;

public sealed class StageSuiteLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sir-thaddeus-stage-suite-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void LoadSuite_ParsesFollowUpContext()
    {
        var suiteDir = Path.Combine(_root, "continuity");
        Directory.CreateDirectory(suiteDir);
        File.WriteAllText(
            Path.Combine(suiteDir, "01_followup_anchor.json"),
            """
            {
              "id": "local_business_followup_anchor",
              "name": "Vague bakery follow-up uses anchor",
              "input": "Pull up more info about that bakery.",
              "context": {
                "assistant_context": "Here is what I found: Left Bank Pastry at 108 5th Ave SW, Olympia, WA.",
                "followup_anchor": "Left Bank Pastry",
                "user_city": "Olympia, WA",
                "has_recent_search_results": true
              },
              "stage_checks": {
                "query": {
                  "search_query_must_contain": ["Left Bank Pastry"]
                }
              }
            }
            """);

        var loader = new StageSuiteLoader();

        var suite = loader.LoadSuite(_root, "continuity");

        var test = Assert.Single(suite.Tests);
        Assert.Equal("Left Bank Pastry", test.Context.FollowUpAnchor);
        Assert.Equal("Olympia, WA", test.Context.UserCity);
        Assert.True(test.Context.HasRecentSearchResults);
        Assert.Contains("Left Bank Pastry", test.Checks.Query!.SearchQueryMustContain);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}