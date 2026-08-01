using System.Net;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public sealed class LlmClientFactoryTests
{
    [Fact]
    public async Task Create_exposes_configurable_contract_for_openai_compatible_transport()
    {
        var options = new LlmClientOptions
        {
            Provider = "custom",
            BaseUrl = "http://localhost:1234",
            Model = "research-model",
            MaxTokens = 64,
        };
        var handler = new SequenceHttpHandler(
        [
            (HttpStatusCode.OK, JsonSerializer.Serialize(new
            {
                id = "factory-test",
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = "factory response" },
                        finish_reason = "stop",
                    },
                },
            })),
        ]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl) };

        using var client = LlmClientFactory.Create(options, http);
        var response = await client.ChatAsync([ChatMessage.User("hello")]);

        Assert.IsAssignableFrom<IConfigurableLlmClient>(client);
        Assert.Equal("factory response", response.Content);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Create_preserves_provider_switching_behind_stable_contract()
    {
        var options = new LlmClientOptions
        {
            Provider = "codex-cli",
            Model = "first-model",
        };
        using var client = LlmClientFactory.Create(options);

        Assert.Equal("first-model", await client.GetModelNameAsync());

        client.UpdateOptions(options with { Model = "second-model" });

        Assert.Equal("second-model", await client.GetModelNameAsync());
    }
}
