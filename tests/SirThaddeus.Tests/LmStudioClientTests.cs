using System.Net;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// LM Studio Client Tests
//
// Covers: self-healing retry when LM Studio returns "Failed to process
// regex" 400 errors, request body construction with/without extras,
// and response parsing.
//
// All tests use a SequenceHttpHandler to simulate LM Studio responses
// without any network calls.
// ─────────────────────────────────────────────────────────────────────────

public class LmStudioClientSelfHealingTests : IDisposable
{
    private static readonly LlmClientOptions DefaultOptions = new()
    {
        BaseUrl          = "http://localhost:1234",
        Model            = "test-model",
        MaxTokens        = 100,
        Temperature      = 0.7,
        RepetitionPenalty = 1.1,
        StopSequences    = ["\nUser:", "\nHuman:"]
    };

    private static readonly ChatMessage[] SimpleMessages =
    [
        ChatMessage.System("You are a test assistant."),
        ChatMessage.User("Hello!")
    ];

    public void Dispose() { }

    [Fact]
    public async Task SuccessOnFirstAttempt_ReturnsResponse()
    {
        var handler = new SequenceHttpHandler([
            MakeSuccessResponse("Hello back!")
        ]);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Equal("Hello back!", response.Content);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RegexFailure_RetriesWithoutExtras_Succeeds()
    {
        var handler = new SequenceHttpHandler([
            MakeRegexFailureResponse(),    // First attempt fails with regex error
            MakeSuccessResponse("Recovered!") // Bare retry succeeds
        ]);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Equal("Recovered!", response.Content);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task RegexFailure_BothAttemptsFail_ThrowsWithMessage()
    {
        var handler = new SequenceHttpHandler([
            MakeRegexFailureResponse(),  // First attempt fails
            MakeRegexFailureResponse()   // Bare retry also fails
        ]);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync(SimpleMessages));

        Assert.Contains("Failed to process regex", ex.Message);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task NonRegexError_DoesNotRetry()
    {
        // A 500 internal server error should NOT trigger the self-healing retry
        var handler = new SequenceHttpHandler([
            MakeErrorResponse(HttpStatusCode.InternalServerError, "Model crashed")
        ]);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync(SimpleMessages));

        Assert.Contains("Model crashed", ex.Message);
        Assert.Equal(1, handler.CallCount);  // No retry
    }

    [Fact]
    public async Task Non400RegexError_DoesNotRetry()
    {
        // A 503 with "regex" in the body should NOT trigger retry (wrong status code)
        var handler = new SequenceHttpHandler([
            MakeErrorResponse(HttpStatusCode.ServiceUnavailable, "Failed to process regex")
        ]);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync(SimpleMessages));

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task MaxTokensOverride_IsRespected()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler([
            MakeSuccessResponse("Short response")
        ], onRequest: body => capturedBody = body);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages, tools: null, maxTokensOverride: 42);

