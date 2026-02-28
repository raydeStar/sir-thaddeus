using System.Text.Json;
using SirThaddeus.LlmClient;

namespace SirThaddeus.Agent.Orchestration;

public sealed record ValidationResult(
    bool IsValid,
    string? RejectReasonCode = null,
    string? RepairPrompt = null);

/// <summary>
/// A single intended tool call made by the LLM before it is executed.
/// </summary>
public sealed record ProposedToolCall(
    string ToolName,
    string ArgumentsJson,
    string ToolCallId);

/// <summary>
/// Gate that runs strictly after the LLM has proposed a plan (tool calls) but before MCP execution.
/// Hard-fails any plan that violates intent boundaries or missing slot arguments.
/// </summary>
public interface IPlanValidator
{
    ValidationResult Validate(
        IntentDecisionV2 decision,
        IReadOnlyList<ProposedToolCall> proposedCalls,
        IReadOnlyList<ToolDefinition> allowedTools);
}
