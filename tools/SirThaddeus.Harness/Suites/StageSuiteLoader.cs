using System.Text.Json;
using SirThaddeus.Harness.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SirThaddeus.Harness.Suites;

internal sealed class StageSuiteLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IDeserializer _yamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public IReadOnlyList<string> ListSuiteNames(string suitesRoot)
    {
        if (string.IsNullOrWhiteSpace(suitesRoot))
            throw new InvalidOperationException("Stage suites root is required.");

        var rooted = Path.IsPathRooted(suitesRoot)
            ? suitesRoot
            : Path.GetFullPath(suitesRoot, Directory.GetCurrentDirectory());

        if (!Directory.Exists(rooted))
            throw new DirectoryNotFoundException($"Stage suites root not found: {rooted}");

        return Directory
            .EnumerateDirectories(rooted)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray()!;
    }

    public StageSuite LoadSuite(string suitesRoot, string suiteName)
    {
        if (string.IsNullOrWhiteSpace(suitesRoot))
            throw new InvalidOperationException("Stage suites root is required.");
        if (string.IsNullOrWhiteSpace(suiteName))
            throw new InvalidOperationException("Stage suite name is required.");

        var suiteDir = ResolveSuiteDirectory(suitesRoot, suiteName);
        if (!Directory.Exists(suiteDir))
            throw new DirectoryNotFoundException($"Stage suite directory not found: {suiteDir}");

        var files = Directory.GetFiles(suiteDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException($"Stage suite '{suiteName}' contains no test files.");

        var tests = new List<StageTestCase>(files.Count);
        foreach (var file in files)
        {
            var test = ParseTestFile(file);
            ValidateTestCase(test, file);
            tests.Add(test);
        }

        return new StageSuite
        {
            Name = suiteName,
            Tests = tests
        };
    }

    private StageTestCase ParseTestFile(string path)
    {
        var text = File.ReadAllText(path);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = JsonSerializer.Deserialize<StageTestCase>(text, JsonOptions);
            return parsed ?? throw new InvalidOperationException($"Failed to parse stage suite JSON: {path}");
        }

        var yamlParsed = _yamlDeserializer.Deserialize<StageTestCase>(text);
        return yamlParsed ?? throw new InvalidOperationException($"Failed to parse stage suite YAML: {path}");
    }

    private static void ValidateTestCase(StageTestCase test, string filePath)
    {
        if (string.IsNullOrWhiteSpace(test.Id))
            throw new InvalidOperationException($"Missing id in stage suite test: {filePath}");
        if (string.IsNullOrWhiteSpace(test.Name))
            throw new InvalidOperationException($"Missing name in stage suite test: {filePath}");
        if (string.IsNullOrWhiteSpace(test.Input))
            throw new InvalidOperationException($"Missing input in stage suite test: {filePath}");

        if (test.Checks.Preprocess is null && test.Checks.Classify is null && test.Checks.Query is null)
            throw new InvalidOperationException($"Stage suite test must declare at least one stage_check: {filePath}");
    }

    private static string ResolveSuiteDirectory(string suitesRoot, string suiteName)
    {
        var rooted = Path.IsPathRooted(suitesRoot)
            ? suitesRoot
            : Path.GetFullPath(suitesRoot, Directory.GetCurrentDirectory());

        return Path.Combine(rooted, suiteName);
    }
}