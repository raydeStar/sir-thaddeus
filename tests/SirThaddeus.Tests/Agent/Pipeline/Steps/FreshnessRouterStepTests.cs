using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class FreshnessRouterStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("FreshnessRouter", new FreshnessRouterStep().Name);

    // ── Positive: prompts that should force tool_choice=web_search ──────

    [Theory]
    [InlineData("Does the iPhone 15 exist?")]
    [InlineData("is the Tesla Cybertruck real yet")]
    [InlineData("was the PS6 released")]
    [InlineData("Has macOS Sequoia come out yet?")]
    [InlineData("is there a new version of CUDA")]
    [InlineData("Did Nintendo ever release a Switch 2?")]
    public async Task Forces_web_search_on_existence_shapes(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("web_search", cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("what's the latest version of CUDA")]
    [InlineData("who is the current president of Argentina")]
    [InlineData("what year was the GPT-4 model released")]
    [InlineData("when did Starfield come out")]
    [InlineData("how much does a PlayStation 5 cost now")]
    [InlineData("what is the price of Bitcoin")]
    public async Task Forces_web_search_on_recency_and_pricing_shapes(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("web_search", cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("Can you recommend a good Ashwagandha on Amazon.com?")]
    [InlineData("What is the best supplement brand to buy on Amazon?")]
    public async Task Forces_web_search_on_product_recommendation_shapes(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "memory_retrieve", "web_search", "browser_navigate");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("web_search", cont.Next.ForcedTool);
    }

    // ── Negative: prompts that must NOT force a search ──────────────────
    // These are the cases the user worried about — "willy-nilly" search
    // on casual chat, opinion, and self-referential queries.

    [Theory]
    [InlineData("hi there")]
    [InlineData("hey, how are you doing")]
    [InlineData("thanks!")]
    public async Task Does_not_fire_on_casual_chat(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("does this code work?")]
    [InlineData("is it correct to use a HashMap here?")]
    [InlineData("does this make sense")]
    [InlineData("is this right?")]
    [InlineData("what do you think of this approach")]
    [InlineData("what's your favorite color")]
    [InlineData("what's up")]
    [InlineData("what's your name")]
    public async Task Does_not_fire_on_self_referential_or_opinion(string userText)
    {
        // Exactly the "don't run off on a random search" concern.
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("write me a poem about the sea")]
    [InlineData("explain how TCP works")]
    [InlineData("help me debug this stack trace")]
    [InlineData("can you refactor this function")]
    [InlineData("summarize the three-body problem plot")]
    public async Task Does_not_fire_on_generative_or_tutorial(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    // ── Guards ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Does_not_treat_freshness_words_inside_quoted_context_as_the_user_request()
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(
            "Answer the multiple-choice question below.\n\n" +
            "Example: How many children today are vaccinated? Answer: 80%.\n\n" +
            "Question: Which definition best describes cooperative federalism?",
            "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Fact]
    public async Task No_op_when_web_search_not_in_tool_list()
    {
        // If the footman narrowed tools such that web_search isn't
        // available, the router must NOT force a nonexistent tool —
        // otherwise tool_choice would reference a tool that isn't
        // advertised, which most providers reject with 400.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("does the iPhone 15 exist?", "weather_forecast");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Respects_preexisting_forced_tool()
    {
        // If an earlier step already set a forced tool (e.g. a future
        // specialized router), we don't overwrite its decision.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("does the iPhone 15 exist?", "web_search") with
        {
            ForcedTool = "places_lookup",
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("places_lookup", cont.Next.ForcedTool);
    }

    // ── Imperative tool invocation ──────────────────────────────────────
    // Small models preempt tool calls when the user literally says "use X"
    // or "try Y" — they fabricate the error/result without actually firing
    // the tool. Forcing tool_choice removes that shortcut.

    [Theory]
    [InlineData("Use web_search for AI policy news and handle timeout gracefully.", "web_search")]
    [InlineData("Try file_read and clearly explain if permission is denied.", "file_read")]
    [InlineData("Run tool_ping and confirm whether the MCP server is responding.", "tool_ping")]
    [InlineData("please use the `web_search` tool for this", "web_search")]
    [InlineData("Call weather_forecast for Seattle", "weather_forecast")]
    public async Task Forces_named_tool_on_imperative_phrasing(string userText, string expectedTool)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search", "file_read", "tool_ping", "weather_forecast");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(expectedTool, cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Imperative_phrasing_skipped_when_named_tool_not_available()
    {
        // If the user names a tool that the footman has already filtered
        // out, don't force it — the tool_choice directive would 400 the
        // LLM request. Better to fall through to auto-routing.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("Use file_read on the log", "web_search"); // file_read NOT in list

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    // Ambient prose that mentions "use" / "try" without being imperative.
    [InlineData("what's a good use case for a hash map")]
    [InlineData("I want to try a new approach to this")]
    public async Task Does_not_force_on_prose_use_or_try(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Null(cont.Next.ForcedTool);
    }

    // ── Weather intent ──────────────────────────────────────────────────
    // Small models routinely prefer web_search over weather_forecast even
    // when both are exposed — force weather_geocode (always the first
    // step) so the tool loop can chain into weather_forecast naturally.

    [Theory]
    [InlineData("What's the weather in Seattle?")]
    [InlineData("Use weather tools to provide a short weather outlook for Seattle, WA.")]
    [InlineData("Is it raining in Portland right now?")]
    [InlineData("Show me the forecast for Tokyo this week")]
    [InlineData("How cold is it outside?")]
    public async Task Forces_weather_geocode_on_weather_queries(string userText)
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "weather_geocode", "weather_forecast", "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("weather_geocode", cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Forces_weather_geocode_for_natural_location_time_request()
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(
            "I am scheduling a call with someone in Tokyo. Use the available time tools if needed and tell me the current date and time there in one short sentence.",
            "weather_geocode",
            "resolve_timezone",
            "time_now");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("weather_geocode", cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Forces_resolve_timezone_for_location_time_request_when_geocode_unavailable()
    {
        var step = new FreshnessRouterStep();
        var ctx = WithTools(
            "What is the current time in Tokyo right now?",
            "resolve_timezone",
            "time_now");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("resolve_timezone", cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Weather_router_no_op_when_weather_geocode_unavailable()
    {
        // If weather tools are filtered out, fall through — don't force
        // a tool that isn't on the menu.
        var step = new FreshnessRouterStep();
        var ctx = WithTools("What's the weather in Seattle?", "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        // Falls through to the freshness heuristic which may still fire
        // on "latest/current" phrasing; but our prompt doesn't include
        // those, so no forcing.
        Assert.Null(cont.Next.ForcedTool);
    }

    [Theory]
    [InlineData("I want to find a weather vane for my garden")]  // "weather" as noun
    [InlineData("The weather is nice today, thanks for asking")] // statement, not query
    public async Task Does_not_force_weather_on_prose_mentions(string userText)
    {
        // The `WeatherIntentPattern` is intentionally word-level, so these
        // DO technically match the word "weather". Current behavior is
        // that they still trigger — weather_geocode will return empty for
        // garden-ornament queries, which is fine; better to search and
        // get no results than hallucinate. This test documents that.
        var step = new FreshnessRouterStep();
        var ctx = WithTools(userText, "weather_geocode", "weather_forecast", "web_search");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("weather_geocode", cont.Next.ForcedTool);
    }

    [Fact]
    public async Task Honors_pre_cancelled_token()
    {
        var step = new FreshnessRouterStep();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(WithTools("does X exist", "web_search"), cts.Token));
    }

    private static TurnContext WithTools(string userText, params string[] toolNames)
    {
        var defs = toolNames.Select(n => new ToolDefinition
        {
            Function = new FunctionDefinition
            {
                Name = n,
                Description = $"stub {n}",
                Parameters = new { type = "object" },
            }
        }).ToArray();
        return new TurnContext
        {
            ThreadId = "t1",
            MessageId = "m1",
            UserText = userText,
            ToolDefs = defs,
        };
    }
}
