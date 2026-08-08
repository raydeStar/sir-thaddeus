using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.LlmClient;

/// <summary>
/// Covers the context-window budget: the client must never send a request whose
/// prompt plus requested completion cannot fit the model's context, and when the
/// provider reports its real <c>n_ctx</c> the client must learn it rather than
/// failing the turn.
///
/// <para>The regression these guard: a ~6.4k-token tool-loop prompt was sent
/// with <c>max_tokens: 4096</c> against a model loaded at 8192, llama.cpp
/// rejected it with "n_keep &gt;= n_ctx", and the whole assistant turn was
/// discarded and replaced with a transport-failure message — while the model
/// was healthy and generating.</para>
/// </summary>
public sealed class LmStudioClientContextBudgetTests
{
    private const string OverflowError =
        "{\"error\":\"The number of tokens to keep from the initial prompt is greater than " +
        "the context length (n_keep: 11553>= n_ctx: 8192). Try to load the model with a larger " +
        "context length, or provide a shorter input.\"}";

    [Theory]
    [InlineData("(n_keep: 11553>= n_ctx: 8192)", 8192)]
    [InlineData("n_ctx: 4096", 4096)]
    [InlineData("N_CTX:  16384 ", 16384)]
    public void TryParseContextOverflow_reads_the_reported_context_length(string body, int expected)
    {
        Assert.True(LmStudioClient.TryParseContextOverflow(body, out var parsed));
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("some unrelated 400")]
    [InlineData("n_ctx: not-a-number")]
    public void TryParseContextOverflow_ignores_unrelated_errors(string? body)
    {
        Assert.False(LmStudioClient.TryParseContextOverflow(body, out var parsed));
        Assert.Equal(0, parsed);
    }

    [Fact]
    public async Task Requested_completion_never_exceeds_the_configured_context_window()
    {
        // Context window 8192, prompt ~6000 tokens, configured cap 4096.
        // 6000 + 4096 overflows, so the cap must be trimmed to fit.
        var handler = new RecordingHandler(_ => Ok("fine"));
        using var client = NewClient(handler, contextWindowTokens: 8192, maxTokens: 4096);

        await client.ChatAsync(new[] { ChatMessage.User(new string('x', 24_000)) });

        var maxTokens = handler.Requests.Single().MaxTokens;
        var promptTokens = handler.Requests.Single().EstimatedPromptTokens;
        Assert.True(maxTokens < 4096, $"expected the configured cap to be trimmed, got {maxTokens}");
        Assert.True(
            promptTokens + maxTokens <= 8192,
            $"prompt {promptTokens} + completion {maxTokens} must fit 8192");
    }

    [Fact]
    public async Task Small_prompts_keep_the_configured_completion_budget()
    {
        // Nothing to trim here — the budget must not become needlessly stingy.
        var handler = new RecordingHandler(_ => Ok("fine"));
        using var client = NewClient(handler, contextWindowTokens: 8192, maxTokens: 512);

        await client.ChatAsync(new[] { ChatMessage.User("hello") });

        Assert.Equal(512, handler.Requests.Single().MaxTokens);
    }

    [Fact]
    public async Task Context_overflow_is_relearned_from_the_provider_and_retried()
    {
        // Settings claim 16384 but the model is really loaded at 8192, so the
        // first request looks fine to us and is rejected. The provider's own
        // n_ctx is authoritative: learn it, re-budget, and complete the turn.
        var responses = 0;
        var handler = new RecordingHandler(_ =>
            ++responses == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(OverflowError, Encoding.UTF8, "application/json"),
                }
                : Ok("recovered"));
        using var client = NewClient(handler, contextWindowTokens: 16384, maxTokens: 4096);

        var reply = await client.ChatAsync(new[] { ChatMessage.User(new string('x', 24_000)) });

        Assert.Equal("recovered", reply.Content);
        Assert.Equal(2, handler.Requests.Count);

