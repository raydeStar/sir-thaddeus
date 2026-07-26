using Thaddeus.Runtime.Api;

namespace Thaddeus.Runtime.Tests;

public sealed class PermissionsApiTests
{
    [Theory]
    [InlineData(null, "group")]
    [InlineData("", "group")]
    [InlineData("group", "group")]
    [InlineData("tool", "tool")]
    [InlineData("call", "call")]
    [InlineData(" CALL ", "call")]
    public void TryNormalizeScope_AcceptsSupportedScopes(string? input, string expected)
    {
        var accepted = PermissionsApi.TryNormalizeScope(input, out var scope);

        Assert.True(accepted);
        Assert.Equal(expected, scope);
    }

    [Fact]
    public void TryNormalizeScope_RejectsUnknownScope()
    {
        var accepted = PermissionsApi.TryNormalizeScope("forever", out var scope);

        Assert.False(accepted);
        Assert.Equal("group", scope);
    }
}
