using SirThaddeus.Config;
using SirThaddeus.RuntimeHost;

namespace SirThaddeus.Tests;

[Collection(RuntimeEnvironmentVariableCollection.Name)]
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

    [Fact]
    public void Build_PreservesInheritedSettingsAndAuditPaths()
    {
        using var envScope = new EnvironmentVariableScope(new Dictionary<string, string?>
        {
            ["ST_SETTINGS_PATH"] = @"C:\temp\custom-settings.json",
            ["ST_AUDIT_PATH"] = @"C:\temp\custom-audit.jsonl"
        });

        var env = RuntimeMcpEnvironmentBuilder.Build(new AppSettings());

        Assert.Equal(@"C:\temp\custom-settings.json", env["ST_SETTINGS_PATH"]);
        Assert.Equal(@"C:\temp\custom-audit.jsonl", env["ST_AUDIT_PATH"]);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly Dictionary<string, string?> _priorValues = new(StringComparer.OrdinalIgnoreCase);

        public EnvironmentVariableScope(IReadOnlyDictionary<string, string?> values)
        {
            foreach (var (key, value) in values)
            {
                _priorValues[key] = Environment.GetEnvironmentVariable(key);
                Environment.SetEnvironmentVariable(key, value);
            }
        }

        public void Dispose()
        {
            foreach (var (key, value) in _priorValues)
            {
                Environment.SetEnvironmentVariable(key, value);
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RuntimeEnvironmentVariableCollection
{
    public const string Name = "RuntimeEnvironmentVariables";
}