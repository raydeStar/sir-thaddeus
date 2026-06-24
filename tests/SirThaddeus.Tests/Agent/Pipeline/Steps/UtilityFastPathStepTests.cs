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
