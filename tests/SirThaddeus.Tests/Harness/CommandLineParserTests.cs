using SirThaddeus.Harness.Cli;

namespace SirThaddeus.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void Parse_StagePreflight_ParsesContextOptions()
    {
        var options = CommandLineParser.Parse(
        [
            "stage",
            "preflight",
            "--input",
            "tell me more",
            "--assistant-context",
            "Here are 10 bakeries I found nearby in Olympia, WA.",
            "--followup-anchor",
            "Left Bank Pastry",
            "--user-city",
            "Olympia, WA",
            "--has-recent-search-results",
            "--has-recent-rationale"
        ]);

        Assert.Equal(HarnessCommandKind.Stage, options.Command);
        Assert.Equal(HarnessStageTarget.Preflight, options.StageTarget);
        Assert.Equal("tell me more", options.StageInput);
        Assert.Equal("Here are 10 bakeries I found nearby in Olympia, WA.", options.StageAssistantContext);
        Assert.Equal("Left Bank Pastry", options.StageFollowUpAnchor);
        Assert.Equal("Olympia, WA", options.StageUserCity);
        Assert.True(options.StageHasRecentSearchResults);
        Assert.True(options.StageHasRecentFirstPrinciplesRationale);
    }

    [Fact]
    public void Parse_StageSuiteSelection_UsesStageSuitesDefaultRoot()
    {
        var options = CommandLineParser.Parse(
        [
            "stage",
            "query",
            "--suite",
            "continuity"
        ]);

        Assert.Equal(HarnessCommandKind.Stage, options.Command);
        Assert.Equal(HarnessStageTarget.Query, options.StageTarget);
        Assert.Equal("continuity", options.SuiteName);
        Assert.EndsWith(Path.Combine("tools", "SirThaddeus.Harness", "StageSuites"), options.SuitesRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_StageAllAndSuite_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse([
            "stage",
            "query",
            "--all",
            "--suite",
            "continuity"
        ]));

        Assert.Contains("either --all or --suite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_UnknownStageTarget_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(["stage", "mystery", "--input", "hello"]));

        Assert.Contains("preflight", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_InspectLatestFailure_ParsesRunId()
    {
        var options = CommandLineParser.Parse(
        [
            "inspect",
            "latest-failure",
            "--artifacts-root",
            "artifacts/harness",
            "--run-id",
            "20260326_010101"
        ]);

        Assert.Equal(HarnessCommandKind.Inspect, options.Command);
        Assert.Equal(HarnessInspectTarget.LatestFailure, options.InspectTarget);
        Assert.Equal("20260326_010101", options.InspectRunId);
    }

    [Fact]
    public void Parse_InspectUnknownTarget_ThrowsHelpfulError()
    {
        var ex = Assert.Throws<CommandLineException>(() => CommandLineParser.Parse(["inspect", "weird"]));

        Assert.Contains("latest-failure", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}