using System.Text.RegularExpressions;
using SirThaddeus.ObservationSpec;
using SirThaddeus.PermissionBroker;
using SirThaddeus.ToolRunner;

namespace SirThaddeus.Invocation;

/// <summary>
/// Parses command palette input into executable tool plans.
/// </summary>
public sealed partial class CommandPlanner
{
    // ─────────────────────────────────────────────────────────────────
    // Command Patterns
    // ─────────────────────────────────────────────────────────────────

    [GeneratedRegex(@"^open\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OpenUrlPattern();

    [GeneratedRegex(@"^spec\s+new$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecNewPattern();

    [GeneratedRegex(@"^spec\s+validate$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecValidatePattern();

    [GeneratedRegex(@"^spec\s+explain$", RegexOptions.IgnoreCase)]
    private static partial Regex SpecExplainPattern();

    [GeneratedRegex(@"^capture\s*(?:screen)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CaptureScreenPattern();

    // ─────────────────────────────────────────────────────────────────
    // Dependencies
    // ─────────────────────────────────────────────────────────────────

    private readonly Func<string>? _getSpecEditorContent;

    /// <summary>
    /// Creates a new command planner.
    /// </summary>
    /// <param name="getSpecEditorContent">
    /// Optional function to get the current spec editor content for validation/explain commands.
    /// </param>
    public CommandPlanner(Func<string>? getSpecEditorContent = null)
    {
        _getSpecEditorContent = getSpecEditorContent;
    }

    // ─────────────────────────────────────────────────────────────────
    // Planning
    // ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plans the execution of a command string.
    /// </summary>
    /// <param name="commandText">The command text from the palette.</param>
    /// <returns>A plan result with either a tool plan or an error.</returns>
    public PlanResult Plan(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText))
            return PlanResult.Fail("No command provided");

        var trimmed = commandText.Trim();

        // Try each command pattern
        var openMatch = OpenUrlPattern().Match(trimmed);
        if (openMatch.Success)
            return PlanOpenUrl(openMatch.Groups[1].Value.Trim());

        if (SpecNewPattern().IsMatch(trimmed))
            return PlanSpecNew();

        if (SpecValidatePattern().IsMatch(trimmed))
            return PlanSpecValidate();

        if (SpecExplainPattern().IsMatch(trimmed))
            return PlanSpecExplain();

        if (CaptureScreenPattern().IsMatch(trimmed))
            return PlanCaptureScreen();

        return PlanResult.Fail($"Unknown command: {trimmed}");
    }

    // ─────────────────────────────────────────────────────────────────
    // Individual Command Planners
    // ─────────────────────────────────────────────────────────────────

    private static PlanResult PlanOpenUrl(string url)
    {
        // Normalize URL
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return PlanResult.Fail($"Invalid URL: {url}");

        var toolCall = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "browser_navigate",
            Purpose = $"Navigate to {uri.Host}",
            RequiredCapability = Capability.BrowserControl,
            Arguments = new Dictionary<string, object>
            {
                ["url"] = uri.ToString()
            }
        };

        return PlanResult.Ok(ToolPlan.SingleStep(
            toolCall,
            $"Open {uri.Host} in browser"));
    }

    private static PlanResult PlanCaptureScreen()
    {
        var toolCall = new ToolCall
        {
            Id = ToolCall.GenerateId(),
            Name = "screen_capture",
            Purpose = "Capture the current screen",
            RequiredCapability = Capability.ScreenRead,
            Arguments = new Dictionary<string, object>
            {
                ["target"] = "active_window"
            }
        };

        return PlanResult.Ok(ToolPlan.SingleStep(
            toolCall,
            "Capture active window screenshot"));
    }

    private static PlanResult PlanSpecNew()
    {
        // Generate a template using the real schema
        var template = SpecSerializer.CreateTemplateJson();
        return PlanResult.Direct(template);
    }

    private PlanResult PlanSpecValidate()
    {
        if (_getSpecEditorContent == null)
            return PlanResult.Fail("Spec editor not available. Use this command from the spec authoring UI.");

        var content = _getSpecEditorContent();
        if (string.IsNullOrWhiteSpace(content))
            return PlanResult.Fail("No spec content to validate.");

        // Parse the JSON
        if (!SpecSerializer.TryDeserialize(content, out var spec, out var parseError))
        {
            return PlanResult.Direct($"❌ PARSE ERROR\n{parseError}");
        }

        // Validate the spec
        var validator = new ObservationSpecValidator();
        var result = validator.Validate(spec!);

        if (result.IsValid)
        {
            return PlanResult.Direct("✅ VALID\n\nThe observation spec is valid and ready to use.");
        }
        else
        {
            return PlanResult.Direct($"❌ VALIDATION ERRORS\n\n{result.GetErrorSummary()}");
        }
    }

    private PlanResult PlanSpecExplain()
    {
        if (_getSpecEditorContent == null)
            return PlanResult.Fail("Spec editor not available. Use this command from the spec authoring UI.");

        var content = _getSpecEditorContent();
        if (string.IsNullOrWhiteSpace(content))
            return PlanResult.Fail("No spec content to explain.");

        // Parse the JSON
        if (!SpecSerializer.TryDeserialize(content, out var spec, out var parseError))
        {
            return PlanResult.Direct($"❌ PARSE ERROR\n{parseError}");
        }

        // Explain the spec
        var explainer = new ObservationSpecExplainer();
        var explanation = explainer.Explain(spec!);

        return PlanResult.Direct(explanation);
    }
}
