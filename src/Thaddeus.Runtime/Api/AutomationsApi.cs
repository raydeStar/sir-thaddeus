using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SirThaddeus.Agent;
using SirThaddeus.LlmClient;
using Thaddeus.Runtime.Activity;
using Thaddeus.Runtime.Automations;
using Thaddeus.Runtime.Settings;
using Thaddeus.Runtime.Tools;
using Thaddeus.SharedTypes;
using LlmChatMessage = SirThaddeus.LlmClient.ChatMessage;

namespace Thaddeus.Runtime.Api;

/// <summary>REST endpoints for user-defined automations (Phase 7.2).</summary>
public static class AutomationsApi
{
    public static IEndpointRouteBuilder MapAutomationsApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/automations", async (IAutomationStore store, CancellationToken ct) =>
        {
            var items = await store.ListAsync(ct).ConfigureAwait(false);
            return Results.Json(
                new AutomationListResponse(items.ToArray()),
                AutomationsJsonContext.Default.AutomationListResponse);
        });

        // Catalog the UI uses to populate the "Tools this automation can use"
        // picker. Each entry includes the MCP tool name, its description, and
        // the policy group we classify it into — so the UI can group by
        // Web / Files / System / Memory and show icons + tooltips.
        app.MapGet("/api/automations/tools", async (IMcpToolClient mcp, CancellationToken ct) =>
        {
            var tools = await mcp.ListToolsAsync(ct).ConfigureAwait(false);
            var catalog = tools
                .Select(t => new ToolCatalogEntry(
                    Name: t.Name,
                    Description: t.Description,
                    Group: ToolGroupClassifier.Classify(t.Name).ToString()))
                .OrderBy(t => t.Group, StringComparer.Ordinal)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
            return Results.Json(
                new ToolCatalogResponse(catalog),
                AutomationsJsonContext.Default.ToolCatalogResponse);
        });

        // Draft an automation from a one-sentence goal. The user types
        // something like "Check walmart.com for PS5 availability" and the
        // model returns a structured proposal (name, description, ordered
        // steps). The UI then chains into /suggest-tools to fill in the
        // tool allowlist. All via function-calling so the output shape is
        // deterministic regardless of how chatty the small local model is.
        app.MapPost("/api/automations/draft",
            async (DraftAutomationRequest? req, ISettingsStore settings,
                ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Goal))
                return Results.BadRequest(new { error = "goal_required" });

            var doc = await settings.GetAsync(ct).ConfigureAwait(false);
            var llm = doc.Llm;
            if (string.Equals(llm.Provider, "stub", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(llm.BaseUrl) ||
                string.IsNullOrWhiteSpace(llm.ModelId))
            {
                return Results.Json(
                    new DraftAutomationResponse(
                        Name: null, Description: null, Steps: Array.Empty<string>(),
                        Note: "LLM is not configured."),
                    AutomationsJsonContext.Default.DraftAutomationResponse);
            }

            var options = new LlmClientOptions
            {
                BaseUrl = llm.BaseUrl!,
                Model = llm.ModelId,
                MaxTokens = 1024,
                Temperature = 0.2,
            };
            using var client = new LmStudioClient(options);

            var system =
                "You translate a one-sentence user goal into an automation that " +
                "Sir Thaddeus can run unattended. Output a concise title " +
                "(<=60 chars), a short description, and 2–6 ordered steps.\n\n" +

                "The assistant runs headless on the user's machine. When an " +
                "automation fires, NO HUMAN is watching. The assistant has ONLY " +
                "these capabilities:\n" +
                "  • web_search — search the web and read results\n" +
                "  • browser_navigate — fetch a specific URL and read the page\n" +
                "  • screen_capture — read the user's current screen (rarely useful in automations)\n" +
                "  • file_read / file_list — read local files\n" +
                "  • memory tools — recall / save long-term notes\n" +
                "  • utility tools — weather, time, places, calculator, holidays, " +
                "currency, clipboard\n\n" +

                "Every step must be executable with ONE of those capabilities. " +
                "NEVER produce steps that require the assistant to:\n" +
                "  • open a browser tab / window / switch tabs\n" +
                "  • click buttons, fill forms, or type into pages\n" +
                "  • wait / sleep / pause for a duration\n" +
                "  • ask the user for input or confirmation\n" +
                "  • use a physical camera / microphone / printer\n\n" +

                "Phrase each step as a concrete instruction to the assistant " +
                "(e.g. 'Search Amazon for Nintendo Switch 2 listings and report " +
                "the top result's price and availability.'). Prefer single, " +
                "testable actions per step. No step numbers, no meta-commentary, " +
                "no hedging.\n\n" +

                "Examples of good drafts:\n" +
                "  Goal: \"Check the weather in Olympia WA\"\n" +
                "    name: \"Olympia weather check\"\n" +
                "    steps: [\"Check the current weather in Olympia, WA and " +
                "report temperature, conditions, and precipitation chance.\"]\n\n" +
                "  Goal: \"Check Amazon for Nintendo Switch 2\"\n" +
                "    name: \"Amazon: Switch 2 availability\"\n" +
                "    steps: [\n" +
                "      \"Use web_search to find the current Amazon listing for " +
                "'Nintendo Switch 2' and capture the URL.\",\n" +
                "      \"Fetch that Amazon listing URL with browser_navigate and " +
                "extract the product title, price, and 'in stock' status.\",\n" +
                "      \"Summarize: product name, price, stock status, and link.\"\n" +
                "    ]\n\n" +
                "  Goal: \"Morning briefing\"\n" +
                "    name: \"Morning briefing\"\n" +
                "    steps: [\n" +
                "      \"Check the current weather in my saved location.\",\n" +
                "      \"Search the web for the top 3 news headlines today.\",\n" +
                "      \"Combine into a short morning briefing (weather first, " +
                "then headlines).\"\n" +
                "    ]\n\n" +

                "You MUST respond by calling the draft_automation function.";

            var defs = new[]
            {
                new ToolDefinition
                {
                    Function = new FunctionDefinition
                    {
                        Name = "draft_automation",
                        Description = "Records the drafted automation.",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                name = new { type = "string", description = "Short title, <= 60 chars." },
                                description = new { type = "string", description = "One-line description." },
                                steps = new
                                {
                                    type = "array",
                                    description = "Ordered list of single-action instructions.",
                                    items = new { type = "string" }
                                }
                            },
                            required = new[] { "name", "steps" }
                        }
                    }
                }
            };

            try
            {
                var response = await client.ChatAsync(
                    new[] { LlmChatMessage.System(system), LlmChatMessage.User(req.Goal) },
                    defs, ct).ConfigureAwait(false);

                if (response.ToolCalls is not null && response.ToolCalls.Count > 0)
                {
                    var call = response.ToolCalls[0];
                    using var parsed = JsonDocument.Parse(call.Function.Arguments ?? "{}");
                    var root = parsed.RootElement;
                    string? name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        ? n.GetString() : null;
                    string? description = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                    var steps = new List<string>();
                    if (root.TryGetProperty("steps", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String)
                            {
                                var s = el.GetString();
                                if (!string.IsNullOrWhiteSpace(s)) steps.Add(s.Trim());
                            }
                        }
                    }

                    return Results.Json(
                        new DraftAutomationResponse(
                            Name: string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                            Description: string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                            Steps: steps,
                            Note: steps.Count == 0 ? "Model returned no steps." : null),
                        AutomationsJsonContext.Default.DraftAutomationResponse);
                }

                return Results.Json(
                    new DraftAutomationResponse(null, null, Array.Empty<string>(),
                        "Model replied without calling the draft function."),
                    AutomationsJsonContext.Default.DraftAutomationResponse);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AutomationsApi.Draft")
                    .LogWarning(ex, "draft_automation.failed");
                return Results.Json(
                    new DraftAutomationResponse(null, null, Array.Empty<string>(), ex.Message),
                    AutomationsJsonContext.Default.DraftAutomationResponse);
            }
        });

        // "Let AI pick tools" — takes a description + ordered steps, sends
        // the model a short no-history prompt with the tool catalog, and
        // expects a structured pick. The model must call the provided
        // `select_tools` function with an array of tool names.
        app.MapPost("/api/automations/suggest-tools",
            async (SuggestToolsRequest? req, ISettingsStore settings, IMcpToolClient mcp,
                ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            if (req is null || req.Steps is null || req.Steps.Count == 0)
                return Results.BadRequest(new { error = "steps_required" });

            var doc = await settings.GetAsync(ct).ConfigureAwait(false);
            var llm = doc.Llm;
            if (string.Equals(llm.Provider, "stub", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(llm.BaseUrl) ||
                string.IsNullOrWhiteSpace(llm.ModelId))
            {
                return Results.Json(
                    new SuggestToolsResponse(Array.Empty<string>(),
                        "LLM is not configured; pick tools manually."),
                    AutomationsJsonContext.Default.SuggestToolsResponse);
            }

            var catalog = await mcp.ListToolsAsync(ct).ConfigureAwait(false);
            if (catalog.Count == 0)
            {
                return Results.Json(
                    new SuggestToolsResponse(Array.Empty<string>(),
                        "No tools are available right now."),
                    AutomationsJsonContext.Default.SuggestToolsResponse);
            }

            var options = new LlmClientOptions
            {
                BaseUrl = llm.BaseUrl!,
                Model = llm.ModelId,
                MaxTokens = 512,
                Temperature = 0.1,
            };
            using var client = new LmStudioClient(options);

            // Plan-style "dry run": we ask the model to mentally walk each
            // step and list every tool call it *would* make, then derive the
            // allowlist from that trace. Empirically more accurate than
            // "pick from this list" — the model grounds its answer in the
            // real work, not vibes about the step text.
            var toolSummary = string.Join("\n", catalog.Select(t =>
                $"- {t.Name} ({ToolGroupClassifier.Classify(t.Name)}): {Truncate(t.Description, 140)}"));
            var system =
                "You are planning which local tools an automation will need. " +
                "Walk through each step as if you were about to run it. For every " +
                "step, list the exact tool names you would call (in order) and " +
                "nothing else — no commentary, no invented tools, no generic " +
                "categories. If a step needs no tool (pure reasoning or text), " +
                "include no tools for it. Favor breadth of realistic dependencies: " +
                "if a step says 'check the weather in X', both weather_geocode " +
                "(to resolve X) and weather_forecast are likely needed. If it " +
                "says 'visit walmart.com', browser_navigate is required, and " +
                "web_search is a likely fallback. Respond by calling " +
                "select_tools with the de-duplicated list of tool names actually " +
                "referenced in your plan.";
            var user = "Available tools (name + group + description):\n" + toolSummary +
                "\n\nAutomation name: " + (req.Name ?? "(unnamed)") +
                "\nDescription: " + (req.Description ?? "(none)") +
                "\nSteps:\n" + string.Join("\n", req.Steps.Select((s, i) => $"{i + 1}. {s}"));

            var defs = new[]
            {
                new ToolDefinition
                {
                    Function = new FunctionDefinition
                    {
                        Name = "select_tools",
                        Description = "Records which tools the automation needs pre-approved.",
                        Parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                tools = new
                                {
                                    type = "array",
                                    description = "Tool names the automation should be allowed to call without a prompt.",
                                    items = new { type = "string" }
                                }
                            },
                            required = new[] { "tools" }
                        }
                    }
                }
            };

            try
            {
                var response = await client.ChatAsync(
                    new[] { LlmChatMessage.System(system), LlmChatMessage.User(user) },
                    defs, ct).ConfigureAwait(false);

                if (response.ToolCalls is not null && response.ToolCalls.Count > 0)
                {
                    var call = response.ToolCalls[0];
                    using var parsed = JsonDocument.Parse(call.Function.Arguments ?? "{}");
                    var names = new List<string>();
                    if (parsed.RootElement.TryGetProperty("tools", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.ValueKind == JsonValueKind.String)
                            {
                                var n = el.GetString();
                                if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                            }
                        }
                    }
                    // Only keep names the MCP server actually exposes.
                    var valid = new HashSet<string>(catalog.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                    var final = names.Where(n => valid.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    return Results.Json(
                        new SuggestToolsResponse(final, null),
                        AutomationsJsonContext.Default.SuggestToolsResponse);
                }

                return Results.Json(
                    new SuggestToolsResponse(Array.Empty<string>(),
                        "Model replied without picking tools — choose them manually."),
                    AutomationsJsonContext.Default.SuggestToolsResponse);
            }
            catch (Exception ex)
            {
                loggerFactory.CreateLogger("AutomationsApi.Suggest")
                    .LogWarning(ex, "suggest_tools.failed");
                return Results.Json(
                    new SuggestToolsResponse(Array.Empty<string>(), ex.Message),
                    AutomationsJsonContext.Default.SuggestToolsResponse);
            }
        });

        app.MapPost("/api/automations", async (HttpContext ctx, IAutomationStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<CreateAutomationRequest>(ctx, AutomationsJsonContext.Default.CreateAutomationRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var item = await store.CreateAsync(
                req.Name ?? string.Empty,
                req.Description ?? string.Empty,
                req.Steps ?? Array.Empty<string>(),
                req.Enabled ?? true,
                req.AllowedTools,
                req.Schedule,
                ct).ConfigureAwait(false);
            return Results.Json(item, AutomationsJsonContext.Default.Automation, statusCode: StatusCodes.Status201Created);
        });

        app.MapGet("/api/automations/{id}", async (string id, IAutomationStore store, CancellationToken ct) =>
        {
            var item = await store.GetAsync(id, ct).ConfigureAwait(false);
            return item is null ? Results.NotFound() : Results.Json(item, AutomationsJsonContext.Default.Automation);
        });

        app.MapPatch("/api/automations/{id}", async (string id, HttpContext ctx, IAutomationStore store, CancellationToken ct) =>
        {
            var req = await ReadAsync<UpdateAutomationRequest>(ctx, AutomationsJsonContext.Default.UpdateAutomationRequest, ct);
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var updated = await store.UpdateAsync(id, req.Name, req.Description, req.Steps, req.Enabled, req.AllowedTools, req.Schedule, ct)
                .ConfigureAwait(false);
            return updated is null
                ? Results.NotFound()
                : Results.Json(updated, AutomationsJsonContext.Default.Automation);
        });

        app.MapDelete("/api/automations/{id}", async (string id, IAutomationStore store, CancellationToken ct) =>
        {
            var ok = await store.DeleteAsync(id, ct).ConfigureAwait(false);
            return ok ? Results.NoContent() : Results.NotFound();
        });

        // Manual trigger. Executes the automation's steps end-to-end:
        // creates a thread, posts each step as a user turn, and lets the
        // assistant respond (with tools + permissions) to each. The response
        // includes the new thread id so the UI can navigate and watch.
        app.MapPost("/api/automations/{id}/run",
            async (string id, IAutomationStore store, AutomationRunner runner, CancellationToken ct) =>
        {
            var item = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (item is null) return Results.NotFound();
            if (!item.Enabled) return Results.BadRequest(new { error = "disabled" });
            if (item.Steps.Count == 0)
                return Results.BadRequest(new { error = "no_steps", message = "This automation has no steps to run." });

            var start = await runner.StartRunAsync(item, ct).ConfigureAwait(false);
            var updated = await store.RecordRunAsync(id, ct).ConfigureAwait(false);

            return Results.Json(
                new AutomationRunResponse(updated!, start.ThreadId, start.ActivityId),
                AutomationsJsonContext.Default.AutomationRunResponse);
        });

        return app;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext ctx, JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(ctx.Request.Body, info, ct).ConfigureAwait(false);
        }
        catch (JsonException) { return null; }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..max] + "…";
    }
}

