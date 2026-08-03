using System.Collections.Concurrent;
using System.Text.Json;
using SirThaddeus.Agent;
using SirThaddeus.Agent.Pipeline;
using SirThaddeus.Agent.Pipeline.Steps;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests.Agent.Pipeline.Steps;

[Collection(RoutingLatencyEnvironmentCollection.Name)]
public class ToolLoopStepTests
{
    [Fact]
    public void Name_matches_conventional_step_name() =>
        Assert.Equal("ToolLoop", BuildStep(new FakeLlm()).Name);

    [Fact]
    public async Task Continues_with_assistant_draft_when_model_returns_no_tool_calls()
    {
        // Simplest possible path: LLM replies with plain text on the
        // first round. The step hands a Continue with AssistantDraft set
        // to the downstream post-process + composer steps — it does NOT
        // terminate the pipeline on its own.
        var llm = new FakeLlm(LlmReply.Final("hello there"));
        var sink = new CapturingSink();
        var step = BuildStep(llm, sink: sink);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("hello there", cont.Next.AssistantDraft);
        Assert.Empty(cont.Next.ToolCallsMade);
        Assert.Empty(sink.ToolStarted);
        Assert.Empty(sink.ToolCompleted);
    }

    [Fact]
    public async Task Executes_tool_calls_and_emits_start_complete_events()
    {
        // Round 1: LLM asks for web_search. Round 2: LLM produces final text.
        // Event pair should fire for the web_search call; context should
        // carry the call record + draft out to the next step.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"cats\"}"),
            LlmReply.Final("cats are furry"));
        var mcp = new StubMcp(toolName => "search result for " + toolName);
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("cats are furry", cont.Next.AssistantDraft);
        Assert.Single(cont.Next.ToolCallsMade);
        Assert.Equal("web_search", cont.Next.ToolCallsMade[0].ToolName);
        Assert.True(cont.Next.ToolCallsMade[0].Success);

