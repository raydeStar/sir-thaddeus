# Tool Conflict Matrix (Turn-Level)

This matrix is applied inside the tool loop before any MCP calls are executed for a turn.
The runtime resolves requested tool calls to winners and skipped calls, then executes winners only.

## Resolution Order

1. Policy forbid check (`policy_forbid`)
2. Tool-specific exception rules
3. Capability-level conflict rules
4. Deterministic tie-breaker

## Capability-Level Conflicts

- `WebSearch` vs `SystemExecute` -> keep lower-risk tool
- `BrowserNavigate` vs `SystemExecute` -> keep lower-risk tool
- `ScreenCapture` vs `SystemExecute` -> keep lower-risk tool
- `MemoryWrite` vs `SystemExecute` -> keep lower-risk tool
- `MemoryWrite` vs `WebSearch` -> keep lower-risk tool

## Tool-Specific Exception Rules

- `screen_capture` vs `get_active_window` -> winner: `get_active_window` (`deterministic_priority`)

## Audit Reason Enum

Every skipped call uses one reason from this bounded enum set:

- `explicit_user_request`
- `lower_risk`
- `deterministic_priority`
- `policy_forbid`

## Notes

- Conflicts are modeled primarily at capability level to survive tool renames.
- Tool-specific rules are intentionally sparse and should only be added with tests.
- Unmapped tools are hidden by default at policy filtering time.

