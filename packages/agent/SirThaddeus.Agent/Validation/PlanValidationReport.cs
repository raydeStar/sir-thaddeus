using SirThaddeus.Agent.Orchestration;

namespace SirThaddeus.Agent.Validation;

/// <summary>
/// Rich validation report for a proposed set of tool calls.
/// Extends the existing <see cref="ValidationResult"/> with per-call
/// details, schema validation results, and sanitized calls that
/// passed all checks.
/// </summary>
public sealed record PlanValidationReport
{
    /// <summary>Whether the entire plan is valid and safe to execute.</summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// High-level rejection reason (matches existing <see cref="ValidationResult.RejectReasonCode"/>).
    /// Null when valid.
    /// </summary>
    public string? RejectReasonCode { get; init; }

    /// <summary>
    /// Human-readable repair prompt for the LLM (matches existing <see cref="ValidationResult.RepairPrompt"/>).
    /// Null when valid.
    /// </summary>
    public string? RepairPrompt { get; init; }

    /// <summary>
    /// Per-call validation details. One entry per proposed call, in order.
    /// </summary>
    public IReadOnlyList<CallValidationDetail> CallDetails { get; init; } = [];

    /// <summary>
    /// Proposed calls that passed all validation checks and are safe to execute.
    /// May be a subset of the original calls if some were rejected.
    /// </summary>
    public IReadOnlyList<ProposedToolCall> SanitizedCalls { get; init; } = [];

    /// <summary>
    /// Converts to the legacy <see cref="ValidationResult"/> for backward compatibility.
    /// </summary>
    public ValidationResult ToLegacy() => new(IsValid, RejectReasonCode, RepairPrompt);

    /// <summary>Factory for a fully valid plan.</summary>
    public static PlanValidationReport Valid(IReadOnlyList<ProposedToolCall> calls) => new()
    {
        IsValid = true,
        SanitizedCalls = calls,
        CallDetails = calls.Select(c => new CallValidationDetail
        {
            ToolName = c.ToolName,
            IsValid = true
        }).ToList()
    };

    /// <summary>Factory for a rejected plan.</summary>
    public static PlanValidationReport Rejected(
        string reasonCode,
        string repairPrompt,
        IReadOnlyList<CallValidationDetail>? details = null) => new()
    {
        IsValid = false,
        RejectReasonCode = reasonCode,
        RepairPrompt = repairPrompt,
        CallDetails = details ?? []
    };
}

/// <summary>
/// Validation detail for a single proposed tool call within a plan.
/// </summary>
public sealed record CallValidationDetail
{
    /// <summary>Name of the tool being called.</summary>
    public required string ToolName { get; init; }

    /// <summary>Whether this specific call passed validation.</summary>
    public bool IsValid { get; init; }

    /// <summary>Issues found during validation (empty when valid).</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>Whether this call was rejected by policy (not in allowlist).</summary>
    public bool PolicyRejected { get; init; }

    /// <summary>Whether this call failed schema validation.</summary>
    public bool SchemaRejected { get; init; }
}
