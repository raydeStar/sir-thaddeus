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

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
