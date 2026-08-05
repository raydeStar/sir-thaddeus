using SirThaddeus.Agent.Pipeline.Steps;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public sealed class RequestedTerminalDateEvidenceCoveragePolicyTests
{
    [Theory]
    [InlineData("as of the market close on March 14, 2025")]
    [InlineData("until the market closed on March 14, 2025")]
    public void Builds_one_day_correction_for_matching_end_date_and_missing_dated_row(string request)
    {
        var active = RequestedTerminalDateEvidenceCoveragePolicy.TryBuildCorrection(
            request,
            "{\"ticker\":\"AMD\",\"start_date\":\"2024-03-15\",\"end_date\":\"2025-03-14\"}",
            "[{\"Date\":\"2025-03-13T04:00:00.000Z\",\"Close\":90}]",
            out var correction);

        Assert.True(active);
        Assert.Equal(new DateOnly(2025, 3, 14), correction.RequestedDate);
        Assert.Contains("\"end_date\":\"2025-03-15\"", correction.CorrectedArguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_activate_when_requested_date_is_already_present()
    {
        Assert.False(RequestedTerminalDateEvidenceCoveragePolicy.TryBuildCorrection(
            "through market close on March 14, 2025",
            "{\"end_date\":\"2025-03-14\"}",
            "[{\"Date\":\"2025-03-14T04:00:00.000Z\"}]",
            out _));
    }

    [Theory]
    [InlineData("Give me prices for March 2025.", "{\"end_date\":\"2025-03-14\"}", "[{\"Date\":\"2025-03-13T04:00:00.000Z\"}]")]
    [InlineData("through market close on March 14, 2025", "{\"end_date\":\"2025-03-13\"}", "[{\"Date\":\"2025-03-12T04:00:00.000Z\"}]")]
    [InlineData("through market close on March 14, 2025", "not json", "[{\"Date\":\"2025-03-13T04:00:00.000Z\"}]")]
    [InlineData("through market close on March 14, 2025", "{\"end_date\":\"2025-03-14\"}", "not json")]
    public void Fails_closed_for_ambiguous_mismatched_or_unstructured_inputs(
        string request,
        string arguments,
        string result)
    {
        Assert.False(RequestedTerminalDateEvidenceCoveragePolicy.TryBuildCorrection(
            request,
            arguments,
            result,
            out _));
    }
}
