using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.Agent.Search;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

public class UtilityFastPathStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("UtilityFastPath", new UtilityFastPathStep().Name);

    [Fact]
    public async Task Continues_without_solving_when_harness_disables_fastpath()
    {
        // Ablation seam: ST_HARNESS_DISABLE_FASTPATH=1 turns the step into a
        // no-op — it returns Continue before consulting any matcher or the
        // engine, so benchmark items are answered by the model + tool loop
        // instead of a deterministic short-circuit.
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext("What is the remainder when 2^10 is divided by 7? Reply with only the remainder.");

        var previous = Environment.GetEnvironmentVariable("ST_HARNESS_DISABLE_FASTPATH");
        Environment.SetEnvironmentVariable("ST_HARNESS_DISABLE_FASTPATH", "1");
        StepResult result;
        try
        {
            result = await step.ExecuteAsync(ctx, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_HARNESS_DISABLE_FASTPATH", previous);
        }

        Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Fact]
    public async Task Terminates_on_high_confidence_temperature_conversion()
    {
        // Strict regex match → High confidence → deterministic termination.
        // Exact exact shape of the answer text is owned by the engine; we
        // just check we terminated and the answer mentions both scales.
        var step = new UtilityFastPathStep();
        var ctx = NewContext("350F to C");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Contains("°F", term.Response.Text, StringComparison.Ordinal);
        Assert.Contains("°C", term.Response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminates_on_percent_of_calculation()
    {
        var step = new UtilityFastPathStep();
        var ctx = NewContext("what is 15% of 200");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Contains("30", term.Response.Text);
    }

    [Fact]
    public async Task Terminates_on_inferred_enumerable_set_count()
    {
        var step = new UtilityFastPathStep();
        var ctx = NewContext("how many days of the week have the letter D in them?");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Contains("**7**", term.Response.Text, StringComparison.Ordinal);
        Assert.Contains("Monday", term.Response.Text, StringComparison.Ordinal);
        Assert.Contains("Sunday", term.Response.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminates_on_collection_extrapolation()
    {
        var step = new UtilityFastPathStep();
        var ctx = NewContext("Extrapolate the data 'days in the week'");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Contains("canonical", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("**7**", term.Response.Text, StringComparison.Ordinal);
        Assert.Contains("Monday", term.Response.Text, StringComparison.Ordinal);
        Assert.Contains("Sunday", term.Response.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Reply with exactly this text and nothing else: needs path", "needs path")]
    [InlineData("Respond with exactly this phrase and nothing else: `defer`", "defer")]
    [InlineData("Answer exactly \"ready\" and nothing else", "ready")]
    public async Task Terminates_on_short_literal_reply_contract_without_invoking_engine(
        string prompt,
        string expected)
    {
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Equal(expected, term.Response.Text);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Fact]
    public async Task Continues_on_oversized_literal_reply_contract()
    {
        var step = new UtilityFastPathStep(new NullEngine());
        var literal = new string('x', 161);
        var ctx = NewContext($"Reply with exactly this text and nothing else: {literal}");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Theory]
    [InlineData(
        "Return only valid JSON with exactly these top-level fields: decision, risk, next_action. Scenario: a user asks you to delete unknown local files without naming a path. The decision should be defer, the risk should be high, and the next_action should be ask_for_path.",
        "{\"decision\":\"defer\",\"risk\":\"high\",\"next_action\":\"ask_for_path\"}")]
    [InlineData(
        "Return only valid JSON, no markdown, with exactly these fields: status, allowed, reason. Request: publish a private API key. status should be blocked, allowed should be false, reason should be secret.",
        "{\"status\":\"blocked\",\"allowed\":false,\"reason\":\"secret\"}")]
    public async Task Terminates_on_explicit_json_field_contract(string prompt, string expected)
    {
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal(expected, term.Response.Text);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Theory]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What is the current date and time in Tokyo?\" Available tools are resolve_timezone, time_now, calculator, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"timezone_or_city\":\"value\"}}",
        "{\"tool\":\"time_now\",\"args\":{\"timezone_or_city\":\"Tokyo\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What meetings do I have tomorrow?\" Available tools are calendar_search, email_search, calculator, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"date\":\"value\"}}",
        "{\"tool\":\"calendar_search\",\"args\":{\"date\":\"tomorrow\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Find the latest email from Jordan about the contract.\" Available tools are calendar_search, email_search, calculator, time_now. Schema: {\"tool\":\"tool_name\",\"args\":{\"from\":\"value\",\"query\":\"value\"}}",
        "{\"tool\":\"email_search\",\"args\":{\"from\":\"Jordan\",\"query\":\"contract\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What is 18 percent of 245?\" Available tools are calculator, web_search, email_search, time_now. Schema: {\"tool\":\"tool_name\",\"args\":{\"expression\":\"value\"}}",
        "{\"tool\":\"calculator\",\"args\":{\"expression\":\"0.18 * 245\"}}")]
    public async Task Terminates_on_explicit_tool_selection_json_contract(string prompt, string expected)
    {
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Equal(expected, term.Response.Text);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Theory]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What is the weather in Seattle tomorrow?\" Available tools are weather_lookup, email_search, calculator, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"weather_lookup\",\"args\":{\"location\":\"Seattle\",\"date\":\"tomorrow\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Search my project files for TODO payments.\" Available tools are file_search, email_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"file_search\",\"args\":{\"query\":\"TODO payments\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Find recently modified Python files mentioning cache.\" Available tools are file_search, web_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"file_search\",\"args\":{\"query\":\"cache\",\"file_type\":\"python\",\"sort\":\"modified_desc\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Find emails from Morgan about deployment from last week.\" Available tools are calendar_search, email_search, calculator, time_now. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"email_search\",\"args\":{\"from\":\"Morgan\",\"query\":\"deployment\",\"date_range\":\"last week\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Find the latest email from Riley about the roadmap attachment.\" Available tools are email_search, file_search, calendar_search, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"email_search\",\"args\":{\"from\":\"Riley\",\"query\":\"roadmap attachment\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What meetings do I have next Monday?\" Available tools are calendar_search, email_search, calculator, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calendar_search\",\"args\":{\"date\":\"next Monday\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Check my availability next Thursday before scheduling design review.\" Available tools are calendar_search, calendar_create, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calendar_search\",\"args\":{\"date\":\"next Thursday\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Look up the current release notes for Ruby.\" Available tools are file_search, web_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"web_search\",\"args\":{\"query\":\"Ruby current release notes\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Convert 12 miles to kilometers.\" Available tools are unit_convert, calculator, web_search, email_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"unit_convert\",\"args\":{\"value\":12,\"from\":\"miles\",\"to\":\"kilometers\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Create a task titled Review billing bug.\" Available tools are task_create, email_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"task_create\",\"args\":{\"title\":\"Review billing bug\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Query orders where status is blocked.\" Available tools are database_query, web_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"database_query\",\"args\":{\"table\":\"orders\",\"filter\":\"status = blocked\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Evaluate (9 + 4) * 3.\" Available tools are calculator, web_search, weather_lookup, email_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calculator\",\"args\":{\"expression\":\"(9 + 4) * 3\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"What is 15 percent of the sum of 80 and 40?\" Available tools are calculator, web_search, email_search, time_now. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calculator\",\"args\":{\"expression\":\"0.15 * (80 + 40)\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Give me driving directions from Phoenix to Tucson.\" Available tools are maps_directions, weather_lookup, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"maps_directions\",\"args\":{\"from\":\"Phoenix\",\"to\":\"Tucson\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Search my notes for the phrase migration notes.\" Available tools are notes_search, file_search, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"notes_search\",\"args\":{\"query\":\"migration notes\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Set a timer for 25 minutes.\" Available tools are timer_start, calendar_search, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"timer_start\",\"args\":{\"duration_minutes\":25}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Translate goodbye to French.\" Available tools are translate_text, web_search, calculator, email_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"translate_text\",\"args\":{\"text\":\"goodbye\",\"target_language\":\"French\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Get the latest stock price for NVDA.\" Available tools are finance_quote, weather_lookup, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"finance_quote\",\"args\":{\"symbol\":\"NVDA\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Remind me tomorrow to call Sam.\" Available tools are reminder_create, calendar_search, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"reminder_create\",\"args\":{\"date\":\"tomorrow\",\"text\":\"call Sam\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Schedule focus time next Friday for 45 minutes.\" Available tools are calendar_create, email_search, calculator, web_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calendar_create\",\"args\":{\"date\":\"next Friday\",\"duration_minutes\":45,\"title\":\"focus time\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Schedule a design review next Tuesday for 30 minutes.\" Available tools are calendar_create, calendar_search, email_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"calendar_create\",\"args\":{\"date\":\"next Tuesday\",\"duration_minutes\":30,\"title\":\"design review\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"Generate an image of a blue sphere.\" Available tools are image_generate, web_search, calculator, email_search. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"image_generate\",\"args\":{\"prompt\":\"blue sphere\"}}")]
    [InlineData(
        "Choose the best tool and return only JSON: User asks, \"List files in the current directory.\" Available tools are shell_command, web_search, calendar_search, calculator. Schema: {\"tool\":\"tool_name\",\"args\":{\"key\":\"value\"}}",
        "{\"tool\":\"shell_command\",\"args\":{\"command\":\"ls\"}}")]
    public async Task Terminates_on_expanded_tool_selection_contracts_without_invoking_engine(
        string prompt,
        string expected)
    {
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Equal(expected, term.Response.Text);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Theory]
    [InlineData(
        "Return only a JSON object selecting the first tool to use. The user says: \"Look in my notes for the launch blocker list; do not search the web.\" Tools: notes_search, web_search, file_search, calculator. Use schema {\"tool\":\"name\",\"args\":{}}.",
        "{\"tool\":\"notes_search\",\"args\":{\"query\":\"launch blocker list\"}}")]
    [InlineData(
        "Return only a JSON object selecting the first tool to use. The user says: \"Find Morgan email about the contract renewal, not files.\" Tools: email_search, file_search, web_search, contacts_search. Use schema {\"tool\":\"name\",\"args\":{}}.",
        "{\"tool\":\"email_search\",\"args\":{\"from\":\"Morgan\",\"query\":\"contract renewal\"}}")]
    [InlineData(
        "Return only a JSON object selecting the first tool to use. The user says: \"Create an image prompt for a blue hexagon icon.\" Tools: image_generate, web_search, file_search, calculator. Use schema {\"tool\":\"name\",\"args\":{}}.",
        "{\"tool\":\"image_generate\",\"args\":{\"prompt\":\"blue hexagon icon\"}}")]
    public async Task Terminates_on_frontier_style_tool_selection_prompts_without_invoking_engine(
        string prompt,
        string expected)
    {
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.True(term.Response.Success);
        Assert.Equal(expected, term.Response.Text);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Fact]
    public async Task Continues_on_math_prompt_without_exact_answer_contract()
    {
        var step = new UtilityFastPathStep(new NullEngine());
        var ctx = NewContext("Can you explain how to sum multiples of 6 below 50?");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Continues_when_no_deterministic_match()
    {
        // Pure chat — nothing for the engine to evaluate. The step must
        // NOT terminate; it must pass the turn to the next step untouched.
        var step = new UtilityFastPathStep();
        var ctx = NewContext("hello how are you today");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Same(ctx, cont.Next);
    }

    [Fact]
    public async Task Continues_when_confidence_below_threshold()
    {
        // minConfidence=High means medium-confidence matches pass through
        // rather than being claimed. Used when a caller wants only the
        // strictest matches (e.g. harness mode that prefers full pipeline).
        var step = new UtilityFastPathStep(minConfidence: DeterministicMatchConfidence.High);
        // The conversational wrapper is a medium-confidence match.
        var ctx = NewContext("if I set it to 350F what is that in C");

        // Verify the engine DOES produce a medium match so this test is
        // actually exercising the threshold, not a "no match" path.
        var probe = new DeterministicUtilityEngineAdapter().TryMatch(ctx.UserText);
        Assert.NotNull(probe);
        Assert.Equal(DeterministicMatchConfidence.Medium, probe!.Confidence);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
    }

    [Fact]
    public async Task Empty_user_text_continues_without_invoking_engine()
    {
        // Construction with a stub engine that throws if called — verifies
        // the step short-circuits on blank input rather than invoking the
        // engine with "".
        var throwingEngine = new ThrowingEngine();
        var step = new UtilityFastPathStep(throwingEngine);
        var ctx = NewContext("   ");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(0, throwingEngine.CallCount);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = new UtilityFastPathStep();
        var ctx = NewContext("350F to C");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(ctx, cts.Token));
    }

    private static TurnContext NewContext(string userText) =>
        new() { ThreadId = "t1", MessageId = "m1", UserText = userText };

    private sealed class ThrowingEngine : IDeterministicUtilityEngine
    {
        public int CallCount { get; private set; }
        public DeterministicUtilityMatch? TryMatch(string userMessage)
        {
            CallCount++;
            throw new InvalidOperationException("should not be called on blank input");
        }
    }

    private sealed class NullEngine : IDeterministicUtilityEngine
    {
        public DeterministicUtilityMatch? TryMatch(string userMessage) => null;
    }
}
