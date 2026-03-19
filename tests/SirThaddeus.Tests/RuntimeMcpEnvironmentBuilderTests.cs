using SirThaddeus.Config;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

public sealed class RuntimeMcpEnvironmentBuilderTests
{
    [Fact]
    public void Build_ExportsFileAccessGuardEnvironmentVariables()
    {
        var rootOne = Path.Combine(Path.GetTempPath(), "st-root-one");
        var rootTwo = Path.Combine(Path.GetTempPath(), "st-root-two");
        var settings = new AppSettings
        {
            DocumentReader = new DocumentReaderSettings
            {
                DisableAllFileAccess = true,
                AllowedRoots = [rootOne, rootTwo]
            }
        };

        var env = RuntimeMcpEnvironmentBuilder.Build(settings);

        Assert.Equal("true", env["ST_DOCUMENT_READER_DISABLE_FILE_ACCESS"]);
        Assert.Equal(string.Join(Path.PathSeparator, new[] { rootOne, rootTwo }), env["ST_DOCUMENT_READER_ALLOWED_ROOTS"]);
    }
}