using SirThaddeus.DirectEval;

namespace SirThaddeus.Tests.Harness;

public sealed class DirectEvalStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "SirThaddeus.DirectEval.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Applies_and_observes_files_inside_the_case_sandbox()
    {
        var state = new DirectEvalState(new FakeMcpClient("{}"), _root);
        await state.ApplyAsync(
            new DirectStateSetup
            {
                Files = [new DirectFileSetup { Path = "notes/evidence.txt", Content = "fabricated" }]
            },
            CancellationToken.None);

        var observed = await state.ObserveAsync(
            [new DirectObservation { Type = "files", Paths = ["notes/evidence.txt"] }],
            CancellationToken.None);

        var json = System.Text.Json.JsonSerializer.Serialize(observed);
        Assert.Contains("fabricated", json);
        Assert.Contains("notes/evidence.txt", json);
    }

    [Fact]
    public async Task Rejects_fixture_paths_that_escape_the_case_sandbox()
    {
        var state = new DirectEvalState(new FakeMcpClient("{}"), _root);

        await Assert.ThrowsAsync<InvalidDataException>(() => state.ApplyAsync(
            new DirectStateSetup
            {
                Files = [new DirectFileSetup { Path = "../outside.txt", Content = "no" }]
            },
            CancellationToken.None));
    }

    [Fact]
    public async Task Applies_exact_ambiguous_delete_preflight_and_tears_down_cleanly()
    {
        var client = new RecordingWikiMcpClient();
        var state = new DirectEvalState(client, _root);
        var setup = new DirectStateSetup
        {
            WikiRoots =
            [
                new DirectWikiRootSetup { Name = "Old Brindle Archive East" },
                new DirectWikiRootSetup { Name = "Old Brindle Archive West" }
            ]
        };

        await state.ApplyAsync(setup, CancellationToken.None);

        Assert.Equal(
            ["Old Brindle Archive East", "Old Brindle Archive West"],
            client.CreatedRoots);
        Assert.True(Directory.Exists(_root));

        Dispose();

        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingWikiMcpClient : SirThaddeus.Agent.IMcpToolClient
    {
        public List<string> CreatedRoots { get; } = [];

        public Task<string> CallToolAsync(
            string name,
            string argumentsJson,
            CancellationToken cancellationToken)
        {
            Assert.Equal("wiki_root_create", name);
            using var arguments = System.Text.Json.JsonDocument.Parse(argumentsJson);
            var rootName = arguments.RootElement.GetProperty("name").GetString()!;
            CreatedRoots.Add(rootName);
            var id = $"root_{CreatedRoots.Count}";
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
            {
                ok = true,
                root = new { id }
            }));
        }

        public Task<IReadOnlyList<SirThaddeus.Agent.McpToolInfo>> ListToolsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SirThaddeus.Agent.McpToolInfo>>([]);
    }
}
