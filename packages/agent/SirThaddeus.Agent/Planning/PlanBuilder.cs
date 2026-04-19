using System.Text.Json;
using SirThaddeus.Agent.Routing;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Planning;

/// <summary>
/// Builds a planning prompt, sends it to the LLM, and parses the
/// returned JSON into a <see cref="TaskPlan"/>. Invalid plans trigger
/// exactly one re-prompt before falling back.
/// </summary>
public sealed class PlanBuilder
{
    private readonly ILlmClient _llm;
    private const int PlanMaxTokens = 300;

    public PlanBuilder(ILlmClient llm)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
    }

    /// <summary>
    /// Asks the LLM to produce a <see cref="TaskPlan"/> for the given input.
    /// On validation failure, re-prompts once with the specific errors.
    /// On second failure, returns <c>null</c> (caller should fall back to a
    /// direct response with no plan).
    /// </summary>
    /// <param name="userInput">The user's original message.</param>
    /// <param name="lane">The classified task lane.</param>
    /// <param name="availableToolNames">
    /// Tool names available for this route (from PolicyGate + ToolCapabilityRegistry).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A valid <see cref="TaskPlan"/>, or <c>null</c> on unrecoverable failure.</returns>
    public async Task<TaskPlan?> BuildPlanAsync(
        string userInput,
        TaskLane lane,
        IReadOnlyCollection<string> availableToolNames,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(lane, availableToolNames);
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(systemPrompt),
            ChatMessage.User(userInput)
        };

        // First attempt
        var (plan, parseError) = await TryGetPlanAsync(messages, lane, cancellationToken);
        if (plan is not null)
        {
            var errors = PlanValidator.Validate(plan, availableToolNames);
            if (errors.Count == 0)
                return plan;

            // Re-prompt with validation errors
            messages.Add(ChatMessage.Assistant(SerializePlan(plan)));
            messages.Add(ChatMessage.User(
                $"Your plan has validation errors. Fix them and return valid JSON only:\n" +
                string.Join("\n", errors.Select(e => $"- {e}"))));
        }
        else if (parseError is not null)
        {
            // Re-prompt with parse error
            messages.Add(ChatMessage.User(
                $"Your response was not valid JSON. Return ONLY a JSON object:\n- {parseError}"));
        }
        else
        {
            return null; // Empty response, give up
        }

        // Second attempt
        var (rePlan, _) = await TryGetPlanAsync(messages, lane, cancellationToken);
        if (rePlan is null)
            return null;

        var reErrors = PlanValidator.Validate(rePlan, availableToolNames);
        return reErrors.Count == 0 ? rePlan : null;
    }

    private async Task<(TaskPlan? Plan, string? Error)> TryGetPlanAsync(
        List<ChatMessage> messages,
        TaskLane lane,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _llm.ChatAsync(
                messages, tools: null, PlanMaxTokens, cancellationToken);

            return ParsePlanResponse(response.Content, lane);
        }
        catch
        {
            return (null, "LLM call failed.");
        }
    }

    /// <summary>
    /// Parses the LLM response into a <see cref="TaskPlan"/>.
    /// </summary>
    internal static (TaskPlan? Plan, string? Error) ParsePlanResponse(string? content, TaskLane lane)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, "Empty response.");

        try
        {
            var json = content.Trim();
            // Strip markdown code fences
            if (json.StartsWith("```", StringComparison.Ordinal))
            {
                var start = json.IndexOf('{');
                var end = json.LastIndexOf('}');
                if (start >= 0 && end > start)
                    json = json[start..(end + 1)];
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var taskKind = root.TryGetProperty("TaskKind", out var tkProp)
                ? tkProp.GetString() ?? ""
                : root.TryGetProperty("taskKind", out tkProp)
                    ? tkProp.GetString() ?? ""
                    : "";

            var requiredTools = ParseStringArray(root, "RequiredTools")
                ?? ParseStringArray(root, "requiredTools")
                ?? [];

            var steps = ParseStringArray(root, "Steps")
                ?? ParseStringArray(root, "steps")
                ?? [];

            var stopCondition = root.TryGetProperty("StopCondition", out var scProp)
                ? scProp.GetString() ?? ""
                : root.TryGetProperty("stopCondition", out scProp)
                    ? scProp.GetString() ?? ""
                    : "";

            var successCriteria = root.TryGetProperty("SuccessCriteria", out var sucProp)
                ? sucProp.GetString() ?? ""
                : root.TryGetProperty("successCriteria", out sucProp)
                    ? sucProp.GetString() ?? ""
                    : "";

            // Parse lane from response or use the pre-classified lane
            var laneParsed = lane;
            if (root.TryGetProperty("Lane", out var laneProp) || root.TryGetProperty("lane", out laneProp))
            {
                var laneStr = laneProp.GetString() ?? "";
                if (Enum.TryParse<TaskLane>(laneStr, ignoreCase: true, out var parsed))
                    laneParsed = parsed;
            }

            var plan = new TaskPlan
            {
                TaskKind = taskKind,
                Lane = laneParsed,
                RequiredTools = requiredTools,
                Steps = steps,
                StopCondition = stopCondition,
                SuccessCriteria = successCriteria
            };

            return (plan, null);
        }
        catch (JsonException ex)
        {
            return (null, $"Invalid JSON: {ex.Message}");
        }
    }

    private static IReadOnlyList<string>? ParseStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.Array)
            return null;

        var list = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            var val = item.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                list.Add(val);
        }
        return list;
    }

    private static string SerializePlan(TaskPlan plan)
    {
        return JsonSerializer.Serialize(new
        {
            plan.TaskKind,
            Lane = plan.Lane.ToString(),
            plan.RequiredTools,
            plan.Steps,
            plan.StopCondition,
            plan.SuccessCriteria
        });
    }

    private static string BuildSystemPrompt(TaskLane lane, IReadOnlyCollection<string> availableToolNames)
    {
        var toolList = availableToolNames.Count > 0
            ? string.Join(", ", availableToolNames)
            : "(none)";

        return $$"""
            You are a planning agent. Before any tool execution, produce an execution plan.
            The user's request has been classified into lane: {{lane}}
            Available tools: [{{toolList}}]

            Respond with ONLY a JSON object — no markdown, no explanation:
            {
              "TaskKind": "<high-level task type, e.g. web_lookup, file_organization, explanation>",
              "Lane": "{{lane}}",
              "RequiredTools": ["<tool1>", "<tool2>"],
              "Steps": ["<step 1>", "<step 2>", "<step 3>"],
              "StopCondition": "<when to stop executing>",
              "SuccessCriteria": "<what counts as success>"
            }

            Rules:
            - RequiredTools must only contain tools from the available tools list (or be empty if no tools needed)
            - Steps must have at least 1 step
            - StopCondition and SuccessCriteria must not be empty
            """;
    }
}