        // Paired events for the single tool call.
        var started = Assert.Single(sink.ToolStarted);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.Equal(started.ActivityId, completed.ActivityId);
        Assert.Equal("web_search", started.Tool);
        Assert.True(completed.Ok);
    }

    [Fact]
    public async Task Permission_denial_records_failure_and_skips_mcp()
    {
        // Gate denies; the step must not call MCP, must surface an error
        // completion event, and must feed a denial stub back into history
        // so the model can continue gracefully.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{}"),
            LlmReply.Final("okay, no search"));
        var mcp = new StubMcp(_ => throw new InvalidOperationException("should not be called"));
        var sink = new CapturingSink();
        var gate = new DeterministicGate(allow: false, reason: "blocked by policy");

        var step = BuildStep(llm, mcp: mcp, sink: sink, gate: gate);
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.False(completed.Ok);
        Assert.Contains("blocked by policy", completed.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wiki_mutation_target_without_operation_blocks_write_before_mcp()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(
                "wiki_page_update_by_name",
                "{\"rootName\":\"Project Archive\",\"pageTitle\":\"Overview\",\"markdown\":\"wrong\"}"),
            LlmReply.Final("I stopped because that was outside the selected target."));
        var mcpCalled = false;
        var mcp = new StubMcp(_ =>
        {
            mcpCalled = true;
            return "should not run";
        });
        var logEntries = new List<(string Event, string Message)>();
        var step = BuildStep(llm, mcp: mcp, log: (name, message) => logEntries.Add((name, message)));
        var previousTrace = Environment.GetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE");
        Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", "1");
        try
        {
            var context = NewContext() with
            {
                ToolDefs = [ToolDefinitionFor("wiki_page_update_by_name")],
                WikiMutationTarget = new WikiMutationTarget(
                    WikiMutationTargetKind.Page,
                    "root-1",
                    "Project",
                    "page-1",
                    "Plan"),
            };

            var result = await step.ExecuteAsync(context, CancellationToken.None);

            var next = Assert.IsType<StepResult.Continue>(result).Next;
            Assert.False(mcpCalled);
            var call = Assert.Single(next.ToolCallsMade);
            Assert.False(call.Success);
            Assert.Contains("wiki_explicit_operation_required", call.Result, StringComparison.Ordinal);
            Assert.Contains(logEntries, entry =>
                entry.Event == "EXPERIMENT_ACTIVATION" &&
                entry.Message.Contains("event=wiki_explicit_operation_gate", StringComparison.Ordinal) &&
                entry.Message.Contains("decision=activated", StringComparison.Ordinal));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ST_ROUTING_LATENCY_TRACE", previousTrace);
        }
    }

    [Fact]
    public async Task Wiki_mutation_target_with_approved_operation_allows_bound_write_to_reach_mcp()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(
                "wiki_page_update_by_name",
                "{\"markdown\":\"right\"}"),
            LlmReply.Final("Updated."));
        var mcpCalled = false;
        var mcp = new StubMcp(_ =>
        {
            mcpCalled = true;
            return "{\"ok\":true,\"version\":2}";
        });
        var step = BuildStep(llm, mcp: mcp);
        var context = NewContext() with
        {
            ToolDefs = [ToolDefinitionFor("wiki_page_update_by_name")],
            WikiMutationTarget = new WikiMutationTarget(
                WikiMutationTargetKind.Page,
                "root-1",
                "Project",
                "page-1",
                "Plan",
                WikiMutationOperation.PageUpdate),
        };

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.True(mcpCalled);
    }

    [Fact]
    public async Task Selected_target_without_operation_projects_reads_only_and_logs_activation()
    {
        var llm = new FakeLlm(LlmReply.Final("No write operation is approved."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var context = NewContext() with
        {
            ToolDefs =
            [
                ToolDefinitionFor("wiki_page_read"),
                ToolDefinitionFor("wiki_page_update_by_name"),
                ToolDefinitionFor("web_search"),
            ],
            WikiMutationTarget = new WikiMutationTarget(
                WikiMutationTargetKind.Page,
                "root-1",
                "Project",
                "page-1",
                "Plan"),
        };

        await step.ExecuteAsync(context, CancellationToken.None);

        var advertised = Assert.Single(llm.ReceivedTools)!;
        Assert.Equal(
            ["wiki_page_read", "web_search"],
            advertised.Select(tool => tool.Function.Name));
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_explicit_operation_gate", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Approved_wiki_effect_forces_payload_only_tool_and_binds_runtime_target_once()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("wiki_page_update_by_name", "{\"markdown\":\"Approved replacement\"}"),
            LlmReply.Final("Updated."));
        var mcpCalls = new List<(string Tool, string Arguments)>();
        var mcp = new StubMcp((tool, arguments) =>
        {
            mcpCalls.Add((tool, arguments));
            return "{\"ok\":true,\"version\":2}";
        });
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, mcp: mcp, log: (eventName, message) => logs.Add((eventName, message)));
        var context = NewContext() with
        {
            ToolDefs =
            [
                ToolDefinitionFor("wiki_page_update_by_name"),
                ToolDefinitionFor("wiki_page_read"),
            ],
            WikiMutationTarget = new WikiMutationTarget(
                WikiMutationTargetKind.Page,
                "root-1",
                "Project",
                "page-1",
                "Plan",
                WikiMutationOperation.PageUpdate),
        };

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var next = Assert.IsType<StepResult.Continue>(result).Next;
        Assert.Equal("Updated.", next.AssistantDraft);
        Assert.Equal(["wiki_page_update_by_name", null], llm.ForcedToolNames);

        var projected = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ToolDefinition>>(llm.ReceivedTools[0]));
        Assert.Equal("wiki_page_update_by_name", projected.Function.Name);
        var projectedSchema = JsonSerializer.Serialize(projected.Function.Parameters);
        Assert.Contains("markdown", projectedSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("rootName", projectedSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("pageTitle", projectedSchema, StringComparison.Ordinal);
        Assert.Null(llm.ReceivedTools[1]);

        var execution = Assert.Single(mcpCalls);
        Assert.Equal("wiki_page_update_by_name", execution.Tool);
        using var arguments = JsonDocument.Parse(execution.Arguments);
        Assert.Equal("Project", arguments.RootElement.GetProperty("rootName").GetString());
        Assert.Equal("Plan", arguments.RootElement.GetProperty("pageTitle").GetString());
        Assert.Equal("Approved replacement", arguments.RootElement.GetProperty("markdown").GetString());
        Assert.Single(next.ToolCallsMade);
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_bound_effect", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_page_read_forces_one_payload_only_call_and_binds_runtime_page()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("wiki_page_read", "{\"pageId\":\"invented\",\"maxChars\":1200}"),
            LlmReply.Final("The launch code is CIRRUS."));
        var mcpCalls = new List<(string Tool, string Arguments)>();
        var mcp = new StubMcp((tool, arguments) =>
        {
            mcpCalls.Add((tool, arguments));
            return "{\"markdown\":\"Launch code: CIRRUS\"}";
        });
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, mcp: mcp, log: (eventName, message) => logs.Add((eventName, message)));
        var context = NewContext() with
        {
            ToolDefs =
            [
                ToolDefinitionFor("wiki_page_read"),
                ToolDefinitionFor("wiki_page_update_by_name"),
                ToolDefinitionFor("web_search"),
            ],
            WikiMutationTarget = new WikiMutationTarget(
                WikiMutationTargetKind.Page,
                "root-1",
                "Project",
                "page-opaque-1",
                "Plan",
                WikiMutationOperation.PageRead),
        };

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        var next = Assert.IsType<StepResult.Continue>(result).Next;
        Assert.Equal("The launch code is CIRRUS.", next.AssistantDraft);
        Assert.Equal(["wiki_page_read", null], llm.ForcedToolNames);
        var projected = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ToolDefinition>>(llm.ReceivedTools[0]));
        Assert.Equal("wiki_page_read", projected.Function.Name);
        var schema = JsonSerializer.Serialize(projected.Function.Parameters);
        Assert.Contains("maxChars", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("pageId", schema, StringComparison.Ordinal);
        Assert.Null(llm.ReceivedTools[1]);

        var execution = Assert.Single(mcpCalls);
        using var arguments = JsonDocument.Parse(execution.Arguments);
        Assert.Equal("page-opaque-1", arguments.RootElement.GetProperty("pageId").GetString());
        Assert.Equal(1200, arguments.RootElement.GetProperty("maxChars").GetInt32());
        Assert.Single(next.ToolCallsMade);
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_explicit_read_operation", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Interceptor_claims_call_and_MCP_is_not_invoked()
    {
        // An interceptor that owns the tool name short-circuits MCP —
        // this is how the runtime hooks propose_automation.
        var llm = new FakeLlm(
            LlmReply.Tool("propose_automation", "{\"name\":\"x\"}"),
            LlmReply.Final("draft sent"));
        var mcpCalled = false;
        var mcp = new StubMcp(_ => { mcpCalled = true; return "should not happen"; });
        var interceptor = new NamedInterceptor("propose_automation",
            new ToolCallOutcome("proposal accepted", Ok: true, Error: null));
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink, interceptors: new[] { interceptor });
        var ctx = NewContext();

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        Assert.False(mcpCalled);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.True(completed.Ok);
        Assert.Equal("propose_automation", completed.Tool);
    }

    [Fact]
    public async Task Args_rewriter_mutates_arguments_before_execution()
    {
        // The rewriter is our seam for runtime-specific tweaks (e.g. force
        // recency=week on web_search inside an automation run). It must
        // run before both the interceptor chain and MCP.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"a\"}"),
            LlmReply.Final("done"));
        string? observedArgs = null;
        var mcp = new StubMcp((tool, args) => { observedArgs = args; return "ok"; });
        var rewriter = new InlineArgsRewriter((ctx, name, args) =>
            name == "web_search" ? "{\"q\":\"a\",\"recency\":\"week\"}" : args);

        var step = BuildStep(llm, mcp: mcp, argsRewriters: new[] { rewriter });

        await step.ExecuteAsync(NewContext(), CancellationToken.None);

        Assert.NotNull(observedArgs);
        Assert.Contains("recency", observedArgs);
    }

    [Fact]
    public async Task Synthesizes_current_time_after_timezone_lookup()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(ToolNames.WeatherGeocode, "{\"location\":\"Tokyo, Japan\"}"),
            LlmReply.Tool(ToolNames.ResolveTimezone, "{\"countryCode\":\"JP\",\"latitude\":35.6768601,\"longitude\":139.7638947}"),
            LlmReply.Tool(ToolNames.TimeNow, "{}"),
            LlmReply.Final("I cannot provide the live time."));
        var mcp = new StubMcp((tool, _) => tool switch
        {
            var name when string.Equals(name, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) =>
                "[Weather geocode: 3 result(s), source=photon]",
            var name when string.Equals(name, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) =>
                "[Timezone lookup: timezone=Asia/Tokyo, source=open-meteo]",
            var name when string.Equals(name, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase) =>
                "{\"iso\":\"2026-05-07T20:29:14.0325030-06:00\",\"unix_ms\":1778207354032,\"timezone\":\"Mountain Standard Time\",\"offset\":\"-06:00\"}",
            _ => "ok"
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = NewContext() with
        {
            UserText = "What time is it in Tokyo, Japan right now?",
            LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User("What time is it in Tokyo, Japan right now?") },
            ToolDefs = new[]
            {
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.WeatherGeocode, Description = "geocode", Parameters = new { } } },
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.ResolveTimezone, Description = "timezone", Parameters = new { } } },
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.TimeNow, Description = "time", Parameters = new { } } },
            }
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(3, cont.Next.ToolCallsMade.Count);
        Assert.Contains(cont.Next.ToolCallsMade, call => string.Equals(call.ToolName, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("currently", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tokyo, Japan", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("open-meteo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time_now=", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Synthesizes_current_time_from_json_timezone_payload_without_advertised_defs()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(ToolNames.WeatherGeocode, "{\"location\":\"Tokyo, Japan\"}"),
            LlmReply.Tool(ToolNames.ResolveTimezone, "{\"countryCode\":\"JP\",\"latitude\":35.6768601,\"longitude\":139.7638947}"),
            LlmReply.Final("I cannot provide the live time."));
        var mcp = new StubMcp((tool, _) => tool switch
        {
            var name when string.Equals(name, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) =>
                "{\"results\":[{\"name\":\"Tokyo\"}],\"source\":\"photon\"}",
            var name when string.Equals(name, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) =>
                "{\"timezone\":\"Asia/Tokyo\",\"source\":\"open-meteo\"}",
            _ => "ok"
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = NewContext() with
        {
            UserText = "What time is it in Tokyo, Japan right now?",
            LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User("What time is it in Tokyo, Japan right now?") },
            ToolDefs = Array.Empty<ToolDefinition>()
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(2, cont.Next.ToolCallsMade.Count);
        Assert.Contains("currently", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Tokyo, Japan", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("open-meteo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot provide", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chains_timezone_and_time_now_for_natural_location_time_request()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(ToolNames.WeatherGeocode, "{\"location\":\"Tokyo\"}"),
            LlmReply.Tool(ToolNames.ResolveTimezone, "{\"countryCode\":\"JP\",\"latitude\":35.6768601,\"longitude\":139.7638947}"),
            LlmReply.Final("I cannot provide the live time."));
        var mcp = new StubMcp((tool, _) => tool switch
        {
            var name when string.Equals(name, ToolNames.WeatherGeocode, StringComparison.OrdinalIgnoreCase) =>
                "{\"results\":[{\"name\":\"Tokyo\",\"latitude\":35.6768601,\"longitude\":139.7638947}],\"source\":\"photon\"}",
            var name when string.Equals(name, ToolNames.ResolveTimezone, StringComparison.OrdinalIgnoreCase) =>
                "{\"timezone\":\"Asia/Tokyo\",\"source\":\"open-meteo\"}",
            var name when string.Equals(name, ToolNames.TimeNow, StringComparison.OrdinalIgnoreCase) =>
                "{\"iso\":\"2026-05-07T20:29:14.0325030-06:00\",\"unix_ms\":1778207354032,\"timezone\":\"Mountain Standard Time\",\"offset\":\"-06:00\"}",
            _ => "ok"
        });
        var step = BuildStep(llm, mcp: mcp);
        var prompt = "I am scheduling a call with someone in Tokyo. Use the available time tools if needed and tell me the current date and time there in one short sentence.";
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User(prompt) },
            ToolDefs = new[]
            {
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.WeatherGeocode, Description = "geocode", Parameters = new { } } },
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.ResolveTimezone, Description = "timezone", Parameters = new { } } },
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.TimeNow, Description = "time", Parameters = new { } } },
            }
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal(3, cont.Next.ToolCallsMade.Count);
        Assert.Contains("Tokyo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Asia/Tokyo", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("time_now=", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("one short sentence", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot provide", cont.Next.AssistantDraft, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mcp_exception_is_surfaced_as_failed_outcome_not_thrown()
    {
        // The step's contract is "run the loop to completion"; an MCP
        // tool that throws should produce a failed ToolCallRecord and a
        // completed event with ok=false — not bubble out of ExecuteAsync.
        var llm = new FakeLlm(
            LlmReply.Tool("flaky", "{}"),
            LlmReply.Final("handled"));
        var mcp = new StubMcp(_ => throw new InvalidOperationException("boom"));
        var sink = new CapturingSink();

        var step = BuildStep(llm, mcp: mcp, sink: sink);

        var result = await step.ExecuteAsync(NewContext(), CancellationToken.None);

        Assert.IsType<StepResult.Continue>(result);
        var completed = Assert.Single(sink.ToolCompleted);
        Assert.False(completed.Ok);
        Assert.Contains("boom", completed.Error);
    }

    [Fact]
    public async Task Hits_round_trip_cap_with_deterministic_message()
    {
        // LLM keeps asking for tools forever. After MaxRoundTrips we bail
        // with a plain-language recovery message so the UI doesn't spin
        // or leak internal loop terminology to the user.
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"a\"}"),
            LlmReply.Tool("web_search", "{\"q\":\"b\"}"),
            LlmReply.Tool("web_search", "{\"q\":\"c\"}"));
        var mcp = new StubMcp(_ => "ok");

        var step = BuildStep(llm, mcp: mcp, maxRoundTrips: 2);

        var result = await step.ExecuteAsync(NewContext(), CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Contains("got stuck", term.Response.Text);
        Assert.DoesNotContain("round-trip cap", term.Response.Text);
        Assert.DoesNotContain("Tool-call loop", term.Response.Text);
        Assert.Equal(2, term.Response.LlmRoundTrips);
    }

    [Fact]
    public async Task Honours_pre_cancelled_token()
    {
        var step = BuildStep(new FakeLlm(LlmReply.Final("never runs")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            step.ExecuteAsync(NewContext(), cts.Token));
    }

    [Fact]
    public async Task Caps_llm_output_budget_for_tool_loop_rounds()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("web_search", "{\"q\":\"cats\"}"),
            LlmReply.Final("cats are furry"));
        var step = BuildStep(llm, mcp: new StubMcp(_ => "search result"));
        var ctx = NewContext() with { ForcedTool = "web_search" };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(new[] { 1024, 1024 }, llm.MaxTokenOverrides);
        Assert.Equal(new[] { "web_search", null }, llm.ForcedToolNames);
    }

    [Fact]
    public async Task Calculator_parse_error_nudges_model_to_retry_with_expression()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("calculator", "{\"expression\":\"sum of all positive multiples of 6 less than 50\"}"),
            LlmReply.Tool("calculator", "{\"expression\":\"6+12+18+24+30+36+42+48\"}"),
            LlmReply.Final("216"));
        var mcp = new StubMcp((_, args) =>
            args.Contains("6+12+18+24+30+36+42+48", StringComparison.Ordinal)
                ? "{\"expression\":\"6+12+18+24+30+36+42+48\",\"result\":\"216\"}"
                : "{\"error\":\"Could not evaluate expression: Error parsing the expression. The calculator only accepts a pure arithmetic expression, not prose or a word problem.\"}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = NewContext() with
        {
            UserText = "What is the sum of all positive multiples of 6 that are less than 50?",
            LlmMessages =
            [
                ChatMessage.System("sys"),
                ChatMessage.User("What is the sum of all positive multiples of 6 that are less than 50?"),
            ],
            ToolDefs =
            [
                new ToolDefinition { Function = new FunctionDefinition { Name = "calculator", Description = "calc", Parameters = new { } } },
            ],
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("216", cont.Next.AssistantDraft);
        Assert.Equal(3, llm.ReceivedMessages.Count);
        Assert.Contains(
            llm.ReceivedMessages[1],
            message => message.Role == "system" &&
                (message.Content?.Contains("Call calculator again with only a pure expression", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Equal("calculator", llm.ForcedToolNames[1]);
    }

    [Fact]
    public async Task Calculator_turn_adds_setup_hint_before_first_model_call()
    {
        var llm = new FakeLlm(LlmReply.Final("37"));
        var step = BuildStep(llm);
        var prompt = "Use the calculator tool for each arithmetic step. Let b1 = 2, b2 = 5, and b_n = b_{n-1} + 2b_{n-2} for n >= 3. What is b5? Reply with only the integer.";
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User(prompt) },
            ToolDefs = new[]
            {
                new ToolDefinition { Function = new FunctionDefinition { Name = "calculator", Description = "calc", Parameters = new { } } },
            },
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var firstRound = Assert.Single(llm.ReceivedMessages);
        Assert.Contains(
            firstRound,
            message => message.Role == "system" &&
                (message.Content?.Contains("Preserve operand order exactly from formulas", StringComparison.OrdinalIgnoreCase) ?? false) &&
                (message.Content?.Contains("recurrences or indexed sequences", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    [Fact]
    public async Task Strict_calculator_integer_turn_uses_latest_successful_tool_result()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("calculator", "{\"expression\":\"6+12+18+24+30+36+42+48\"}"),
            LlmReply.Final("48"));
        var mcp = new StubMcp(_ => "{\"expression\":\"6+12+18+24+30+36+42+48\",\"result\":\"216\"}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = NewContext() with
        {
            UserText = "Use the calculator tool to compute the arithmetic. What is the sum of all positive multiples of 6 that are less than 50? Reply with only the integer.",
            LlmMessages =
            [
                ChatMessage.System("sys"),
                ChatMessage.User("Use the calculator tool to compute the arithmetic. What is the sum of all positive multiples of 6 that are less than 50? Reply with only the integer."),
            ],
            ToolDefs =
            [
                new ToolDefinition { Function = new FunctionDefinition { Name = "calculator", Description = "calc", Parameters = new { } } },
            ],
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("216", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Strict_python_integer_turn_uses_latest_successful_stdout()
    {
        // Observed live: the model's second python attempt printed the correct
        // 111 (exit 0) and the model still answered "1". The latest successful
        // bare-number stdout must win the strict-integer turn.
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(collatz(27))\"}"),
            LlmReply.Final("1"));
        var mcp = new StubMcp(_ => "{\"stdout\":\"111\\n\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        var prompt = "Use the python_eval tool to compute this. Starting from 27, how many Collatz steps does it take to reach 1? Reply with only the integer.";
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                new ToolDefinition { Function = new FunctionDefinition { Name = "python_eval", Description = "sandbox", Parameters = new { } } },
            ],
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("111", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Exact_duplicate_successful_python_calls_reuse_the_turn_local_result()
    {
        const string args = "{\"code\":\"print(73)\"}";
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", args),
            LlmReply.Tool("python_eval", args),
            LlmReply.Final("73"));
        var mcpCalls = 0;
        var mcp = new StubMcp(_ =>
        {
            mcpCalls++;
            return "{\"stdout\":\"73\\n\",\"exit_code\":0}";
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use python_eval and return just the number.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("73", cont.Next.AssistantDraft);
        Assert.Equal(2, cont.Next.ToolCallsMade.Count);
        Assert.Equal(1, mcpCalls);
    }

    [Fact]
    public async Task Failed_python_calls_are_not_cached()
    {
        const string args = "{\"code\":\"print(missing_name)\"}";
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", args),
            LlmReply.Tool("python_eval", args),
            LlmReply.Final("unable to compute"));
        var mcpCalls = 0;
        var mcp = new StubMcp(_ =>
        {
            mcpCalls++;
            return "{\"stdout\":\"\",\"stderr\":\"NameError\",\"exit_code\":1}";
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use python_eval and return just the number.");

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal(2, mcpCalls);
    }

    [Theory]
    [InlineData("Count the matching rows with python_eval and answer with just a number.")]
    [InlineData("Use python_eval for the calculation; output only an integer.")]
    [InlineData("Please run python_eval and give me the numeric value only.")]
    public async Task Numeric_only_contract_accepts_natural_paraphrases(string prompt)
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(73)\"}"),
            LlmReply.Final("I calculated seventy."));
        var mcp = new StubMcp(_ => "{\"stdout\":\"73\\n\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("73", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Numeric_only_contract_uses_actual_tool_call_without_tool_name_in_prompt()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(73)\"}"),
            LlmReply.Final("70"));
        var mcp = new StubMcp(_ => "{\"stdout\":\"73\\n\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        const string prompt = "Count the matching rows and return just the number.";
        var ctx = ComputeContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("73", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Explanatory_compute_request_does_not_adopt_bare_tool_result_as_entire_answer()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(73)\"}"),
            LlmReply.Final("The result is 73 because each matching row was counted once."));
        var mcp = new StubMcp(_ => "{\"stdout\":\"73\\n\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        const string prompt = "Only calculate a number if it helps, then explain the method.";
        var ctx = ComputeContext(prompt);

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("The result is 73 because each matching row was counted once.", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Numeric_only_contract_accepts_numeric_json_result_and_scientific_notation()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("calculator", "{\"expression\":\"-1250\"}"),
            LlmReply.Final("approximately -1,250"));
        var mcp = new StubMcp(_ => "{\"result\":-1.25e3}");
        var step = BuildStep(llm, mcp: mcp);
        const string prompt = "Calculate the result and respond with just a numeric value.";
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                new ToolDefinition { Function = new FunctionDefinition { Name = "calculator", Description = "calc", Parameters = new { } } },
            ],
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("-1.25e3", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Failed_python_script_stdout_is_never_adopted_as_strict_answer()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(x)\"}"),
            LlmReply.Final("42"));
        var mcp = new StubMcp(_ => "{\"stdout\":\"13\\n\",\"stderr\":\"NameError\",\"exit_code\":1}");
        var step = BuildStep(llm, mcp: mcp);
        var prompt = "Use the python_eval tool to compute this. Reply with only the integer.";
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                new ToolDefinition { Function = new FunctionDefinition { Name = "python_eval", Description = "sandbox", Parameters = new { } } },
            ],
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        // Exit code 1 → the printed 13 is from a broken script; keep the model's draft.
        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("42", cont.Next.AssistantDraft);
    }

    [Fact]
    public async Task Places_discover_success_builds_local_business_draft_without_second_llm_round()
    {
        var llm = new FakeLlm(
            LlmReply.Tool(ToolNames.PlacesDiscover, "{\"query\":\"florist nearby\"}"),
            LlmReply.Final("should not run"));
        var mcp = new StubMcp((tool, _) => tool == ToolNames.PlacesDiscover
            ? "{" +
              "\"provider\":\"osm_overpass\"," +
              "\"resolvedLocation\":\"Olympia, Washington, US\"," +
              "\"results\":[" +
              "{\"name\":\"Fleurae\",\"address\":\"101 Capitol Way S, Olympia, WA\",\"distanceMeters\":420,\"osmUrl\":\"https://www.openstreetmap.org/node/1\"}," +
              "{\"name\":\"Buds and Blooms\",\"address\":\"517 Washington St SE, Olympia, WA\",\"distanceMeters\":180,\"osmUrl\":\"https://www.openstreetmap.org/node/2\"}" +
              "]" +
              "}"
            : "");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = NewContext() with
        {
            UserText = "Is there a florist nearby?",
            LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User("Is there a florist nearby?") },
            ToolDefs = new[]
            {
                new ToolDefinition { Function = new FunctionDefinition { Name = ToolNames.PlacesDiscover, Description = "places", Parameters = new { } } },
            },
        };

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var term = Assert.IsType<StepResult.Terminate>(result);
        Assert.Contains("florists", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Fleurae", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Buds and Blooms", term.Response.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Single(term.Response.ToolCallsMade);
        Assert.Equal(2, term.Response.Sources.Count);
        Assert.All(term.Response.Sources, source => Assert.Equal("openstreetmap.org", source.Domain));
        Assert.Single(llm.MaxTokenOverrides);
    }

    // ── compute interventions ─────────────────────────────────────────

    // Intervention 1(a): python exit-1 SyntaxError → repair nudge injected
    // (mentions multi-line + print) and python_eval forced next round; the
    // model's fixed second script wins.
    [Fact]
    public async Task Python_syntax_error_injects_repair_nudge_and_forces_retry()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"def f(): x=0; for i in range(10): x+=i; return x\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"x = 0\\nfor i in range(10):\\n    x += i\\nprint(x)\"}"),
            LlmReply.Final("45"));
        var mcp = new StubMcp((_, args) =>
            args.Contains("print(x)", StringComparison.Ordinal)
                ? "{\"stdout\":\"45\\n\",\"exit_code\":0}"
                : "{\"stdout\":\"\",\"stderr\":\"SyntaxError: invalid syntax\",\"exit_code\":1}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("45", cont.Next.AssistantDraft);
        // The repair nudge must land before the second model call.
        Assert.Contains(
            llm.ReceivedMessages[1],
            message => message.Role == "system" &&
                (message.Content?.Contains("MULTI-LINE", StringComparison.OrdinalIgnoreCase) ?? false) &&
                (message.Content?.Contains("print(", StringComparison.OrdinalIgnoreCase) ?? false));
        // python_eval was forced for the repair round.
        Assert.Equal("python_eval", llm.ForcedToolNames[1]);
    }

    // Intervention 1(b): exit-0 with empty stdout → same repair nudge.
    [Fact]
    public async Task Python_empty_stdout_injects_repair_nudge_and_forces_retry()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"x = 1 + 1\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(2)\"}"),
            LlmReply.Final("2"));
        var mcp = new StubMcp((_, args) =>
            args.Contains("print(2)", StringComparison.Ordinal)
                ? "{\"stdout\":\"2\\n\",\"exit_code\":0}"
                : "{\"stdout\":\"\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("2", cont.Next.AssistantDraft);
        Assert.Contains(
            llm.ReceivedMessages[1],
            message => message.Role == "system" &&
                (message.Content?.Contains("printed nothing", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Equal("python_eval", llm.ForcedToolNames[1]);
    }

    // Intervention 1(c): three consecutive failures → only 2 repair nudges,
    // then the loop proceeds normally (no infinite loop).
    [Fact]
    public async Task Python_repair_nudge_is_capped_at_two_per_turn()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"boom1\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"boom2\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"boom3\"}"),
            LlmReply.Final("I could not compute a clean result."));
        var mcp = new StubMcp(_ => "{\"stdout\":\"\",\"stderr\":\"SyntaxError\",\"exit_code\":1}");
        var step = BuildStep(llm, mcp: mcp, maxRoundTrips: 6);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        // Loop terminated cleanly on the model's final text — no runaway.
        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("I could not compute a clean result.", cont.Next.AssistantDraft);
        Assert.Equal(3, cont.Next.ToolCallsMade.Count);
        // Exactly two rounds were force-directed to python_eval by the repair
        // nudge (after the 1st and 2nd failures); the 3rd failure does not nudge.
        Assert.Equal(2, llm.ForcedToolNames.Count(name => name == "python_eval"));
    }

    // Intervention 2(d): digit_sum_power incident. call1 prints "37" (exit 0),
    // call2 prints "1" (exit 0), model finalizes → reconciliation message names
    // 37 and 1, one forced python round returns "37" → final draft "37".
    [Fact]
    public async Task Compute_disagreement_triggers_reconciliation_and_adopts_reconciled_value()
    {
        var callIndex = 0;
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(sum(int(d) for d in str(2**30)))\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(str(2**30)[0::-1])\"}"),
            LlmReply.Final("1"),
            LlmReply.Tool("python_eval", "{\"code\":\"n = 2**30\\nprint(sum(int(d) for d in str(n)))\"}"),
            LlmReply.Final("37"));
        var mcp = new StubMcp(_ =>
        {
            callIndex++;
            return callIndex switch
            {
                1 => "{\"stdout\":\"37\\n\",\"exit_code\":0}",
                2 => "{\"stdout\":\"1\\n\",\"exit_code\":0}",
                _ => "{\"stdout\":\"37\\n\",\"exit_code\":0}",
            };
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool. What is the digit sum of 2**30? Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("37", cont.Next.AssistantDraft);
        // The reconciliation message named both disagreeing values.
        Assert.Contains(
            llm.ReceivedMessages.SelectMany(round => round),
            message => message.Role == "system" &&
                (message.Content?.Contains("returned different values", StringComparison.OrdinalIgnoreCase) ?? false) &&
                (message.Content?.Contains("37", StringComparison.Ordinal) ?? false) &&
                (message.Content?.Contains("1", StringComparison.Ordinal) ?? false));
        // Exactly one reconciliation round was forced to python_eval after the finalize.
        Assert.Equal("python_eval", llm.ForcedToolNames[^2]);
        Assert.Equal(3, cont.Next.ToolCallsMade.Count);
    }

    // Intervention 2(e): agreement case — two successful calls both "111", model
    // finalizes → NO reconciliation round, draft "111".
    [Fact]
    public async Task Compute_agreement_skips_reconciliation()
    {
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(111)\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(100+11)\"}"),
            LlmReply.Final("wrong transcription"));
        var mcp = new StubMcp(_ => "{\"stdout\":\"111\\n\",\"exit_code\":0}");
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("111", cont.Next.AssistantDraft);
        // Only two tool calls — no forced reconciliation round happened.
        Assert.Equal(2, cont.Next.ToolCallsMade.Count);
        Assert.DoesNotContain(
            llm.ReceivedMessages.SelectMany(round => round),
            message => message.Role == "system" &&
                (message.Content?.Contains("returned different values", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    // Regression (measured live, divisibility_count 3/5 -> 0/5): the repair
    // nudge must NOT fire when a successful compute value already exists.
    // Call 1 printed the correct 401; the model's redundant sympy "verify"
    // then failed. Nudging the model to "fix" that doomed verify goaded it
    // into a wrong-but-successful competitor (201) that tied against the
    // right answer. With the gate, the failed verify is ignored and the
    // turn keeps 401.
    [Fact]
    public async Task Repair_nudge_does_not_fire_once_a_successful_value_exists()
    {
        var callIndex = 0;
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"count = sum(1 for i in range(1,1001) if (i%3==0 or i%5==0) and i%15!=0)\\nprint(count)\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"from sympy import lcm; print(...)\"}"),
            LlmReply.Final("201"));
        var mcp = new StubMcp(_ =>
        {
            callIndex++;
            return callIndex == 1
                ? "{\"stdout\":\"401\\n\",\"exit_code\":0}"
                : "{\"stdout\":\"\",\"stderr\":\"ModuleNotFoundError: No module named 'sympy'\",\"exit_code\":1}";
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("401", cont.Next.AssistantDraft);
        Assert.Equal(2, cont.Next.ToolCallsMade.Count);
        Assert.DoesNotContain(
            llm.ReceivedMessages.SelectMany(round => round),
            message => message.Role == "system" &&
                (message.Content?.Contains("printed nothing", StringComparison.OrdinalIgnoreCase) ?? false));
    }

    // Regression companion: when the vote is split 1-1 (reconciliation cap
    // already spent, model declines the forced round), the OLDEST value wins —
    // the taxonomy showed corrupted values come from later verify calls.
    [Fact]
    public async Task Split_vote_resolves_to_oldest_value()
    {
        var callIndex = 0;
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(401)\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(201)\"}"),
            LlmReply.Final("201"),
            LlmReply.Final("201"));
        var mcp = new StubMcp(_ =>
        {
            callIndex++;
            return callIndex == 1
                ? "{\"stdout\":\"401\\n\",\"exit_code\":0}"
                : "{\"stdout\":\"201\\n\",\"exit_code\":0}";
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        // Reconciliation fired once (cap 1); the model declined to compute a
        // tiebreaker, so the split resolves to the first computation.
        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("401", cont.Next.AssistantDraft);
    }

    // Intervention 2(f): majority rule after the reconciliation cap is used.
    // call1=111, call2=13 → finalize triggers the single reconciliation round →
    // reconciliation call3=111 → finalize with the cap now spent. Collected
    // successful results are 111, 13, 111 → draft "111" (majority beats newest).
    [Fact]
    public async Task Compute_majority_value_wins_over_newest()
    {
        var callIndex = 0;
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"print(111)\"}"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(13)\"}"),
            LlmReply.Final("first finalize"),
            LlmReply.Tool("python_eval", "{\"code\":\"print(111)\"}"),
            LlmReply.Final("second finalize"));
        var mcp = new StubMcp(_ =>
        {
            callIndex++;
            return callIndex switch
            {
                1 => "{\"stdout\":\"111\\n\",\"exit_code\":0}",
                2 => "{\"stdout\":\"13\\n\",\"exit_code\":0}",
                _ => "{\"stdout\":\"111\\n\",\"exit_code\":0}",
            };
        });
        var step = BuildStep(llm, mcp: mcp);
        var ctx = ComputeContext("Use the python_eval tool to compute this. Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        // Values collected oldest→newest: 111, 13, 111. Majority (2) = 111.
        Assert.Equal("111", cont.Next.AssistantDraft);
        Assert.Equal(3, cont.Next.ToolCallsMade.Count);
    }

    // Both interventions in one turn: python first fails (repair nudge, class B),
    // the retry succeeds with 37, a redundant "verify" call succeeds with 1
    // (disagreement), the model finalizes → reconciliation nudge (class C) forces
    // one clean round returning 37 → draft 37. Confirms the two budgets are
    // independent and the forced rounds stay within the round-trip cap.
    [Fact]
    public async Task Both_interventions_can_fire_in_one_turn()
    {
        var callIndex = 0;
        var llm = new FakeLlm(
            LlmReply.Tool("python_eval", "{\"code\":\"def f(): return\"}"),        // fails
            LlmReply.Tool("python_eval", "{\"code\":\"print(sum(int(d) for d in str(2**30)))\"}"), // 37
            LlmReply.Tool("python_eval", "{\"code\":\"print(str(2**30)[0::-1])\"}"), // 1 (corrupted verify)
            LlmReply.Final("1"),
            LlmReply.Tool("python_eval", "{\"code\":\"n=2**30\\nprint(sum(int(d) for d in str(n)))\"}"), // reconcile 37
            LlmReply.Final("37"));
        var mcp = new StubMcp(_ =>
        {
            callIndex++;
            return callIndex switch
            {
                1 => "{\"stdout\":\"\",\"stderr\":\"SyntaxError\",\"exit_code\":1}",
                2 => "{\"stdout\":\"37\\n\",\"exit_code\":0}",
                3 => "{\"stdout\":\"1\\n\",\"exit_code\":0}",
                _ => "{\"stdout\":\"37\\n\",\"exit_code\":0}",
            };
        });
        var step = BuildStep(llm, mcp: mcp, maxRoundTrips: 8);
        var ctx = ComputeContext("Use the python_eval tool. What is the digit sum of 2**30? Reply with only the integer.");

        var result = await step.ExecuteAsync(ctx, CancellationToken.None);

        var cont = Assert.IsType<StepResult.Continue>(result);
        Assert.Equal("37", cont.Next.AssistantDraft);
        var allMessages = llm.ReceivedMessages.SelectMany(round => round).ToList();
        // Intervention 1 (repair) and Intervention 2 (reconciliation) both landed.
        Assert.Contains(allMessages, m => m.Role == "system" &&
            (m.Content?.Contains("MULTI-LINE", StringComparison.OrdinalIgnoreCase) ?? false));
        Assert.Contains(allMessages, m => m.Role == "system" &&
            (m.Content?.Contains("returned different values", StringComparison.OrdinalIgnoreCase) ?? false));
        // 4 tool calls total: fail, 37, 1, reconcile-37.
        Assert.Equal(4, cont.Next.ToolCallsMade.Count);
    }

    [Fact]
    public async Task Uses_configured_primary_output_token_budget()
    {
        var llm = new FakeLlm(LlmReply.Final("done"));
        var step = new ToolLoopStep(
            llm,
            new StubMcp(_ => string.Empty),
            maxOutputTokens: 2048);

        await step.ExecuteAsync(NewContext() with { ToolDefs = [] }, CancellationToken.None);

        Assert.Equal([2048], llm.MaxTokenOverrides);
    }

    [Theory]
    [InlineData("Create a new Wiki Canvas root named Field Notes.")]
    [InlineData("Please add a Wiki Canvas root called Travel Log.")]
    [InlineData("Would you please make a Wiki Canvas root called Reading Notes?")]
    [InlineData("Start a new Wiki Canvas root named Planning.")]
    [InlineData("Set up a new Wiki Canvas root named Project Notes.")]
    [InlineData("Establish a Wiki Canvas root called Field Notes.")]
    [InlineData("Initialize a new Wiki root named Lab Notes.")]
    [InlineData("Spin up a Wiki Canvas root called Design Notes.")]
    [InlineData("I need a new Wiki Canvas root named Client Notes. Please create it.")]
    [InlineData("Prepare a Wiki Canvas root called Meeting Notes.")]
    [InlineData("Provision a new Wiki root named Operations Notes.")]
    [InlineData("Build a Wiki Canvas root called Knowledge Notes.")]
    public async Task Explicit_wiki_root_creation_forces_root_create(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var step = BuildStep(llm);
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [ToolDefinitionFor("wiki_root_create")],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("wiki_root_create", llm.ForcedToolNames[0]);
    }

    [Fact]
    public async Task Explicit_wiki_root_creation_hides_application_owned_default_path()
    {
        var prompt = "Create a new Wiki Canvas root named Field Notes.";
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [WikiRootCreateDefinition()],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var definition = Assert.Single(Assert.Single(llm.ReceivedTools)!);
        var schema = JsonSerializer.Serialize(definition.Function.Parameters);
        Assert.Contains("\"name\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"path\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "optional path",
            definition.Function.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_default_location_contract", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Create a Wiki Canvas root named Field Notes under folder Research.")]
    [InlineData("Create a Wiki Canvas root named Field Notes at C:\\Wiki\\Research.")]
    [InlineData("Create a Wiki Canvas root named Field Notes in location /srv/wiki/research.")]
    public async Task Explicit_custom_location_retains_full_root_contract(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [WikiRootCreateDefinition()],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("wiki_root_create", llm.ForcedToolNames[0]);
        var definition = Assert.Single(Assert.Single(llm.ReceivedTools)!);
        var schema = JsonSerializer.Serialize(definition.Function.Parameters);
        Assert.Contains("\"path\"", schema, StringComparison.Ordinal);
        Assert.Contains(
            "optional path",
            definition.Function.Description,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_default_location_contract", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=inactive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("How do I create a Wiki Canvas root?")]
    [InlineData("Explain how to set up a Wiki Canvas root.")]
    [InlineData("Maybe create a Wiki Canvas root later.")]
    [InlineData("Do not create a Wiki Canvas root named Scratch.")]
    [InlineData("Create a page named Findings under the Wiki Canvas root Research.")]
    [InlineData("I need a new Wiki page in the Research root.")]
    [InlineData("Rename the Draft page in Wiki Canvas root Research.")]
    public async Task Non_root_creation_request_does_not_force_root_create(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var step = BuildStep(llm);
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [ToolDefinitionFor("wiki_root_create")],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(llm.ForcedToolNames[0]);
    }

    [Theory]
    [InlineData("Maybe create a Wiki Canvas root named Scratch later.")]
    [InlineData("Hypothetically, create a wiki root called Scratch.")]
    [InlineData("What would happen if you created a Wiki Canvas root named Scratch?")]
    [InlineData("Do not create a Wiki Canvas root named Scratch.")]
    [InlineData("Explain how to create a Wiki Canvas root named Scratch.")]
    public async Task Explicit_non_action_request_withholds_root_mutation_tool(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                ToolDefinitionFor("wiki_root_create"),
                ToolDefinitionFor("wiki_roots_list"),
            ],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var advertised = Assert.Single(llm.ReceivedTools)!;
        Assert.DoesNotContain(advertised, tool => tool.Function.Name == "wiki_root_create");
        Assert.Contains(advertised, tool => tool.Function.Name == "wiki_roots_list");
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_non_action_pruning", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_default_location_contract", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=inactive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Create a Wiki Canvas root named Elm Ledger tomorrow, not now.")]
    [InlineData("Please set up a wiki root called Fjord Papers next week.")]
    [InlineData("When I approve it, create a Wiki Canvas root named Grove Register.")]
    [InlineData("Only after I confirm, initialize a wiki root called Harbor Journal.")]
    [InlineData("After we finish the review, establish a Wiki Canvas root named Indigo Archive.")]
    [InlineData("Once the budget is signed off, build a wiki root called Juniper Notebook.")]
    public async Task Temporal_deferral_withholds_root_mutation_tool(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                ToolDefinitionFor("wiki_root_create"),
                ToolDefinitionFor("wiki_roots_list"),
            ],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Null(llm.ForcedToolNames[0]);
        var advertised = Assert.Single(llm.ReceivedTools)!;
        Assert.DoesNotContain(advertised, tool => tool.Function.Name == "wiki_root_create");
        Assert.Contains(advertised, tool => tool.Function.Name == "wiki_roots_list");
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_temporal_deferral_pruning", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=activated", StringComparison.Ordinal));
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_default_location_contract", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=inactive", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Create a Wiki Canvas root named Tomorrow Notes now.")]
    [InlineData("Create a wiki root called Meeting Atlas now for tomorrow's planning session.")]
    [InlineData("Create a Wiki Canvas root named Planning Brief for tomorrow's meeting.")]
    [InlineData("Initialize a Wiki Canvas root named After Review Archive right now.")]
    [InlineData("Build a wiki root called Launch Notes immediately; we will use it after sign-off.")]
    [InlineData("Set up a Wiki Canvas root named Next Week Ledger now.")]
    [InlineData("Create a wiki root called Eventually Better now.")]
    [InlineData("Create a Wiki Canvas root named Daily Brief today; we can populate it tomorrow.")]
    public async Task Immediate_temporal_context_retains_root_mutation_tool(string prompt)
    {
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [ToolDefinitionFor("wiki_root_create")],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("wiki_root_create", llm.ForcedToolNames[0]);
        var advertised = Assert.Single(llm.ReceivedTools)!;
        Assert.Contains(advertised, tool => tool.Function.Name == "wiki_root_create");
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_temporal_deferral_pruning", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=inactive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explicit_root_creation_retains_root_mutation_tool()
    {
        var prompt = "Create a Wiki Canvas root named Field Notes.";
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var logs = new List<(string Event, string Message)>();
        using var trace = new EnvironmentScope("ST_ROUTING_LATENCY_TRACE", "1");
        var step = BuildStep(llm, log: (eventName, message) => logs.Add((eventName, message)));
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs = [ToolDefinitionFor("wiki_root_create")],
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        var advertised = Assert.Single(llm.ReceivedTools)!;
        Assert.Contains(advertised, tool => tool.Function.Name == "wiki_root_create");
        Assert.Contains(logs, entry =>
            entry.Event == "EXPERIMENT_ACTIVATION" &&
            entry.Message.Contains("event=wiki_root_non_action_pruning", StringComparison.Ordinal) &&
            entry.Message.Contains("decision=inactive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upstream_forced_tool_overrides_wiki_root_selection()
    {
        var prompt = "Create a Wiki Canvas root named Field Notes.";
        var llm = new FakeLlm(LlmReply.Final("Ready."));
        var step = BuildStep(llm);
        var ctx = NewContext() with
        {
            UserText = prompt,
            LlmMessages = [ChatMessage.System("sys"), ChatMessage.User(prompt)],
            ToolDefs =
            [
                ToolDefinitionFor("wiki_root_create"),
                ToolDefinitionFor("wiki_roots_list"),
            ],
            ForcedTool = "wiki_roots_list",
        };

        await step.ExecuteAsync(ctx, CancellationToken.None);

        Assert.Equal("wiki_roots_list", llm.ForcedToolNames[0]);
    }

    private static TurnContext ComputeContext(string prompt) => NewContext() with
    {
        UserText = prompt,
        LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User(prompt) },
        ToolDefs = new[]
        {
            new ToolDefinition { Function = new FunctionDefinition { Name = "python_eval", Description = "sandbox", Parameters = new { } } },
        },
    };

    // ── helpers ──────────────────────────────────────────────────────

    private static ToolLoopStep BuildStep(
        FakeLlm llm,
        StubMcp? mcp = null,
        IChatEventSink? sink = null,
        IToolPermissionGate? gate = null,
        IEnumerable<IToolCallInterceptor>? interceptors = null,
        IEnumerable<IToolArgsRewriter>? argsRewriters = null,
        int maxRoundTrips = 6,
        Action<string, string>? log = null)
        => new(
            llm,
            mcp ?? new StubMcp(_ => ""),
            sink ?? NullChatEventSink.Instance,
            gate,
            groupClassifier: null,
            interceptors,
            argsRewriters,
            maxRoundTrips,
            log: log);

    private static TurnContext NewContext() => new()
    {
        ThreadId = "t1",
        MessageId = "m1",
        UserText = "hi",
        LlmMessages = new[] { ChatMessage.System("sys"), ChatMessage.User("hi") },
        ToolDefs = new[]
        {
            new ToolDefinition { Function = new FunctionDefinition { Name = "web_search", Description = "web", Parameters = new { } } },
            new ToolDefinition { Function = new FunctionDefinition { Name = "flaky", Description = "test", Parameters = new { } } },
            new ToolDefinition { Function = new FunctionDefinition { Name = "propose_automation", Description = "virtual", Parameters = new { } } },
        },
    };

    private static ToolDefinition ToolDefinitionFor(string name) => new()
    {
        Function = new FunctionDefinition
        {
            Name = name,
            Description = "test tool",
            Parameters = new { },
        },
    };

    private static ToolDefinition WikiRootCreateDefinition() => new()
    {
        Function = new FunctionDefinition
        {
            Name = "wiki_root_create",
            Description = "Create a Wiki root. Optional path must stay inside the library.",
            Parameters = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object>
                {
                    ["name"] = new Dictionary<string, object> { ["type"] = "string" },
                    ["path"] = new Dictionary<string, object> { ["type"] = "string" },
                },
                ["required"] = new[] { "name" },
                ["additionalProperties"] = false,
            },
        },
    };

    private sealed class FakeLlm : ILlmClient
    {
        private readonly Queue<LlmReply> _replies;
        public List<int> MaxTokenOverrides { get; } = new();
        public List<string?> ForcedToolNames { get; } = new();
        public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = new();
        public List<IReadOnlyList<ToolDefinition>?> ReceivedTools { get; } = new();

        public FakeLlm(params LlmReply[] replies) => _replies = new(replies);

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            ReceivedTools.Add(tools);
            return Task.FromResult(Record(messages,
                _replies.Count > 0
                    ? _replies.Dequeue().ToResponse()
                    : new LlmResponse { IsComplete = true, Content = "(ran out)", FinishReason = "stop" }));
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
            => ChatAsync(messages, tools, cancellationToken);

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            string? forcedToolName,
            CancellationToken cancellationToken = default)
        {
            MaxTokenOverrides.Add(maxTokensOverride);
            ForcedToolNames.Add(forcedToolName);
            return ChatAsync(messages, tools, cancellationToken);
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");

        private LlmResponse Record(IReadOnlyList<ChatMessage> messages, LlmResponse response)
        {
            ReceivedMessages.Add(messages.ToArray());
            return response;
        }
    }

    private abstract record LlmReply
    {
        public abstract LlmResponse ToResponse();

        public static LlmReply Final(string text) => new FinalReply(text);
        public static LlmReply Tool(string name, string args)
            => new ToolReply(name, args);
    }

    private sealed record FinalReply(string Text) : LlmReply
    {
        public override LlmResponse ToResponse()
            => new() { IsComplete = true, Content = Text, FinishReason = "stop" };
    }

    private sealed record ToolReply(string Name, string Args) : LlmReply
    {
        public override LlmResponse ToResponse() => new()
        {
            IsComplete = false,
            Content = null,
            FinishReason = "tool_calls",
            ToolCalls = new[]
            {
                new ToolCallRequest
                {
                    Id = "call_" + Guid.NewGuid().ToString("N")[..8],
                    Type = "function",
                    Function = new FunctionCallDetails { Name = Name, Arguments = Args },
                },
            },
        };
    }

    private sealed class StubMcp : IMcpToolClient
    {
        private readonly Func<string, string, string> _impl;
        public StubMcp(Func<string, string> impl) : this((tool, _) => impl(tool)) { }
        public StubMcp(Func<string, string, string> impl) { _impl = impl; }

        public Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpToolInfo>>(Array.Empty<McpToolInfo>());

        public Task<string> CallToolAsync(string toolName, string argumentsJson, CancellationToken cancellationToken = default)
            => Task.FromResult(_impl(toolName, argumentsJson));
    }

    private sealed class DeterministicGate : IToolPermissionGate
    {
        private readonly bool _allow;
        private readonly string _reason;
        public DeterministicGate(bool allow, string reason) { _allow = allow; _reason = reason; }
        public Task<ToolPermissionResult> CheckAsync(string toolName, string argumentsJson, CancellationToken ct)
            => Task.FromResult(_allow ? ToolPermissionResult.Grant() : ToolPermissionResult.Deny(_reason));
    }

    private sealed class NamedInterceptor : IToolCallInterceptor
    {
        private readonly string _name;
        private readonly ToolCallOutcome _outcome;
        public NamedInterceptor(string name, ToolCallOutcome outcome) { _name = name; _outcome = outcome; }

        public Task<ToolCallOutcome?> TryInterceptAsync(
            TurnContext context, string toolName, string argumentsJson, string activityId, CancellationToken ct)
            => Task.FromResult(string.Equals(toolName, _name, StringComparison.OrdinalIgnoreCase) ? _outcome : null);
    }

    private sealed class InlineArgsRewriter : IToolArgsRewriter
    {
        private readonly Func<TurnContext, string, string, string> _impl;
        public InlineArgsRewriter(Func<TurnContext, string, string, string> impl) { _impl = impl; }
        public string Rewrite(TurnContext context, string toolName, string argumentsJson)
            => _impl(context, toolName, argumentsJson);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvironmentScope(string name, string value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }

    private sealed class CapturingSink : IChatEventSink
    {
        public ConcurrentBag<ToolStartedEvent> ToolStarted { get; } = new();
        public ConcurrentBag<ToolCompletedEvent> ToolCompleted { get; } = new();

        public Task TurnStartedAsync(string t, string m, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnDeltaAsync(string t, string m, string x, CancellationToken c = default) => Task.CompletedTask;
        public Task TurnCompleteAsync(string t, string m, string f, bool cx, CancellationToken c = default) => Task.CompletedTask;
        public Task FootmanDecisionAsync(string t, string m, string s, double cf, bool ab, string r, int k, int to, long e, CancellationToken c = default) => Task.CompletedTask;

        public Task ToolStartedAsync(string activityId, string threadId, string messageId, string tool, string group, string argsPreview, CancellationToken c = default)
        {
            ToolStarted.Add(new ToolStartedEvent(activityId, tool, group));
            return Task.CompletedTask;
        }

        public Task ToolCompletedAsync(string activityId, string threadId, string messageId, string tool, bool ok, long durationMs, string? resultSnippet, string? error, CancellationToken c = default)
        {
            ToolCompleted.Add(new ToolCompletedEvent(activityId, tool, ok, error));
            return Task.CompletedTask;
        }
    }

    private sealed record ToolStartedEvent(string ActivityId, string Tool, string Group);
    private sealed record ToolCompletedEvent(string ActivityId, string Tool, bool Ok, string? Error);
}
