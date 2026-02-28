using System.Text.Json;
using SirThaddeus.Agent.Orchestration;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Evals;

public class RoutingEvalRunnerTests
{
    private class DummyEmbedder : ITextEmbedder
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            // Dummy implementation that just returns an empty vector for test compilation
            return Task.FromResult(new float[128]);
        }
    }

    private class DummyLlm : ILlmClient
    {
        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse { Content = "{ \"Intent\": \"ChatOnly\", \"Confidence\": 1.0 }", IsComplete = true });
        }

        public Task<LlmResponse> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, int maxTokensOverride, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse { Content = "{ \"Intent\": \"ChatOnly\", \"Confidence\": 1.0 }", IsComplete = true });
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>("dummy-model");
        }
    }

    private class RoutingEvalCase
    {
        public string Prompt { get; set; } = string.Empty;
        public string ExpectedIntent { get; set; } = string.Empty;
        public string RiskLevel { get; set; } = "Low";
    }

    [Fact(Skip = "Run manually to evaluate routing accuracy")]
    public async Task RunV2RoutingEvals()
    {
        var evalPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Evals", "routing_eval_v2.json");
        var json = await File.ReadAllTextAsync(evalPath);
        var cases = JsonSerializer.Deserialize<List<RoutingEvalCase>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(cases);

        var nn = new NnIntentClassifier(new DummyEmbedder());
        var llm = new LlmIntentClassifier(new DummyLlm());
        var router = new RouterV2(nn, llm);

        int passed = 0;
        foreach (var c in cases)
        {
            var decision = await router.RouteAsync(c.Prompt, default);
            if (decision.Intent == c.ExpectedIntent)
            {
                passed++;
            }
            else
            {
                Console.WriteLine($"[FAIL] Prompt: '{c.Prompt}' -> Expected: {c.ExpectedIntent}, Got: {decision.Intent}");
            }
        }

        var accuracy = (double)passed / cases.Count;
        Console.WriteLine($"Accuracy: {accuracy:P0} ({passed}/{cases.Count})");
        
        Assert.True(accuracy >= 0.85, $"Accuracy dropped below 85%: {accuracy:P0}");
    }
}
