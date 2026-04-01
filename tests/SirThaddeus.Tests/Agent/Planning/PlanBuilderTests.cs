using SirThaddeus.Agent.Planning;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Tests;

public class PlanBuilderTests
{
    private static readonly IReadOnlyCollection<string> SampleTools = new[]
    {
        "web_search", "weather_forecast", "file_read", "memory_retrieve"
    };

    // ── ParsePlanResponse: Valid JSON ────────────────────────────────

    [Fact]
    public void ParsePlanResponse_ValidJson_ReturnsPlan()
    {
        var json = """
            {
              "TaskKind": "web_lookup",
              "Lane": "Lookup",
              "RequiredTools": ["web_search"],
              "Steps": ["Search for the answer", "Return result"],
              "StopCondition": "Answer found",
              "SuccessCriteria": "User gets a factual answer"
            }
            """;

        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.Lookup);

        Assert.NotNull(plan);
        Assert.Null(error);
        Assert.Equal("web_lookup", plan.TaskKind);
        Assert.Equal(TaskLane.Lookup, plan.Lane);
        Assert.Single(plan.RequiredTools);
        Assert.Equal("web_search", plan.RequiredTools[0]);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("Answer found", plan.StopCondition);
        Assert.Equal("User gets a factual answer", plan.SuccessCriteria);
    }

    [Fact]
    public void ParsePlanResponse_CamelCaseJson_ReturnsPlan()
    {
        var json = """
            {
              "taskKind": "explanation",
              "lane": "Explain",
              "requiredTools": [],
              "steps": ["Break down the concept"],
              "stopCondition": "Explanation complete",
              "successCriteria": "User understands the concept"
            }
            """;

        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.Explain);

        Assert.NotNull(plan);
        Assert.Null(error);
        Assert.Equal("explanation", plan.TaskKind);
        Assert.Equal(TaskLane.Explain, plan.Lane);
        Assert.Empty(plan.RequiredTools);
    }

    [Fact]
    public void ParsePlanResponse_MarkdownFenced_ReturnsPlan()
    {
        var json = """
            ```json
            {
              "TaskKind": "file_organization",
              "Lane": "FileSystem",
              "RequiredTools": ["file_read"],
              "Steps": ["List files", "Organize them"],
              "StopCondition": "All files categorized",
              "SuccessCriteria": "Files are organized"
            }
            ```
            """;

        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.FileSystem);

        Assert.NotNull(plan);
        Assert.Null(error);
        Assert.Equal("file_organization", plan.TaskKind);
    }

    // ── ParsePlanResponse: Invalid inputs ────────────────────────────

    [Fact]
    public void ParsePlanResponse_NullContent_ReturnsError()
    {
        var (plan, error) = PlanBuilder.ParsePlanResponse(null, TaskLane.Conversation);

        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("Empty", error);
    }

    [Fact]
    public void ParsePlanResponse_EmptyContent_ReturnsError()
    {
        var (plan, error) = PlanBuilder.ParsePlanResponse("", TaskLane.Conversation);

        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("Empty", error);
    }

    [Fact]
    public void ParsePlanResponse_InvalidJson_ReturnsError()
    {
        var (plan, error) = PlanBuilder.ParsePlanResponse("not json at all", TaskLane.Lookup);

        Assert.Null(plan);
        Assert.NotNull(error);
        Assert.Contains("Invalid JSON", error);
    }

    [Fact]
    public void ParsePlanResponse_MissingFields_ReturnsPlanWithDefaults()
    {
        var json = """
            {
              "Steps": ["Do something"]
            }
            """;

        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.Conversation);

        Assert.NotNull(plan);
        Assert.Null(error);
        Assert.Equal("", plan.TaskKind);
        Assert.Equal(TaskLane.Conversation, plan.Lane); // Falls back to classified lane
        Assert.Single(plan.Steps);
        Assert.Equal("", plan.StopCondition);
        Assert.Equal("", plan.SuccessCriteria);
    }

    // ── ParsePlanResponse: Lane override from LLM ────────────────────

    [Fact]
    public void ParsePlanResponse_LaneParsedFromLlmResponse()
    {
        var json = """
            {
              "TaskKind": "weather",
              "Lane": "Lookup",
              "RequiredTools": ["weather_forecast"],
              "Steps": ["Fetch weather"],
              "StopCondition": "Forecast retrieved",
              "SuccessCriteria": "User gets weather info"
            }
            """;

        // Pre-classified lane was Conversation, but LLM says Lookup
        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.Conversation);

        Assert.NotNull(plan);
        Assert.Equal(TaskLane.Lookup, plan.Lane);
    }

    [Fact]
    public void ParsePlanResponse_InvalidLaneString_KeepsPreClassified()
    {
        var json = """
            {
              "TaskKind": "chat",
              "Lane": "NonExistentLane",
              "RequiredTools": [],
              "Steps": ["Respond"],
              "StopCondition": "Done",
              "SuccessCriteria": "User satisfied"
            }
            """;

        var (plan, error) = PlanBuilder.ParsePlanResponse(json, TaskLane.Conversation);

        Assert.NotNull(plan);
        Assert.Equal(TaskLane.Conversation, plan.Lane);
    }

    // ── PlanValidator ────────────────────────────────────────────────

    [Fact]
    public void Validate_ValidPlan_ReturnsNoErrors()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search" },
            Steps = new[] { "Search for the answer" },
            StopCondition = "Answer found",
            SuccessCriteria = "Correct factual answer returned"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_EmptySteps_ReturnsError()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search" },
            Steps = Array.Empty<string>(),
            StopCondition = "Answer found",
            SuccessCriteria = "Correct"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Contains(errors, e => e.Contains("Steps"));
    }

    [Fact]
    public void Validate_EmptyStopCondition_ReturnsError()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search" },
            Steps = new[] { "Search" },
            StopCondition = "",
            SuccessCriteria = "Correct"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Contains(errors, e => e.Contains("StopCondition"));
    }

    [Fact]
    public void Validate_EmptySuccessCriteria_ReturnsError()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search" },
            Steps = new[] { "Search" },
            StopCondition = "Done",
            SuccessCriteria = ""
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Contains(errors, e => e.Contains("SuccessCriteria"));
    }

    [Fact]
    public void Validate_EmptyTaskKind_ReturnsError()
    {
        var plan = new TaskPlan
        {
            TaskKind = "",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search" },
            Steps = new[] { "Search" },
            StopCondition = "Done",
            SuccessCriteria = "Correct"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Contains(errors, e => e.Contains("TaskKind"));
    }

    [Fact]
    public void Validate_UnavailableTool_ReturnsError()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "web_search", "nonexistent_tool" },
            Steps = new[] { "Search" },
            StopCondition = "Done",
            SuccessCriteria = "Correct"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Contains(errors, e => e.Contains("nonexistent_tool"));
    }

    [Fact]
    public void Validate_EmptyRequiredTools_IsValid()
    {
        var plan = new TaskPlan
        {
            TaskKind = "explanation",
            Lane = TaskLane.Explain,
            RequiredTools = Array.Empty<string>(),
            Steps = new[] { "Explain the concept" },
            StopCondition = "Explanation complete",
            SuccessCriteria = "User understands"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_ToolNameMatching_IsCaseInsensitive()
    {
        var plan = new TaskPlan
        {
            TaskKind = "web_lookup",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "WEB_SEARCH" },
            Steps = new[] { "Search" },
            StopCondition = "Done",
            SuccessCriteria = "Correct"
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_MultipleErrors_ReturnsAll()
    {
        var plan = new TaskPlan
        {
            TaskKind = "",
            Lane = TaskLane.Lookup,
            RequiredTools = new[] { "bad_tool" },
            Steps = Array.Empty<string>(),
            StopCondition = "",
            SuccessCriteria = ""
        };

        var errors = PlanValidator.Validate(plan, SampleTools);

        Assert.True(errors.Count >= 4, $"Expected at least 4 errors, got {errors.Count}");
    }

    // ── BuildPlanAsync with FakeLlmClient ────────────────────────────

    [Fact]
    public async Task BuildPlanAsync_ValidLlmResponse_ReturnsPlan()
    {
        var validPlanJson = """
            {
              "TaskKind": "web_lookup",
              "Lane": "Lookup",
              "RequiredTools": ["web_search"],
              "Steps": ["Search for the answer", "Format the response"],
              "StopCondition": "Answer found",
              "SuccessCriteria": "User gets a factual answer"
            }
            """;
        var llm = new FakeLlmClient(validPlanJson);
        var builder = new PlanBuilder(llm);

        var plan = await builder.BuildPlanAsync(
            "What's the weather in Seattle?",
            TaskLane.Lookup,
            SampleTools);

        Assert.NotNull(plan);
        Assert.Equal("web_lookup", plan.TaskKind);
        Assert.Equal(TaskLane.Lookup, plan.Lane);
        Assert.Equal(2, plan.Steps.Count);
    }

    [Fact]
    public async Task BuildPlanAsync_InvalidJsonThenValid_RepromptsOnce()
    {
        var responses = new[]
        {
            "This is not JSON at all",
            """
            {
              "TaskKind": "explanation",
              "Lane": "Explain",
              "RequiredTools": [],
              "Steps": ["Explain the concept"],
              "StopCondition": "Explanation complete",
              "SuccessCriteria": "User understands"
            }
            """
        };
        var llm = new SequentialFakeLlmClient(responses);
        var builder = new PlanBuilder(llm);

        var plan = await builder.BuildPlanAsync(
            "Explain photosynthesis",
            TaskLane.Explain,
            SampleTools);

        Assert.NotNull(plan);
        Assert.Equal("explanation", plan.TaskKind);
    }

    [Fact]
    public async Task BuildPlanAsync_TwoInvalidResponses_ReturnsNull()
    {
        var responses = new[]
        {
            "Not JSON",
            "Still not JSON"
        };
        var llm = new SequentialFakeLlmClient(responses);
        var builder = new PlanBuilder(llm);

        var plan = await builder.BuildPlanAsync(
            "Tell me a joke",
            TaskLane.Conversation,
            SampleTools);

        Assert.Null(plan);
    }

    [Fact]
    public async Task BuildPlanAsync_ValidationFailureThenFixed_Succeeds()
    {
        var responses = new[]
        {
            // First response: invalid (empty Steps)
            """
            {
              "TaskKind": "web_lookup",
              "Lane": "Lookup",
              "RequiredTools": ["web_search"],
              "Steps": [],
              "StopCondition": "Done",
              "SuccessCriteria": "Found"
            }
            """,
            // Second response: valid
            """
            {
              "TaskKind": "web_lookup",
              "Lane": "Lookup",
              "RequiredTools": ["web_search"],
              "Steps": ["Search for the answer"],
              "StopCondition": "Done",
              "SuccessCriteria": "Found"
            }
            """
        };
        var llm = new SequentialFakeLlmClient(responses);
        var builder = new PlanBuilder(llm);

        var plan = await builder.BuildPlanAsync(
            "What's the population of Mars?",
            TaskLane.Lookup,
            SampleTools);

        Assert.NotNull(plan);
        Assert.Single(plan.Steps);
    }

    [Fact]
    public async Task BuildPlanAsync_EmptyResponse_ReturnsNull()
    {
        var llm = new FakeLlmClient("");
        var builder = new PlanBuilder(llm);

        var plan = await builder.BuildPlanAsync(
            "Hello",
            TaskLane.Conversation,
            SampleTools);

        Assert.Null(plan);
    }

    // ── Test Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// A fake LLM client that always returns the same response.
    /// </summary>
    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly string _response;

        public FakeLlmClient(string response)
        {
            _response = response;
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = _response,
                FinishReason = "stop"
            });
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            return ChatAsync(messages, tools, cancellationToken);
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");
    }

    /// <summary>
    /// A fake LLM client that returns responses in sequence.
    /// </summary>
    private sealed class SequentialFakeLlmClient : ILlmClient
    {
        private readonly string[] _responses;
        private int _index;

        public SequentialFakeLlmClient(string[] responses)
        {
            _responses = responses;
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            CancellationToken cancellationToken = default)
        {
            var response = _index < _responses.Length
                ? _responses[_index]
                : "";
            _index++;
            return Task.FromResult(new LlmResponse
            {
                IsComplete = true,
                Content = response,
                FinishReason = "stop"
            });
        }

        public Task<LlmResponse> ChatAsync(
            IReadOnlyList<ChatMessage> messages,
            IReadOnlyList<ToolDefinition>? tools,
            int maxTokensOverride,
            CancellationToken cancellationToken = default)
        {
            return ChatAsync(messages, tools, cancellationToken);
        }

        public Task<string?> GetModelNameAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<string?>("fake-model");
    }
}
