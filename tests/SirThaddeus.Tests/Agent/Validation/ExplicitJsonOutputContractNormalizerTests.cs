using System.Text.Json;
using SirThaddeus.Agent.Validation;

namespace SirThaddeus.Tests.Agent.Validation;

public sealed class ExplicitJsonOutputContractNormalizerTests
{
    [Fact]
    public void ConvertsOnlySchemaDeclaredNumericStrings()
    {
        using var template = JsonDocument.Parse(
            """
            {
              "risk": "[NUMBER]",
              "margin": "[NUMBER] percentage",
              "project": "[STRING]"
            }
            """);

        var changed = ExplicitJsonOutputContractNormalizer.TryNormalize(
            """{"risk":"3","margin":"26.06%","project":"Lantern-61","zip":"00123"}""",
            template.RootElement,
            out var normalized,
            out var changeCount);

        Assert.True(changed);
        Assert.Equal(2, changeCount);
        Assert.Equal(
            """{"risk":3,"margin":26.06,"project":"Lantern-61","zip":"00123"}""",
            normalized);
    }

    [Fact]
    public void SupportsNestedArraysCurrencyAndFencedJsonWithoutScaling()
    {
        using var template = JsonDocument.Parse(
            """{"rows":[{"value":"[NUMBER]","rate":"[NUMBER]"}]}""");

        var changed = ExplicitJsonOutputContractNormalizer.TryNormalize(
            """
            ```json
            {"rows":[{"value":"$1,250.50","rate":"12.5%"}]}
            ```
            """,
            template.RootElement,
            out var normalized,
            out var changeCount);

        Assert.True(changed);
        Assert.Equal(2, changeCount);
        Assert.Equal("""{"rows":[{"value":1250.50,"rate":12.5}]}""", normalized);
    }

    [Theory]
    [InlineData("risk is 3")]
    [InlineData("{\"risk\":\"about three\"}")]
    [InlineData("{\"risk\":3}")]
    public void LeavesInvalidAmbiguousAndAlreadyTypedResponsesUntouched(string response)
    {
        using var template = JsonDocument.Parse("""{"risk":"[NUMBER]"}""");

        var changed = ExplicitJsonOutputContractNormalizer.TryNormalize(
            response,
            template.RootElement,
            out var normalized,
            out var changeCount);

        Assert.False(changed);
        Assert.Equal(0, changeCount);
        Assert.Equal(response, normalized);
    }
}
