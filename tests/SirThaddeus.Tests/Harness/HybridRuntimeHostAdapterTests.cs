using System.Text.Json;
using SirThaddeus.Config;
using SirThaddeus.Harness.Execution;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Tests.Harness;

public sealed class HybridRuntimeHostAdapterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "thaddeus-hybrid-adapter-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteHarnessSettingsFile_PreservesFrozenModelParameters()
    {
        Directory.CreateDirectory(_root);
        var settings = new AppSettings
        {
            Llm = new LlmSettings
            {
                BaseUrl = "http://127.0.0.1:1234",
                Model = "test-model",
                MaxTokens = 321,
                ContextWindowTokens = 8192,
                Temperature = 0.125,
            },
        };
        var adapter = new HybridRuntimeHostAdapter(settings);

        adapter.WriteHarnessSettingsFile(_root);

        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_root, "runtime-settings.json")));
        var llm = document.RootElement.GetProperty("llm");
        Assert.Equal("test-model", llm.GetProperty("modelId").GetString());
        Assert.Equal(321, llm.GetProperty("maxTokens").GetInt32());
        Assert.Equal(8192, llm.GetProperty("contextWindowTokens").GetInt32());
        Assert.Equal(0.125, llm.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void HarnessTestCase_DeserializesStateSetupAndObservationScope()
    {
        const string json = """
            {
              "id": "wiki-state",
              "user_message": "Update the page.",
              "state_setup": {
                "wiki_roots": [
                  {
                    "name": "Research",
                    "pages": [{ "title": "Plan", "markdown": "before" }]
                  }
                ]
              },
              "observations": [
                { "type": "wiki", "root_names": ["Research"] }
              ]
            }
            """;

        var test = JsonSerializer.Deserialize<HarnessTestCase>(json);

        Assert.NotNull(test);
        Assert.Equal("Research", test.StateSetup.WikiRoots.Single().Name);
        Assert.Equal("Plan", test.StateSetup.WikiRoots.Single().Pages.Single().Title);
        Assert.Equal("wiki", test.Observations.Single().Type);
        Assert.Equal("Research", test.Observations.Single().RootNames.Single());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }
}
