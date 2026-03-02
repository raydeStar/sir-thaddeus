using System.Net;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

// ─────────────────────────────────────────────────────────────────────────
// Thinking Model Tests
//
// Verifies that the system handles chain-of-thought models (e.g.
// lfm2.5-1.2b-thinking, DeepSeek-R1, QwQ) correctly:
//
//   1. StripThinkingScaffold — removes <think> blocks, handles multiples,
//      partial/unclosed tags, labelled sections, and "Final Answer:" patterns.
//   2. IsThinkingModel / EffectiveMaxTokens — auto-detection and token boost.
//   3. LmStudioClient transport — strips <think> before returning to callers,
//      preserves reasoning_content, handles think-before-tool-call.
// ─────────────────────────────────────────────────────────────────────────

public class StripThinkingScaffoldTests
{
    [Fact]
    public void PlainText_Unchanged()
    {
        var result = OrchestratorMessageHelpers.StripThinkingScaffold("Hello, world.");
        Assert.Equal("Hello, world.", result);
    }

    [Fact]
    public void SingleThinkBlock_IsStripped()
    {
        var input = "<think>I need to reason about this carefully.</think>The answer is 42.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("The answer is 42.", result);
    }

    [Fact]
    public void MultipleThinkBlocks_AllStripped()
    {
        var input = "<think>Step 1.</think>First part. <think>Step 2.</think>Final answer here.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("First part. Final answer here.", result);
    }

    [Fact]
    public void UnclosedThinkBlock_ContentAfterTagDiscarded()
    {
        var input = "Some preamble. <think>This was truncated by max_tokens";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("Some preamble.", result);
    }

