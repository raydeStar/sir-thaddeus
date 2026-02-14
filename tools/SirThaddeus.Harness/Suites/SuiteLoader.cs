using System.Text.Json;
using SirThaddeus.Harness.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SirThaddeus.Harness.Suites;

public sealed class SuiteLoader
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

    public HarnessSuite LoadSuite(string suitesRoot, string suiteName)
    {
        if (string.IsNullOrWhiteSpace(suitesRoot))
            throw new InvalidOperationException("Suites root is required.");
        if (string.IsNullOrWhiteSpace(suiteName))
            throw new InvalidOperationException("Suite name is required.");

        var suiteDir = ResolveSuiteDirectory(suitesRoot, suiteName);
        if (!Directory.Exists(suiteDir))
            throw new DirectoryNotFoundException($"Suite directory not found: {suiteDir}");

        var files = Directory.GetFiles(suiteDir, "*.*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
            throw new InvalidOperationException($"Suite '{suiteName}' contains no test files.");

        var tests = new List<HarnessTestCase>(files.Count);
        foreach (var file in files)
        {
            var test = ParseTestFile(file);
            ValidateTestCase(test, file);
            tests.Add(test);
        }

        return new HarnessSuite
        {
            Name = suiteName,
            Tests = tests
        };
    }

    private HarnessTestCase ParseTestFile(string path)
    {
        var text = File.ReadAllText(path);
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            var parsed = JsonSerializer.Deserialize<HarnessTestCase>(text, JsonOptions);
            return parsed ?? throw new InvalidOperationException($"Failed to parse suite test JSON: {path}");
        }

        var yamlParsed = _yamlDeserializer.Deserialize<HarnessTestCase>(text);
        return yamlParsed ?? throw new InvalidOperationException($"Failed to parse suite test YAML: {path}");
    }

    private static void ValidateTestCase(HarnessTestCase test, string filePath)
    {
        if (string.IsNullOrWhiteSpace(test.Id))
            throw new InvalidOperationException($"Missing id in suite test: {filePath}");
        if (string.IsNullOrWhiteSpace(test.Name))
            throw new InvalidOperationException($"Missing name in suite test: {filePath}");
        if (string.IsNullOrWhiteSpace(test.UserMessage))
            throw new InvalidOperationException($"Missing user_message in suite test: {filePath}");
        if (test.MinScore < 0 || test.MinScore > 10)
            throw new InvalidOperationException($"min_score must be 0..10 in suite test: {filePath}");
    }

    private static string ResolveSuiteDirectory(string suitesRoot, string suiteName)
    {
        var rooted = Path.IsPathRooted(suitesRoot)
            ? suitesRoot
            : Path.GetFullPath(suitesRoot, Directory.GetCurrentDirectory());

        return Path.Combine(rooted, suiteName);
    }
}
