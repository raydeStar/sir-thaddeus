using System.Collections.Concurrent;
using System.Diagnostics;
using SirThaddeus.AuditLog;
using SirThaddeus.PermissionBroker;

namespace SirThaddeus.ToolRunner;

/// <summary>
/// Tool runner that enforces permission tokens before execution.
/// All tool executions are logged to the audit system.
/// </summary>
public sealed class EnforcingToolRunner : IToolRunner
{
    private readonly IPermissionBroker _permissionBroker;
    private readonly IAuditLogger _auditLogger;
    private readonly ConcurrentDictionary<string, ITool> _tools = new();

    public EnforcingToolRunner(IPermissionBroker permissionBroker, IAuditLogger auditLogger)
    {
        _permissionBroker = permissionBroker ?? throw new ArgumentNullException(nameof(permissionBroker));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> RegisteredTools => _tools.Keys.ToList();

    /// <inheritdoc />
    public bool HasTool(string name) => _tools.ContainsKey(name);

    /// <inheritdoc />
    public void RegisterTool(ITool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        _tools[tool.Name] = tool;
    }

    /// <inheritdoc />
    public async Task<ToolResult> ExecuteAsync(
        ToolCall call,
        string? tokenId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // ─────────────────────────────────────────────────────────────
        // Step 1: Find the tool
        // ─────────────────────────────────────────────────────────────

        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            LogToolExecution(call, success: false, error: "Tool not found", tokenId: null);
            return ToolResult.Fail(call.Id, $"Tool '{call.Name}' not found");
        }

        // ─────────────────────────────────────────────────────────────
        // Step 2: Validate permission token
        // ─────────────────────────────────────────────────────────────

        if (string.IsNullOrEmpty(tokenId))
        {
            LogToolExecution(call, success: false, error: "No permission token provided", tokenId: null);
            return ToolResult.PermissionDenied(call.Id, "No permission token provided");
        }

        var validation = _permissionBroker.Validate(tokenId, call.RequiredCapability);
        if (!validation.IsValid)
        {
            LogToolExecution(call, success: false, error: validation.InvalidReason ?? "Invalid token", tokenId: tokenId);
            return ToolResult.PermissionDenied(call.Id, validation.InvalidReason ?? "Invalid token");
        }

        // ─────────────────────────────────────────────────────────────
        // Step 3: Execute the tool with context
        // ─────────────────────────────────────────────────────────────

        var context = new ToolExecutionContext
        {
            Call = call,
            Token = validation.Token!,
            CancellationToken = cancellationToken
        };

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = await tool.ExecuteAsync(context);
            stopwatch.Stop();

            LogToolExecution(call, success: true, durationMs: stopwatch.ElapsedMilliseconds, tokenId: tokenId);
            return ToolResult.Ok(call.Id, output, stopwatch.ElapsedMilliseconds, tokenId);
        }
        catch (ScopeViolationException ex)
        {
            stopwatch.Stop();
            LogToolExecution(call, success: false, error: $"Scope violation: {ex.Message}", durationMs: stopwatch.ElapsedMilliseconds, tokenId: tokenId);
            return ToolResult.PermissionDenied(call.Id, ex.Message);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            LogToolExecution(call, success: false, error: "Execution cancelled", durationMs: stopwatch.ElapsedMilliseconds, tokenId: tokenId);
            return ToolResult.Fail(call.Id, "Execution cancelled", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            LogToolExecution(call, success: false, error: ex.Message, durationMs: stopwatch.ElapsedMilliseconds, tokenId: tokenId);
            return ToolResult.Fail(call.Id, ex.Message, stopwatch.ElapsedMilliseconds);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // Audit Logging
    // ─────────────────────────────────────────────────────────────────

    private void LogToolExecution(
        ToolCall call,
        bool success,
        string? error = null,
        long? durationMs = null,
        string? tokenId = null)
    {
        var details = new Dictionary<string, object>
        {
            ["tool"] = call.Name,
            ["call_id"] = call.Id,
            ["capability"] = call.RequiredCapability.ToString(),
            ["purpose"] = call.Purpose
        };

        if (durationMs.HasValue)
            details["duration_ms"] = durationMs.Value;

        if (!string.IsNullOrEmpty(error))
            details["error"] = error;

        _auditLogger.Append(new AuditEvent
        {
            Actor = "runtime",
            Action = "TOOL_EXECUTE",
            Target = call.Name,
            Result = success ? "ok" : "error",
            PermissionTokenId = tokenId,
            Details = details
        });
    }
}
