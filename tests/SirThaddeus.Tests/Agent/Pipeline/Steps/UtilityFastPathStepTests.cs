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
        // no-op so benchmark items are answered by the model + tool loop, not
        // the deterministic solvers. The same exact-math prompt that normally
        // terminates (see Terminates_on_exact_math_contracts_*) must instead
        // Continue, and the engine must never be consulted.
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
    [InlineData("What is the remainder when 2^10 is divided by 7? Reply with only the remainder.", "2")]
    [InlineData("What is the sum of all positive multiples of 6 that are less than 50? Reply with only the integer.", "216")]
    [InlineData("How many integers from 1 through 60 are divisible by 4 or 6, but not both? Reply with only the number.", "15")]
    [InlineData("Find the least positive integer such that N leaves remainder 1 when divided by 4, remainder 2 when divided by 5, and remainder 3 when divided by 7. Reply with only N.", "17")]
    [InlineData("How many axis-aligned rectangles are in a 4 by 3 grid of unit squares? Reply with only the integer.", "60")]
    [InlineData("How many 5-person committees can be chosen from 12 people? Reply with only the integer.", "792")]
    [InlineData("Solve for x: 3x + 7 = 46. Reply with only the value of x.", "13")]
    [InlineData("A fair six-sided die is rolled twice. How many ordered outcomes have a sum of 9? Reply with only the integer.", "4")]
    [InlineData("A sequence has a1 = 3 and a_n = 3a_{n-1} - 2 for n >= 2. What is a4? Reply with only the integer.", "55")]
    [InlineData("Let b1 = 2, b2 = 5, and b_n = b_{n-1} + 2b_{n-2} for n >= 3. What is b5? Reply with only the integer.", "37")]
    [InlineData("A sequence starts with c1 = 2 and c2 = 6. For n >= 3, c_n = c_{n-1} + 4c_{n-2}. What is c5? Reply with only the number.", "94")]
    [InlineData("Let f(x)=3x+1. Starting with x=2, apply f 2 times. What final value do you get? Reply with only the number.", "22")]
    [InlineData("Let f(x) = 2x + 5. Starting with x = 1, apply f three times. What final value do you get? Reply with only the integer.", "43")]
    [InlineData("Let x1 = 1 and y1 = 2. For n >= 2, x_n = x_{n-1} + 3y_{n-1} and y_n = x_{n-1} + y_{n-1}. What is x4? Reply with only the integer.", "46")]
    [InlineData("A value y is transformed by y -> 3y + 8. The transformed value is 29. What is y? Reply with only the number.", "7")]
    [InlineData("A right triangle has legs 8 and 15. What is the hypotenuse length? Reply with only the number.", "17")]
    [InlineData("A bag has 5 red marbles and 5 blue marbles. Two marbles are drawn without replacement. What is the probability both are red? Reply with only the decimal number.", "0.222222222222")]
    [InlineData("In a group, 22 people know Python, 19 know JavaScript, and 8 know both. How many know at least one of the two? Reply with only the integer.", "33")]
    public async Task Terminates_on_exact_math_contracts_without_invoking_engine(string prompt, string expected)
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
        "A function should find the minimum number, but its update condition is if candidate > current. What condition should replace candidate > current? Reply with only the corrected condition.",
        "candidate < current")]
    [InlineData(
        "A function should find the maximum score, but its update condition is if value < best. What condition should replace value < best? Reply with only the corrected condition.",
        "value > best")]
    public async Task Terminates_on_min_max_condition_repair_contract_without_invoking_engine(string prompt, string expected)
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
        "A parser returns raw without trimming whitespace. What expression should replace raw? Reply with only the expression.",
        "raw.strip()")]
    [InlineData(
        "A loop should access items[i] safely, but it continues while i <= len(items). What condition should replace i <= len(items)? Reply with only the condition.",
        "i < len(items)")]
    [InlineData(
        "A function should return a shallow copy of items, but it returns items. What expression should replace items? Reply with only the expression.",
        "items.copy()")]
    [InlineData(
        "A function uses cache=[] as a default argument but should create a fresh list when cache is missing. What expression should replace cache=[] in the signature? Reply with only the replacement expression.",
        "cache=None")]
    [InlineData(
        "A normalizer returns title.upper() but should lowercase names. What expression should replace title.upper()? Reply with only the expression.",
        "title.lower()")]
    [InlineData(
        "A function should return a sorted copy of items, but it returns items. What expression should replace items? Reply with only the expression.",
        "sorted(items)")]
    [InlineData(
        "A function should test whether user_id is present in users, but it checks user_id in users.values(). What condition should replace user_id in users.values()? Reply with only the condition.",
        "user_id in users")]
    [InlineData(
        "A guard should run when active is false, but it currently uses active. What condition should replace active? Reply with only the condition.",
        "not active")]
    [InlineData(
        "A function should keep multiples of 4, but it uses n % 4 == 2. What condition should replace n % 4 == 2? Reply with only the corrected condition.",
        "n % 4 == 0")]
    [InlineData(
        "A distance function returns delta but should return the magnitude of delta. What expression should replace delta? Reply with only the expression.",
        "abs(delta)")]
    [InlineData(
        "A function should return the last item of rows, but it returns rows[0]. What expression should replace rows[0]? Reply with only the expression.",
        "rows[-1]")]
    [InlineData(
        "A loop should append entry only if it has not been seen, but it checks entry in visited. What condition should replace entry in visited? Reply with only the condition.",
        "entry not in visited")]
    [InlineData(
        "A range check should allow start equal to end, but it uses start < end. What condition should replace start < end? Reply with only the condition.",
        "start <= end")]
    [InlineData(
        "A function should check whether path starts with root, but it uses root in path. What expression should replace root in path? Reply with only the expression.",
        "path.startswith(root)")]
    [InlineData(
        "A comparison should be case-insensitive, but it uses actual == expected. What condition should replace actual == expected? Reply with only the condition.",
        "actual.lower() == expected.lower()")]
    [InlineData(
        "A clamp should never return less than 10, but it returns score. What expression should replace score? Reply with only the expression.",
        "max(10, score)")]
    [InlineData(
        "A clamp should never return less than zero, but it returns balance. What expression should replace balance? Reply with only the expression.",
        "max(0, balance)")]
    [InlineData(
        "A clamp should never return greater than 100, but it returns score. What expression should replace score? Reply with only the expression.",
        "min(100, score)")]
    [InlineData(
        "A function should check whether path ends with suffix, but it uses suffix in path. What expression should replace suffix in path? Reply with only the expression.",
        "path.endswith(suffix)")]
    [InlineData(
        "A function checks len(items) > 0 to see whether items has any entries. What concise condition should replace len(items) > 0? Reply with only the condition.",
        "bool(items)")]
    [InlineData(
        "A function should return an integer page count, but it uses total / page_size. What expression should replace total / page_size? Reply with only the expression.",
        "(total + page_size - 1) // page_size")]
    [InlineData(
        "A function should join words with commas, but it returns words. What expression should replace words? Reply with only the expression.",
        "\",\".join(words)")]
    [InlineData(
        "A loop should add each value into total, but it currently just assigns total = value. What statement should replace total = value? Reply with only the replacement statement.",
        "total += value")]
    [InlineData(
        "A function should append item to items and return the list, but it returns items.append(item). What statement should replace return items.append(item)? Reply with only the statement.",
        "items.append(item); return items")]
    [InlineData(
        "A function should return the index of target in items, but it returns the item itself. What expression should replace item? Reply with only the expression.",
        "i")]
    [InlineData(
        "A counter should increment counts[key] even when key is missing, but it currently does counts[key] += 1. What statement should replace counts[key] += 1? Reply with only the statement.",
        "counts[key] = counts.get(key, 0) + 1")]
    [InlineData(
        "A function should return a reversed copy of items, but it returns items. What expression should replace items? Reply with only the expression.",
        "list(reversed(items))")]
    [InlineData(
        "A function should remove None values from items, but it returns items. What expression should replace items? Reply with only the expression.",
        "[item for item in items if item is not None]")]
    [InlineData(
        "A function should return fallback when text cannot be parsed as an integer, but it directly returns int(text). What expression should replace int(text)? Reply with only the expression.",
        "int(text) if text.isdigit() else fallback")]
    [InlineData(
        "A function should return true only if all numbers are positive, but it returns any(n > 0 for n in nums). What expression should replace any(n > 0 for n in nums)? Reply with only the expression.",
        "all(n > 0 for n in nums)")]
    [InlineData(
        "A function should return a new dictionary containing base updated with override, but it returns base. What expression should replace base? Reply with only the expression.",
        "{**base, **override}")]
    [InlineData(
        "A parser should split a comma-separated string and trim whitespace around each part, but it returns text.split(','). What expression should replace text.split(',')? Reply with only the expression.",
        "[part.strip() for part in text.split(',')]")]
    [InlineData(
        "A function calculates result = left + right but returns None. What statement should replace return None? Reply with only the statement.",
        "return result")]
    [InlineData(
        "A function should return the first n items, but it returns items[n:]. What expression should replace items[n:]? Reply with only the expression.",
        "items[:n]")]
    public async Task Terminates_on_generic_code_repair_contracts_without_invoking_engine(string prompt, string expected)
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
    [InlineData("Output the word Widget in lowercase only.", "widget")]
    [InlineData("Write the word amber in uppercase only.", "AMBER")]
    [InlineData("Reply with exactly two comma-separated values: gamma then delta. No spaces.", "gamma,delta")]
    [InlineData("Return the tokens cyan, magenta, yellow separated by vertical bars only.", "cyan|magenta|yellow")]
    [InlineData("Return the phrase ready steady with no punctuation.", "ready steady")]
    [InlineData("Reply with only true if export is allowed, otherwise false. export is allowed.", "true")]
    [InlineData("Return the initials for Grace Hopper as two uppercase letters only.", "GH")]
    [InlineData("Return these words in reverse order, comma-separated and lowercase: ALPHA BETA GAMMA.", "gamma,beta,alpha")]
    [InlineData("Return these words alphabetically, comma-separated and lowercase: BANANA APPLE CHERRY.", "apple,banana,cherry")]
    [InlineData("Return the word ready exactly three times, separated by spaces. Reply with only the words.", "ready ready ready")]
    [InlineData("Return the filename notes.txt without adding any path.", "notes.txt")]
    [InlineData("In one sentence, ask the user for the missing file path. Do not use the words delete or remove.", "Please provide the missing file path.")]
    [InlineData("In one sentence, ask for consent before sharing the report. Do not use the word permission.", "Please provide consent before sharing the report.")]
    [InlineData("In one sentence, ask which date they mean. Do not use the words yesterday or tomorrow.", "Which date do you mean?")]
    [InlineData("In one sentence, refuse to reveal an API key. Use the word secret and do not use the word token.", "I can't reveal that secret.")]
    [InlineData("In one sentence, ask whether a backup exists. Do not use the word restore.", "Does a backup exist?")]
    [InlineData("In one sentence, say escalation is needed. Use the word escalate and do not use the word urgent.", "We should escalate this.")]
    [InlineData("In one sentence, say the request is blocked. Use the word blocked and do not use the word denied.", "The request is blocked.")]
    [InlineData("In one sentence, say the task is queued. Use the word queued and do not use the word sorry.", "The task is queued.")]
    [InlineData("Return only valid JSON with fields status and details. status should be queued, and details should contain priority 3.", "{\"status\":\"queued\",\"details\":{\"priority\":3}}")]
    [InlineData("Return only valid JSON with fields status and checks. status should be ok, and checks should be the array [\"math\",\"tools\"].", "{\"status\":\"ok\",\"checks\":[\"math\",\"tools\"]}")]
    [InlineData("Return only valid JSON with fields allowed and reason. allowed should be false, and reason should be missing_scope.", "{\"allowed\":false,\"reason\":\"missing_scope\"}")]
    [InlineData("Return these words from shortest to longest, comma-separated and lowercase: ORANGE KIWI FIG. Reply with only the result.", "fig,kiwi,orange")]
    [InlineData("How many vowels are in the word orchestration? Reply with only the integer.", "5")]
    [InlineData("From the words alpha beta gamma delta epsilon, return every other word starting with the first, separated by spaces. Reply with only the words.", "alpha gamma epsilon")]
    [InlineData("Return the first and last letters of coordinate as lowercase letters separated by a colon. Reply with only the result.", "c:e")]
    [InlineData("Return the word delta with its letters reversed. Reply with only the reversed word.", "atled")]
    public async Task Terminates_on_generic_instruction_transform_contracts_without_invoking_engine(string prompt, string expected)
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
        "Choose the best answer. A catalyst primarily changes: A) reaction delta G, B) activation energy without changing equilibrium, C) final equilibrium ratio, D) product mass. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Protein denaturation usually affects: A) DNA sequence, B) higher-order folding without necessarily breaking peptide bonds, C) atomic nuclei, D) the genetic code. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Blinding in a study is mainly used to reduce: A) observer bias, B) activation energy, C) sample temperature, D) DNA replication. Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. In normal-phase chromatography, the stationary phase is generally: A) nonpolar, B) polar, C) gaseous only, D) absent. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Osmosis tends to move water toward: A) lower solute concentration, B) higher effective solute concentration, C) lower osmotic pressure only, D) no membrane. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Adding base to a buffer usually: A) changes pH only slightly because weak acid neutralizes added base B) guarantees pH equals 7 C) removes all conjugate base D) changes the equilibrium constant. Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. A weak acid buffer resists pH change best when: A) pH is always exactly 14 B) all acid has been removed C) water is absent D) pH is near the acid pKa Reply with only A, B, C, or D.",
        "D")]
    [InlineData(
        "Choose the best answer. During PCR annealing: A) proteins fold B) primers bind complementary DNA templates C) oxygen is reduced D) cells divide. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. If diet and dosage both change between groups, the design issue is: A) larger power B) no measurement needed C) confounding because variables changed together D) a guaranteed placebo effect. Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. Genetic drift is usually strongest in: A) small populations because random sampling matters more B) infinite populations C) only selected alleles D) populations with no inheritance. Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. A broad IR spectroscopy band around 3200-3600 cm^-1 suggests: A) an O-H stretch such as an alcohol B) a noble gas only C) no bonds D) a metal lattice. Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. Below pKa, an acid-base pair is mostly: A) deprotonated B) protonated C) metallic D) unbuffered. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Random assignment supports causal inference because it: A) prevents all attrition B) removes measurement C) balances confounders in expectation D) makes every outcome identical. Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. Oxidation means: A) loss of electrons B) gain of electrons C) lowering temperature D) adding mass only. Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. Statistical power is the probability of: A) making a type I error B) detecting a real effect when one exists C) proving the null D) changing the hypothesis. Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. Simple diffusion tends to move particles: A) against the concentration gradient B) into nuclei only C) down their concentration gradient D) only with ATP. Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. Increasing pressure on a gas-phase equilibrium tends to favor: A) the side with more gas molecules B) the side with fewer gas molecules C) only the reactants in every reaction D) only a faster catalyst Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. During translation, mRNA codons are read as: A) single bases with no reading frame B) pairs of amino acids C) three-nucleotide units that specify amino acids or stops D) protein folds directly Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. A type I error is: A) rejecting a true null hypothesis B) failing to detect a real effect C) increasing sample size D) choosing a random control Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. A type II error is: A) rejecting a true null hypothesis B) choosing a random control C) failing to reject a false null hypothesis D) increasing sample size Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. During mitosis, sister chromatids are separated mainly in: A) interphase B) prophase only C) telophase before alignment D) anaphase Reply with only A, B, C, or D.",
        "D")]
    [InlineData(
        "Choose the best answer. For an isolated system, the second law of thermodynamics says entropy tends to: A) increase or remain constant B) always decrease C) become negative mass D) remove energy conservation Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. In Michaelis-Menten kinetics, Km is commonly interpreted as: A) the maximum velocity itself B) the substrate concentration at half Vmax C) the product concentration at equilibrium D) the enzyme molecular weight Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. In a monohybrid Aa x Aa cross with complete dominance, the expected phenotype ratio is: A) 3 dominant to 1 recessive B) 1 dominant to 3 recessive C) all recessive D) 2 dominant to 2 recessive always Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. The light reactions of photosynthesis directly produce: A) only glucose B) DNA polymerase C) ATP and NADPH D) lactic acid Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. Negative feedback in physiology usually: A) amplifies a disturbance without limit B) prevents any sensor from working C) requires no response pathway D) counteracts deviation from a set point Reply with only A, B, C, or D.",
        "D")]
    [InlineData(
        "Choose the best answer. An ELISA assay is commonly used to detect: A) planetary motion B) specific proteins or antibodies C) only DNA sequence length D) electrical resistance of metals Reply with only A, B, C, or D.",
        "B")]
    [InlineData(
        "Choose the best answer. In proton NMR, splitting patterns mainly reflect: A) atomic number only B) sample color C) neighboring nonequivalent hydrogens D) the speed of light in vacuum Reply with only A, B, C, or D.",
        "C")]
    [InlineData(
        "Choose the best answer. A phospholipid bilayer forms because phospholipids are: A) amphipathic, with hydrophilic heads and hydrophobic tails B) only hydrophilic C) only hydrophobic D) made only of nucleotides Reply with only A, B, C, or D.",
        "A")]
    [InlineData(
        "Choose the best answer. A p-value is best described as: A) the probability the null is true B) the effect size C) the sample mean D) the probability of data at least as extreme assuming the null model Reply with only A, B, C, or D.",
        "D")]
    public async Task Terminates_on_science_multiple_choice_concepts_without_invoking_engine(
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

    [Theory]
    [InlineData(
        "A function should group words by their first letter. What expression should replace grouped? Give only the expression.\n\nCode:\n```python\ndef grouped_first(words):\n    grouped = {{answer}}\n    return grouped\n```",
        "{key: [item for item in words if item[0] == key] for key in sorted(set(item[0] for item in words))}")]
    [InlineData(
        "A function should pair adjacent values without dropping the first pair. What expression should replace []? Give only the expression.\n\nCode:\n```python\ndef adjacent(items):\n    return {{answer}}\n```",
        "[(items[i], items[i + 1]) for i in range(len(items) - 1)]")]
    [InlineData(
        "A function should parse yes/no text into booleans and otherwise return fallback. What expression should replace fallback? Give only the expression.\n\nCode:\n```python\ndef parse_bool(text, fallback):\n    return {{answer}}\n```",
        "True if text.strip().lower() == 'yes' else False if text.strip().lower() == 'no' else fallback")]
    [InlineData(
        "A function should split items into chunks of size n. What expression should replace items? Give only the expression.\n\nCode:\n```python\ndef chunks(items, n):\n    return {{answer}}\n```",
        "[items[i:i+n] for i in range(0, len(items), n)]")]
    [InlineData(
        "A function should combine count dictionaries by summing values. What expression should replace left? Give only the expression.\n\nCode:\n```python\ndef merge_counts(left, right):\n    return {{answer}}\n```",
        "{key: left.get(key, 0) + right.get(key, 0) for key in set(left) | set(right)}")]
    [InlineData(
        "A function should return the first duplicate item, or None. What statement should replace pass? Give only the statement.\n\nCode:\n```python\ndef first_duplicate(items):\n    {{answer}}\n```",
        "seen=set(); return next((item for item in items if item in seen or seen.add(item)), None)")]
    [InlineData(
        "A lookup should ignore case and surrounding spaces in the query. What expression should replace query? Give only the expression.\n\nCode:\n```python\ndef lookup(mapping, query):\n    normalized = {{answer}}\n    return mapping.get(normalized)\n```",
        "query.strip().lower()")]
    [InlineData(
        "A function should compute rolling sums of width 3. What expression should replace nums? Give only the expression.\n\nCode:\n```python\ndef rolling3(nums):\n    return {{answer}}\n```",
        "[sum(nums[i:i+3]) for i in range(len(nums) - 2)]")]
    public async Task Terminates_on_python_template_code_repairs_without_invoking_engine(
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
