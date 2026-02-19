using System.Text.Json;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Artifacts;

public sealed class FixtureStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string ResolveFixturePath(string fixturesRoot, string suiteName, string testId)
    {
        var rooted = Path.IsPathRooted(fixturesRoot)
            ? fixturesRoot
            : Path.GetFullPath(fixturesRoot, Directory.GetCurrentDirectory());
        Directory.CreateDirectory(Path.Combine(rooted, suiteName));
        return Path.Combine(rooted, suiteName, $"{testId}.json");
    }

    public async Task<HarnessFixture> LoadAsync(
        string fixturesRoot,
        string suiteName,
        string testId,
        CancellationToken cancellationToken)
    {
        var path = ResolveFixturePath(fixturesRoot, suiteName, testId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Replay fixture not found for suite '{suiteName}', test '{testId}'. Run harness record first.",
                path);
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var fixture = JsonSerializer.Deserialize<HarnessFixture>(json, JsonOptions);
        return fixture ?? throw new InvalidOperationException($"Failed to parse fixture: {path}");
    }

    public Task SaveAsync(
        string fixturesRoot,
        string suiteName,
        HarnessFixture fixture,
        CancellationToken cancellationToken)
    {
        var path = ResolveFixturePath(fixturesRoot, suiteName, fixture.TestId);
        var json = JsonSerializer.Serialize(fixture, JsonOptions);
        return File.WriteAllTextAsync(path, json, cancellationToken);
    }
}