public sealed record CreateAutomationRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? Steps,
    bool? Enabled,
    IReadOnlyList<string>? AllowedTools,
    AutomationSchedule? Schedule);
public sealed record UpdateAutomationRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string>? Steps,
    bool? Enabled,
    IReadOnlyList<string>? AllowedTools,
    AutomationSchedule? Schedule);
public sealed record AutomationListResponse(IReadOnlyList<Automation> Automations);
public sealed record AutomationRunResponse(Automation Automation, string ThreadId, string ActivityId);

public sealed record ToolCatalogEntry(string Name, string Description, string Group);
public sealed record ToolCatalogResponse(IReadOnlyList<ToolCatalogEntry> Tools);

public sealed record SuggestToolsRequest(
    string? Name,
    string? Description,
    IReadOnlyList<string> Steps);
public sealed record SuggestToolsResponse(
    IReadOnlyList<string> Tools,
    string? Note);

public sealed record DraftAutomationRequest(string? Goal);
public sealed record DraftAutomationResponse(
    string? Name,
    string? Description,
    IReadOnlyList<string> Steps,
    string? Note);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Automation))]
[JsonSerializable(typeof(AutomationListResponse))]
[JsonSerializable(typeof(AutomationRunResponse))]
[JsonSerializable(typeof(CreateAutomationRequest))]
[JsonSerializable(typeof(UpdateAutomationRequest))]
[JsonSerializable(typeof(ToolCatalogEntry))]
[JsonSerializable(typeof(ToolCatalogResponse))]
[JsonSerializable(typeof(SuggestToolsRequest))]
[JsonSerializable(typeof(SuggestToolsResponse))]
[JsonSerializable(typeof(DraftAutomationRequest))]
[JsonSerializable(typeof(DraftAutomationResponse))]
public partial class AutomationsJsonContext : JsonSerializerContext
{
}
