using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;

namespace Thaddeus.Runtime.Tests;

public sealed class McpClientHostTests
{
    [Fact]
    public void BuildEnv_UsesRuntimeWikiLibraryForMcpTools()
    {
        var wikiLibrary = Path.Combine(Path.GetTempPath(), "thaddeus-runtime-wiki");

        var env = McpClientHost.BuildEnv(SettingsDocument.Defaults(), wikiLibrary);

        Assert.Equal(Path.GetFullPath(wikiLibrary), env["ST_WIKI_LIBRARY_PATH"]);
    }

    [Fact]
    public void BuildEnv_OmitsWikiOverrideWhenStorePathIsUnavailable()
    {
        var env = McpClientHost.BuildEnv(SettingsDocument.Defaults());

        Assert.False(env.ContainsKey("ST_WIKI_LIBRARY_PATH"));
    }

    [Fact]
    public void BuildEnv_UsesCurrentRuntimeSettingsForMcpTools()
    {
        var settingsPath = Path.Combine(
            Path.GetTempPath(),
            "thaddeus-runtime-settings",
            "runtime-settings.json");

        var env = McpClientHost.BuildEnv(
            SettingsDocument.Defaults(),
            settingsPath: settingsPath);

        Assert.Equal(Path.GetFullPath(settingsPath), env["ST_SETTINGS_PATH"]);
    }
}
