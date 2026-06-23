namespace SirThaddeus.Contracts;

/// <summary>
/// Per-test reset payload for the headless runtime when reused across
/// harness tests. Lets the harness swap the test-scoped env-var overrides
/// (allowed_tools, stub overrides) and reset volatile state without
/// having to restart the runtime process.
/// </summary>
public sealed record HarnessResetRequest(
    string? AllowedTools = null,
    IReadOnlyDictionary<string, string?>? StubOverrides = null,
    bool ClearMemoryData = true,
    bool ClearChatHistory = true);

public sealed record HarnessResetResponse(
    bool Ok,
    int MemoryRowsDeleted,
    int StubVarsCleared,
    int StubVarsSet,
    string? AllowedToolsApplied);
