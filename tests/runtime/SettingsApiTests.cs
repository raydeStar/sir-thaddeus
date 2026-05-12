using Thaddeus.Runtime.Api;

namespace Thaddeus.Runtime.Tests;

public sealed class SettingsApiTests
{
    [Fact]
    public void BuildModelDiscoveryProbeUrls_DoesNotDuplicateV1Path()
    {
        var urls = SettingsApi.BuildModelDiscoveryProbeUrls("http://127.0.0.1:1234/v1");

        Assert.Equal(
            [
                "http://127.0.0.1:1234/api/v0/models",
                "http://127.0.0.1:1234/v1/models",
                "http://127.0.0.1:1234/models"
            ],
            urls);
    }

    [Fact]
    public void BuildModelDiscoveryProbeUrls_PreservesPathPrefixBeforeV1()
    {
        var urls = SettingsApi.BuildModelDiscoveryProbeUrls("https://example.test/proxy/v1/");

        Assert.Equal(
            [
                "https://example.test/proxy/api/v0/models",
                "https://example.test/proxy/v1/models",
                "https://example.test/proxy/models"
            ],
            urls);
    }

    [Fact]
    public void BuildModelDiscoveryProbeUrls_UsesRootBaseUrlAsIs()
    {
        var urls = SettingsApi.BuildModelDiscoveryProbeUrls("http://localhost:1234");

        Assert.Equal(
            [
                "http://localhost:1234/api/v0/models",
                "http://localhost:1234/v1/models",
                "http://localhost:1234/models"
            ],
            urls);
    }
}