        var retry = handler.Requests[1];
        Assert.True(
            retry.EstimatedPromptTokens + retry.MaxTokens <= 8192,
            $"retry prompt {retry.EstimatedPromptTokens} + completion {retry.MaxTokens} must fit the observed 8192");
    }

    [Fact]
    public async Task Learned_context_window_is_applied_to_later_requests_without_another_failure()
    {
        var responses = 0;
        var handler = new RecordingHandler(_ =>
            ++responses == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(OverflowError, Encoding.UTF8, "application/json"),
                }
                : Ok("fine"));
        using var client = NewClient(handler, contextWindowTokens: 16384, maxTokens: 4096);

        await client.ChatAsync(new[] { ChatMessage.User(new string('x', 24_000)) });
        var afterLearning = handler.Requests.Count;

        await client.ChatAsync(new[] { ChatMessage.User(new string('x', 24_000)) });

        // The second turn must succeed on its first attempt — one extra
        // round-trip per turn forever would be its own latency regression.
        Assert.Equal(afterLearning + 1, handler.Requests.Count);
        var second = handler.Requests[^1];
        Assert.True(second.EstimatedPromptTokens + second.MaxTokens <= 8192);
    }

    [Fact]
    public async Task Learned_context_window_is_cleared_when_runtime_options_change()
    {
        var responses = 0;
        var handler = new RecordingHandler(_ =>
            ++responses == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(OverflowError, Encoding.UTF8, "application/json"),
                }
                : Ok("fine"));
        using var client = NewClient(handler, contextWindowTokens: 16384, maxTokens: 4096);

        await client.ChatAsync(new[] { ChatMessage.User(new string('x', 24_000)) });

        var observedField = typeof(LmStudioClient).GetField(
            "_observedContextWindowTokens",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(observedField);
        Assert.Equal(8192, (int)observedField!.GetValue(client)!);

        client.UpdateOptions(new LlmClientOptions
        {
            Provider = "lmstudio",
            BaseUrl = "http://localhost:1234",
            Model = "a-different-model",
            ContextWindowTokens = 32768,
            ContextLength = 32768,
            MaxTokens = 4096,
            EnableStartupWarmup = false,
            EnableKeepWarm = false,
        });

        Assert.Equal(0, (int)observedField.GetValue(client)!);
    }

    [Fact]
    public async Task Tool_schemas_count_against_the_context_window()
    {
        // Tool definitions ride in the same request body and consume the same
        // window, but they arrive as a separate argument from the messages. A
        // messages-only budget ignored them and shipped requests that could not
        // possibly fit — the exact shape of the tool-loop failure this guards.
        var prompt = new[] { ChatMessage.User(new string('x', 12_000)) }; // ~3000 tokens

        var withoutTools = new RecordingHandler(_ => Ok("fine"));
        using (var bare = NewClient(withoutTools, contextWindowTokens: 8192, maxTokens: 4096))
        {
            await bare.ChatAsync(prompt);
        }

        var withTools = new RecordingHandler(_ => Ok("fine"));
        using (var equipped = NewClient(withTools, contextWindowTokens: 8192, maxTokens: 4096))
        {
            await equipped.ChatAsync(prompt, BulkyTools(24));
        }

        var bareBudget = withoutTools.Requests.Single().MaxTokens;
        var equippedBudget = withTools.Requests.Single().MaxTokens;

        Assert.True(
            equippedBudget < bareBudget,
            $"advertising tools must shrink the completion budget: {equippedBudget} vs {bareBudget}");
        Assert.True(equippedBudget >= 1, "the budget must stay positive");
    }

    /// <summary>Tool definitions with schemas large enough to matter.</summary>
    private static IReadOnlyList<ToolDefinition> BulkyTools(int count) =>
        Enumerable.Range(0, count).Select(i => new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = $"tool_number_{i}",
                Description = $"Performs operation {i}. " + new string('d', 300),
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = new string('q', 200) },
                        limit = new { type = "integer", description = new string('l', 200) },
                    },
                    required = new[] { "query" },
                },
            },
        }).ToArray();

    [Fact]
    public async Task Unfittable_request_reports_the_shortfall_instead_of_the_raw_provider_error()
    {
        // "n_keep: 13984 >= n_ctx: 8192" tells a user nothing about which part
        // of their setup is oversized. When the prompt cannot fit at any
        // completion budget, say what needs how much and what to change.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(OverflowError, Encoding.UTF8, "application/json"),
        });
        using var client = NewClient(handler, contextWindowTokens: 32768, maxTokens: 4096);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync(new[] { ChatMessage.User(new string('x', 40_000)) }, BulkyTools(40)));

        Assert.Contains("context", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("8,192", ex.Message, StringComparison.Ordinal);
        Assert.Contains("tool definitions", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("larger context length", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Hopeless requests must not be retried — a second identical rejection
        // is pure added latency on an already-failing turn.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Context_exhausted_message_blames_tools_only_when_tools_dominate()
    {
        var toolHeavy = LmStudioClient.BuildContextExhaustedMessage(
            contextWindowTokens: 8192, promptTokens: 2_000, toolTokens: 8_000, toolCount: 60);
        Assert.Contains("tool definitions alone", toolHeavy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("60", toolHeavy, StringComparison.Ordinal);

        var historyHeavy = LmStudioClient.BuildContextExhaustedMessage(
            contextWindowTokens: 8192, promptTokens: 9_000, toolTokens: 200, toolCount: 2);
        Assert.DoesNotContain("tool definitions alone", historyHeavy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new conversation", historyHeavy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_rejection_carries_the_status_code_for_callers()
    {
        // Callers distinguish "provider is down" from "provider said no". A
        // bare message forces both into the unreachable bucket and sends users
        // to restart a server that is running fine.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"unsupported parameter\"}", Encoding.UTF8, "application/json"),
        });
        using var client = NewClient(handler, contextWindowTokens: 8192, maxTokens: 256);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ChatAsync(new[] { ChatMessage.User("hi") }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    private static LmStudioClient NewClient(
        RecordingHandler handler,
        int contextWindowTokens,
        int maxTokens)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:1234") };
        return new LmStudioClient(
            new LlmClientOptions
            {
                Provider = "lmstudio",
                BaseUrl = "http://localhost:1234",
                Model = "test-model",
                MaxTokens = maxTokens,
                ContextWindowTokens = contextWindowTokens,
                MaxInputTokensSoftCap = 100_000, // isolate the context clamp from the soft cap
                ChatCompletionPath = "/v1/chat/completions",
                EnableStartupWarmup = false,
                EnableKeepWarm = false,
            },
            http);
    }

    private static HttpResponseMessage Ok(string content)
    {
        var payload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { role = "assistant", content } } },
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    private sealed record CapturedRequest(int MaxTokens, int EstimatedPromptTokens);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // Only chat completions carry a budget; ignore model-management
            // calls the client may make around them.
            if (request.RequestUri?.AbsolutePath.Contains("chat/completions", StringComparison.Ordinal) == true &&
                request.Content is not null)
            {
                var raw = await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(raw);
                var root = document.RootElement;
                var maxTokens = root.GetProperty("max_tokens").GetInt32();
                var promptChars = root.GetProperty("messages").EnumerateArray()
                    .Sum(m => m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                        ? c.GetString()!.Length
                        : 0);
                // Mirrors the client's own ~4 chars/token estimate closely
                // enough to assert the budget invariant.
                Requests.Add(new CapturedRequest(maxTokens, promptChars / 4));
            }

            return _respond(request);
        }
    }
}