        Assert.NotNull(capturedBody);
        // Verify the body contains max_tokens = 42 (not the default 100)
        using var doc = JsonDocument.Parse(capturedBody);
        Assert.Equal(42, doc.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task BarerRetry_OmitsStopAndRepetitionPenalty()
    {
        var requestBodies = new List<string>();
        var handler = new SequenceHttpHandler([
            MakeRegexFailureResponse(),
            MakeSuccessResponse("Recovered")
        ], onRequest: body => requestBodies.Add(body));

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.Equal(2, requestBodies.Count);

        // First request should include extras
        using var first = JsonDocument.Parse(requestBodies[0]);
        Assert.True(first.RootElement.TryGetProperty("stop", out _),
            "First request should include stop sequences");
        Assert.True(first.RootElement.TryGetProperty("repetition_penalty", out _),
            "First request should include repetition_penalty");

        // Second (bare) request should NOT include extras
        using var second = JsonDocument.Parse(requestBodies[1]);
        Assert.False(second.RootElement.TryGetProperty("stop", out _),
            "Bare retry should NOT include stop sequences");
        Assert.False(second.RootElement.TryGetProperty("repetition_penalty", out _),
            "Bare retry should NOT include repetition_penalty");
    }

    [Fact]
    public async Task NoExtras_WhenDefaultRepetitionPenalty()
    {
        // When RepetitionPenalty is 1.0 (disabled), it should not appear in the body
        var opts = DefaultOptions with { RepetitionPenalty = 1.0, StopSequences = [] };

        string? capturedBody = null;
        var handler = new SequenceHttpHandler([
            MakeSuccessResponse("OK")
        ], onRequest: body => capturedBody = body);

        using var client = new LmStudioClient(opts, new HttpClient(handler)
        {
            BaseAddress = new Uri(opts.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        Assert.False(doc.RootElement.TryGetProperty("repetition_penalty", out _),
            "repetition_penalty=1.0 should be omitted");
        Assert.False(doc.RootElement.TryGetProperty("stop", out _),
            "Empty stop sequences should be omitted");
    }

    [Fact]
    public async Task PlainChat_NormalizesToolScaffolding_ToAlternatingRoles()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler([
            MakeSuccessResponse("All good.")
        ], onRequest: body => capturedBody = body);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var toolCall = new ToolCallRequest
        {
            Id = "call_1",
            Function = new FunctionCallDetails
            {
                Name = "memory_store_facts",
                Arguments = "{}"
            }
        };

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a test assistant."),
            ChatMessage.User("Remember that I like cabbage."),
            ChatMessage.AssistantToolCalls([toolCall]),
            ChatMessage.ToolResult("call_1", """{"stored":1}"""),
            ChatMessage.Assistant("Stored."),
            ChatMessage.User("How are you today?")
        };

        var response = await client.ChatAsync(messages, tools: null);

        Assert.True(response.IsComplete);
        Assert.NotNull(capturedBody);

        using var doc = JsonDocument.Parse(capturedBody);
        var sentMessages = doc.RootElement.GetProperty("messages");
        var roles = sentMessages.EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())
            .ToList();

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, roles);
        Assert.DoesNotContain(sentMessages.EnumerateArray(),
            m => string.Equals(m.GetProperty("role").GetString(), "tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ToolMode_DoesNotStripToolHistory()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler([
            MakeSuccessResponse("Tool mode response")
        ], onRequest: body => capturedBody = body);

        using var client = new LmStudioClient(DefaultOptions, new HttpClient(handler)
        {
            BaseAddress = new Uri(DefaultOptions.BaseUrl)
        });

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a test assistant."),
            ChatMessage.User("Store this memory."),
            ChatMessage.AssistantToolCalls([
                new ToolCallRequest
                {
                    Id = "call_1",
                    Function = new FunctionCallDetails
                    {
                        Name = "memory_store_facts",
                        Arguments = "{}"
                    }
                }
            ]),
            ChatMessage.ToolResult("call_1", """{"stored":1}"""),
            ChatMessage.Assistant("Stored.")
        };

        var tools = new List<ToolDefinition>
        {
            new()
            {
                Function = new FunctionDefinition
                {
                    Name = "memory_store_facts",
                    Description = "Stores memory facts",
                    Parameters = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>()
                    }
                }
            }
        };

        var response = await client.ChatAsync(messages, tools);

        Assert.True(response.IsComplete);
        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody);
        var sentMessages = doc.RootElement.GetProperty("messages");
        Assert.Contains(sentMessages.EnumerateArray(),
            m => string.Equals(m.GetProperty("role").GetString(), "tool", StringComparison.Ordinal));
    }

    // ── Response Builders ──────────────────────────────────────────────

    private static (HttpStatusCode, string) MakeSuccessResponse(string content) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id      = "test-123",
            choices = new[]
            {
                new
                {
                    index         = 0,
                    message       = new { role = "assistant", content },
                    finish_reason = "stop"
                }
            }
        }));

    private static (HttpStatusCode, string) MakeRegexFailureResponse() =>
        (HttpStatusCode.BadRequest,
         """{"error":"Failed to process regex"}""");

    private static (HttpStatusCode, string) MakeErrorResponse(
        HttpStatusCode status, string message) =>
        (status, JsonSerializer.Serialize(new { error = message }));
}

// ─────────────────────────────────────────────────────────────────────────
// Test Infrastructure
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// HTTP handler that serves a sequence of canned responses in order.
/// Tracks how many requests were made for assertion.
/// Optionally captures request bodies for inspection.
/// </summary>
internal sealed class SequenceHttpHandler : HttpMessageHandler
{
    private readonly IReadOnlyList<(HttpStatusCode Status, string Body)> _responses;
    private readonly Action<string>? _onRequest;
    private int _callIndex;

    public int CallCount => _callIndex;

    public SequenceHttpHandler(
        IReadOnlyList<(HttpStatusCode Status, string Body)> responses,
        Action<string>? onRequest = null)
    {
        _responses = responses;
        _onRequest = onRequest;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Capture the request body if a callback was provided
        if (_onRequest is not null && request.Content is not null)
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            _onRequest(body);
        }

        var index = _callIndex;
        _callIndex++;

        if (index >= _responses.Count)
            throw new InvalidOperationException(
                $"Test received more requests ({_callIndex}) than expected ({_responses.Count})");

        var (status, responseBody) = _responses[index];

        return new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