    [Fact]
    public void OnlyThinkBlock_ReturnsEmpty()
    {
        var input = "<think>pure reasoning only</think>";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.True(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void UnclosedThinkBlock_OnlyThink_ReturnsEmpty()
    {
        var input = "<think>truncated with no close tag and nothing before it";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.True(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void FinalAnswerLabel_ExtractsAnswer()
    {
        var input = "Let me reason...\n\nFinal Answer: The capital is Paris.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("The capital is Paris.", result);
    }

    [Fact]
    public void ThinkBlockThenFinalAnswer_BothHandled()
    {
        var input = "<think>Thinking deeply...</think>Some text. Final Answer: 7.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("7.", result);
    }

    [Fact]
    public void LabelledThinkingSection_AnswerExtracted()
    {
        var input = "Thinking:\nLet me work through this step by step.\n\nAnswer: The result is 5.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("The result is 5.", result);
    }

    [Fact]
    public void LabelledReasoningSection_AnswerExtracted()
    {
        var input = "Reasoning:\nAnalyzing the problem...\n\nResponse: Here is the summary.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("Here is the summary.", result);
    }

    [Fact]
    public void PreserveRationale_True_ReturnsOriginal()
    {
        var input = "<think>Some internal reasoning.</think>The answer is 42.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input, preserveRationale: true);
        Assert.Equal(input, result);
    }

    [Fact]
    public void CaseInsensitive_ThinkTag_Stripped()
    {
        var input = "<THINK>uppercase think tag</THINK>Clean output.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("Clean output.", result);
    }

    [Fact]
    public void NullAndWhitespace_ReturnedAsIs()
    {
        Assert.Equal("", OrchestratorMessageHelpers.StripThinkingScaffold(""));
        Assert.Equal("   ", OrchestratorMessageHelpers.StripThinkingScaffold("   "));
    }

    [Fact]
    public void ThinkBeforeAnswer_LeadingWhitespace_Trimmed()
    {
        var input = "<think>\n  lots of reasoning\n</think>\n\n  The answer is here.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("The answer is here.", result);
    }
}

// ─────────────────────────────────────────────────────────────────────────

public class IsThinkingModelTests
{
    [Theory]
    [InlineData("lfm2.5-1.2b-thinking", true)]
    [InlineData("lfm2.5-3b-thinking", true)]
    [InlineData("deepseek-r1-distill-qwen-7b", true)]
    [InlineData("qwq-32b-preview", true)]
    [InlineData("o1-preview", true)]
    [InlineData("o1-mini", true)]
    [InlineData("model-think-v2", true)]
    [InlineData("lfm2.5-1.2b", false)]              // NON-thinking LFM
    [InlineData("lfm2.5-3b", false)]                  // NON-thinking LFM
    [InlineData("qwen2.5-7b-instruct", false)]
    [InlineData("qwen2.5-1.5b-instruct", false)]      // gatekeeper
    [InlineData("llama-3.2-3b-instruct", false)]
    [InlineData("mistral-7b-instruct-v0.3", false)]
    [InlineData("phi-3.5-mini-instruct", false)]
    [InlineData("gemma-2-9b-it", false)]               // guard: no false-pos on 'gemma'
    [InlineData("gemma-2-r1-it", false)]               // guard: no false-pos on '-r1'
    [InlineData("", false)]
    public void IsThinkingModel_CorrectlyDetects(string modelName, bool expected)
    {
        var options = new LlmClientOptions { Model = modelName };
        Assert.Equal(expected, options.IsThinkingModel());
    }

    [Theory]
    [InlineData("lfm2.5-1.2b-thinking", 2048, 4096)]   // boosted to minimum
    [InlineData("lfm2.5-1.2b-thinking", 8192, 8192)]   // configured value larger, kept
    [InlineData("lfm2.5-1.2b-thinking", 4096, 4096)]   // exactly at minimum
    [InlineData("lfm2.5-1.2b",          2048, 2048)]   // non-thinking LFM: unchanged
    [InlineData("lfm2.5-1.2b",          512,  512)]    // non-thinking LFM: unchanged
    [InlineData("qwen2.5-7b-instruct",  2048, 2048)]   // non-thinking: unchanged
    [InlineData("qwen2.5-7b-instruct",  512,  512)]    // non-thinking: unchanged
    public void EffectiveMaxTokens_BoostsOnlyForThinkingModels(
        string model, int configured, int expectedEffective)
    {
        var options = new LlmClientOptions { Model = model, MaxTokens = configured };
        Assert.Equal(expectedEffective, options.EffectiveMaxTokens());
    }

    [Theory]
    [InlineData("lfm2.5-1.2b-thinking", 200, 4096)]    // explicit override boosted
    [InlineData("lfm2.5-1.2b-thinking", 8000, 8000)]   // explicit override kept
    [InlineData("qwen2.5-7b-instruct",  200,  200)]    // non-thinking: override unchanged
    public void EffectiveMaxTokens_WithExplicitOverride(
        string model, int explicitOverride, int expectedEffective)
    {
        var options = new LlmClientOptions { Model = model, MaxTokens = 2048 };
        Assert.Equal(expectedEffective, options.EffectiveMaxTokens(explicitOverride));
    }
}

// ─────────────────────────────────────────────────────────────────────────

public class LmStudioClientThinkingTests
{
    private static readonly ChatMessage[] SimpleMessages =
    [
        ChatMessage.System("You are a helpful assistant."),
        ChatMessage.User("What is 2+2?")
    ];

    private static LmStudioClient MakeClient(string model, Action<string>? onRequest = null)
    {
        var options = new LlmClientOptions
        {
            BaseUrl   = "http://localhost:1234",
            Model     = model,
            MaxTokens = 2048,
        };
        var handler = new SequenceHttpHandler([], onRequest);
        return new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });
    }

    [Fact]
    public async Task ThinkBlock_InContent_IsStrippedBeforeReturn()
    {
        var rawContent = "<think>Let me think about 2+2. Obviously it is 4.</think>The answer is 4.";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Equal("The answer is 4.", response.Content);
        Assert.DoesNotContain("<think>", response.Content ?? "");
    }

    [Fact]
    public async Task MultipleThinkBlocks_AllStrippedBeforeReturn()
    {
        var rawContent = "<think>First pass.</think>Part one. <think>Second pass.</think>Part two.";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Equal("Part one. Part two.", response.Content);
    }

    [Fact]
    public async Task TruncatedThinkBlock_ContentBeforeTagPreserved()
    {
        // Simulates max_tokens cutting off mid-think with some content before the tag
        var rawContent = "Here is what I know. <think>truncated by token limit";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Equal("Here is what I know.", response.Content);
        Assert.DoesNotContain("<think>", response.Content ?? "");
    }

    [Fact]
    public async Task PureThinkBlock_ContentBecomesNull()
    {
        // Model emitted only thinking, no actual answer
        var rawContent = "<think>just thinking</think>";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        Assert.Null(response.Content);
    }

    [Fact]
    public async Task ReasoningContentField_SurfacedInResponse()
    {
        // LM Studio ≥ 0.3.x exposes reasoning_content as a separate field
        var json = JsonSerializer.Serialize(new
        {
            id      = "test-123",
            choices = new[]
            {
                new
                {
                    index         = 0,
                    message       = new
                    {
                        role              = "assistant",
                        content           = "The answer is 4.",
                        reasoning_content = "I reasoned that 2+2=4."
                    },
                    finish_reason = "stop"
                }
            }
        });

        var handler = new SequenceHttpHandler([(System.Net.HttpStatusCode.OK, json)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.Equal("The answer is 4.", response.Content);
        Assert.Equal("I reasoned that 2+2=4.", response.ReasoningContent);
    }

    [Fact]
    public async Task ThinkingModel_MaxTokensBoostedInRequest()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler(
            [MakeContentResponse("ok")],
            body => capturedBody = body);

        var options = new LlmClientOptions
        {
            BaseUrl   = "http://localhost:1234",
            Model     = "lfm2.5-1.2b-thinking",
            MaxTokens = 2048,   // below the 4096 minimum
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var maxTokens = doc.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(4096, maxTokens);
    }

    [Fact]
    public async Task NonThinkingModel_MaxTokensUnchanged()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler(
            [MakeContentResponse("ok")],
            body => capturedBody = body);

        var options = new LlmClientOptions
        {
            BaseUrl   = "http://localhost:1234",
            Model     = "qwen2.5-7b-instruct",
            MaxTokens = 2048,
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var maxTokens = doc.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(2048, maxTokens);
    }

    [Fact]
    public async Task ThinkingModel_LargeConfiguredTokens_NotReduced()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler(
            [MakeContentResponse("ok")],
            body => capturedBody = body);

        var options = new LlmClientOptions
        {
            BaseUrl   = "http://localhost:1234",
            Model     = "deepseek-r1-distill-qwen-7b",
            MaxTokens = 8192,   // already above the minimum boost
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var maxTokens = doc.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(8192, maxTokens);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static (System.Net.HttpStatusCode, string) MakeContentResponse(string content) =>
        (System.Net.HttpStatusCode.OK, JsonSerializer.Serialize(new
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
}

// ─────────────────────────────────────────────────────────────────────────
// Non-Thinking Model Regression Tests
//
// Verifies that all thinking-model fixes are inert for non-thinking models.
// The lfm2.5-1.2b (instruct, not thinking) must be completely unaffected.
// ─────────────────────────────────────────────────────────────────────────

public class NonThinkingModelRegressionTests
{
    private static readonly ChatMessage[] SimpleMessages =
    [
        ChatMessage.System("You are a helpful assistant."),
        ChatMessage.User("Hello!")
    ];

    [Fact]
    public async Task NonThinkingModel_ContentPreservedExactly()
    {
        // Content should pass through byte-for-byte (no trimming, no mutation)
        var rawContent = "  Hello, I'm your assistant.  ";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.True(response.IsComplete);
        // Must be preserved exactly — no .Trim() side-effects
        Assert.Equal(rawContent, response.Content);
    }

    [Fact]
    public async Task NonThinkingModel_MaxTokensUnchanged()
    {
        string? capturedBody = null;
        var handler = new SequenceHttpHandler(
            [MakeContentResponse("ok")],
            body => capturedBody = body);

        var options = new LlmClientOptions
        {
            BaseUrl   = "http://localhost:1234",
            Model     = "lfm2.5-1.2b",
            MaxTokens = 2048,
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        await client.ChatAsync(SimpleMessages);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        var maxTokens = doc.RootElement.GetProperty("max_tokens").GetInt32();
        Assert.Equal(2048, maxTokens);
    }

    [Fact]
    public async Task NonThinkingModel_ReasoningContentIsNull()
    {
        var handler = new SequenceHttpHandler([MakeContentResponse("Hello!")]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        Assert.Null(response.ReasoningContent);
    }

    [Fact]
    public void StripThinkingScaffold_PlainResponse_Unchanged()
    {
        // Normal assistant text with no think tags must pass through unchanged
        var input = "The capital of France is Paris. It is located in the Île-de-France region.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripThinkingScaffold_ContentWithAngleBrackets_NotStripped()
    {
        // Angle brackets that aren't think tags must survive
        var input = "Use <b>bold</b> for emphasis. The formula is x < 5 and y > 3.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripThinkingScaffold_CodeBlock_Preserved()
    {
        // Code blocks that might contain think-like patterns
        var input = "Here is some XML:\n```xml\n<config>\n  <setting>value</setting>\n</config>\n```";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void StripThinkingScaffold_FinalAnswerInNormalContext_BehaviorPreserved()
    {
        // "Final Answer:" is stripped even for non-thinking models (pre-existing behavior).
        // This test documents the behavior, not guarding against regression.
        var input = "Let me calculate. Final Answer: 42.";
        var result = OrchestratorMessageHelpers.StripThinkingScaffold(input);
        Assert.Equal("42.", result);
    }

    [Fact]
    public async Task NonThinkingModel_ContentWithQuotedThinkTag_StripsIt()
    {
        // Edge case: non-thinking model output that literally contains <think>.
        // This IS expected to be stripped — it's a safety net.
        // Documenting the behavior so we know.
        var rawContent = "The <think> tag is used for chain-of-thought.</think> Rest.";
        var handler = new SequenceHttpHandler([MakeContentResponse(rawContent)]);
        var options = new LlmClientOptions
        {
            BaseUrl = "http://localhost:1234",
            Model   = "lfm2.5-1.2b",
            MaxTokens = 2048
        };
        using var client = new LmStudioClient(options, new HttpClient(handler)
        {
            BaseAddress = new Uri(options.BaseUrl)
        });

        var response = await client.ChatAsync(SimpleMessages);

        // The content between <think> and </think> gets stripped, leaving "The" + " Rest."
        Assert.DoesNotContain("<think>", response.Content ?? "");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static (System.Net.HttpStatusCode, string) MakeContentResponse(string content) =>
        (System.Net.HttpStatusCode.OK, JsonSerializer.Serialize(new
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
}
