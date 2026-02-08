# Tool Routing V2 — No-Collision Pipeline

## Status: PLAN (not yet implemented)

---

## Problem Statement

The current tool routing is a series of `if/else` branches in
`AgentOrchestrator.ProcessAsync`:

```
classify intent (LLM) → pick a code path → execute
```

Three intents exist today: `Casual`, `WebLookup`, `Tooling`. Each has
bespoke logic. Adding a new tool or mode means adding another branch
and hoping nothing collides. The model sees all tools (or a hardcoded
subset) and picks — which means the model is simultaneously deciding
**what to do** and **what it's allowed to do**. That's the root of
every collision bug we've hit.

### What already works (keep it)

| Component              | Status  | Notes                                                    |
|------------------------|---------|----------------------------------------------------------|
| `ClassifyIntentAsync`  | Working | LLM + heuristic fallbacks, 3 intents                    |
| `BuildToolDefinitionsAsync` | Working | MCP auto-discovery + schema sanitization            |
| `BuildMemoryOnlyToolsAsync` | Working | Casual gets memory tools only                      |
| `RunToolLoopAsync`     | Working | Extracted, handles multi-tool, errors, safety cap        |
| `PermissionBroker`     | Built   | Token issuance, validation, revocation, audit — **NOT WIRED** |
| `EnforcingToolRunner`  | Built   | Validates tokens before execution — **NOT WIRED**        |
| `JsonLineAuditLogger`  | Working | Agent-level logging active; tool-level logging exists but unused |
| MCP tool server        | Working | 10 tools registered with schema metadata                 |

### What's broken or missing

1. **No policy gate** — the model decides which tools to call from the
   full set. A confused model can call `memory_store_facts` during a
   web search, or `screen_capture` during casual chat.

2. **Permissions not enforced** — `PermissionBroker` and
   `EnforcingToolRunner` exist but the agent→MCP path bypasses them.
   Tools execute without permission checks (violates P1: universal
   enforcement point).

3. **No capability mapping** — MCP tools don't declare which
   `Capability` they require. No way to map `screen_capture` →
   `ScreenRead` or `SystemExecute` → `SystemExecute`.

4. **Memory writes fire mid-thought** — proactive memory storage
   happens inside the tool loop. The design doc says memory writes
   belong in post-hooks with user consent.

5. **Intent taxonomy is too coarse** — 3 intents can't distinguish
   "search the web" from "browse a specific page" from "read a file."
   All non-casual, non-search messages dump into `Tooling` with
   every tool exposed.

---

## Target Architecture

```
  User Message
       │
       ▼
  ┌─────────────────────┐
  │  A) Intent Router    │  LLM call #1 — NO tools
  │  (structured JSON)   │  Classifies intent + requirements
  └──────────┬──────────┘
             │ RouterOutput JSON
             ▼
  ┌─────────────────────┐
  │  B) Policy Gate      │  Deterministic code — NO LLM
  │  (tool filter)       │  Maps intent → allowed tools
  └──────────┬──────────┘  Checks permissions, requests if needed
             │ allowed_tools[]
             ▼
  ┌─────────────────────┐
  │  C) Executor         │  LLM call #2 — only allowed tools
  │  (tool loop)         │  Solves the task
  └──────────┬──────────┘
             │ response + tool results
             ▼
  ┌─────────────────────┐
  │  D) Post-Hooks       │  Deterministic code — NO LLM
  │  (side effects)      │  Memory writes, audit, cleanup
  └─────────────────────┘
```

### What changes vs. today

| Today                           | V2                                      |
|---------------------------------|-----------------------------------------|
| `ClassifyIntentAsync` → enum    | Router → structured `RouterOutput` JSON  |
| Hardcoded tool subsets           | Policy gate maps intent → tool groups    |
| All tools or memory-only         | Only policy-approved tools exposed       |
| Permissions not enforced         | Policy gate checks + requests permissions |
| Memory writes in tool loop       | Memory writes in post-hooks only         |
| 3 intents                        | 8+ intents (expandable)                  |

---

## Phase 1: Router Output (structured classification)

**Goal:** Replace the `ChatIntent` enum with a richer `RouterOutput`
that tells the policy gate exactly what the message needs.

### 1.1 Define `RouterOutput`

```csharp
public sealed record RouterOutput
{
    public required string Intent       { get; init; }  // e.g. "lookup_search"
    public bool NeedsWeb                { get; init; }
    public bool NeedsBrowserAutomation  { get; init; }
    public bool NeedsSearch             { get; init; }
    public bool NeedsMemoryRead         { get; init; }
    public bool NeedsMemoryWrite        { get; init; }
    public bool NeedsFileAccess         { get; init; }
    public bool NeedsScreenRead         { get; init; }
    public bool NeedsSystemExecute      { get; init; }
    public string RiskLevel             { get; init; } = "low";  // low | medium | high
    public double Confidence            { get; init; } = 0.5;
}
```

### 1.2 Intent taxonomy (V1)

| Intent                | Description                                  | Typical tools              |
|-----------------------|----------------------------------------------|----------------------------|
| `chat_only`           | Casual chat, greetings, small talk           | None                       |
| `lookup_search`       | Web search for current info                   | `web_search`               |
| `browse_once`         | Navigate to a specific URL + read content     | `browser_navigate`         |
| `one_shot_discovery`  | Research task (search + browse + judgment)     | `web_search`, `browser_navigate` |
| `screen_observe`      | What's on the user's screen                   | `screen_capture`, `get_active_window` |
| `file_task`           | Read/list files                               | `file_read`, `file_list`   |
| `system_task`         | Execute a system command                      | `system_execute`           |
| `memory_read`         | Recall stored facts                           | (pre-fetch, no executor tool) |
| `memory_write`        | User explicitly asks to remember something    | (post-hook only)           |

### 1.3 Classification strategy

Keep the current hybrid approach but output `RouterOutput`:

1. **Fast-path prefixes** — `/search`, `/chat`, `/browse` etc.
2. **Heuristic detection** — `LooksLikeWebSearchRequest`,
   `LooksLikeMemoryWriteRequest`, `LooksLikeScreenRequest` etc.
3. **LLM classification** — prompt asks for structured JSON. Parse
   with `JsonSerializer`. On parse failure, fall back to heuristics.
4. **Confidence threshold** — if LLM confidence < 0.5, use heuristics
   as tiebreaker (same pattern as `InferFallbackIntent` today).

### 1.4 Migration path

- `ChatIntent.Casual` → `chat_only`
- `ChatIntent.WebLookup` → `lookup_search`
- `ChatIntent.Tooling` → split into `screen_observe`, `file_task`,
  `system_task`, `memory_write`, `browse_once` based on heuristics

**Keep the old enum as a compatibility layer** during migration. Map
`RouterOutput.Intent` → `ChatIntent` for any code that still uses it.

---

## Phase 2: Policy Gate (deterministic tool filter)

**Goal:** A pure function that takes `RouterOutput` → `PolicyDecision`
(allowed tools, required permissions, denied tools).

### 2.1 Define `PolicyDecision`

```csharp
public sealed record PolicyDecision
{
    public required IReadOnlyList<string> AllowedTools    { get; init; }
    public required IReadOnlyList<string> ForbiddenTools  { get; init; }
    public required IReadOnlyList<Capability> RequiredPermissions { get; init; }
}
```

### 2.2 Policy table

Deterministic mapping — no LLM involved:

```csharp
public static PolicyDecision Evaluate(RouterOutput router)
{
    return router.Intent switch
    {
        "chat_only" => new PolicyDecision
        {
            AllowedTools = [],
            ForbiddenTools = ["*"],
            RequiredPermissions = []
        },

        "lookup_search" => new PolicyDecision
        {
            AllowedTools = ["web_search"],
            ForbiddenTools = ["screen_capture", "system_execute",
                              "file_read", "memory_store_facts"],
            RequiredPermissions = [Capability.BrowserControl]
        },

        "screen_observe" => new PolicyDecision
        {
            AllowedTools = ["screen_capture", "get_active_window"],
            ForbiddenTools = ["web_search", "system_execute",
                              "file_read", "memory_store_facts"],
            RequiredPermissions = [Capability.ScreenRead]
        },

        // ... etc for each intent
    };
}
```

### 2.3 Tool filtering

```csharp
// In AgentOrchestrator, before calling RunToolLoopAsync:
var allTools = await BuildToolDefinitionsAsync(ct);
var filtered = allTools
    .Where(t => policy.AllowedTools.Contains(t.Function.Name))
    .ToList();
```

**Key rule:** Forbidden tools are *not present* in the executor's
tool list. The model cannot call what it cannot see.

### 2.4 Capability mapping (new)

Map MCP tool names → required `Capability`:

```csharp
private static readonly Dictionary<string, Capability> ToolCapabilities = new()
{
    ["screen_capture"]     = Capability.ScreenRead,
    ["get_active_window"]  = Capability.ScreenRead,
    ["browser_navigate"]   = Capability.BrowserControl,
    ["web_search"]         = Capability.BrowserControl,
    ["file_read"]          = Capability.FileAccess,
    ["file_list"]          = Capability.FileAccess,
    ["system_execute"]     = Capability.SystemExecute,
    ["memory_store_facts"] = Capability.FileAccess,  // writes to SQLite
    ["memory_update_fact"] = Capability.FileAccess,
    ["MemoryRetrieve"]     = Capability.FileAccess,  // reads from SQLite
};
```

---

## Phase 3: Wire Permissions

**Goal:** Connect `PermissionBroker` to the agent→MCP flow so tool
calls require valid permission tokens.

### 3.1 Permission check before tool execution

In `RunToolLoopAsync`, before calling `_mcp.CallToolAsync`:

```csharp
// Check if this tool requires a permission
if (ToolCapabilities.TryGetValue(toolCall.Function.Name, out var cap))
{
    var token = _permissionBroker.GetActiveToken(cap);
    if (token is null)
    {
        // Request permission from user (via UI callback or auto-deny)
        result = "Permission denied: no active token for " + cap;
        success = false;
        continue;
    }

    var validation = _permissionBroker.Validate(token.Id, cap);
    if (!validation.IsValid)
    {
        result = "Permission denied: " + validation.Reason;
        success = false;
        continue;
    }
}
```

### 3.2 Permission request flow

The policy gate identifies which permissions are needed. Before
entering the executor, request any missing permissions:

```
PolicyDecision.RequiredPermissions
    → check PermissionBroker for active tokens
    → if missing, prompt user (UI callback)
    → if denied, skip tool and return "permission denied"
    → if granted, issue time-boxed token
```

### 3.3 UI integration

The permission prompt goes through the existing UI callback mechanism
(or auto-denies in headless mode). The UI shows:

- What capability is requested
- Why (which tool, what intent)
- Duration (default: 5 minutes)
- Scope (if applicable)

This is the biggest UX change. It should feel fast and non-intrusive
for common operations, with "Allow for this session" as an option.

---

## Phase 4: Post-Hooks

**Goal:** Side effects happen after the executor, not during.

### 4.1 Memory write post-hook

**Today:** Memory writes happen inside the tool loop (proactive
storage). The LLM calls `memory_store_facts` mid-conversation.

**V2:** Memory writes move to a post-hook:

```
Executor finishes
    → scan response for "memory write proposals"
    → if user explicitly said "remember X": auto-store
    → if LLM proposed "should I remember X?": wait for user reply
    → store in post-hook, not in executor
```

### 4.2 Design tension: proactive vs. explicit memory

The current proactive memory storage (LLM silently stores facts)
conflicts with the design doc's "explicit consent" rule. Options:

| Option | Description | Tradeoff |
|--------|-------------|----------|
| A) Strict consent | Memory writes only on explicit "remember" or approved proposal | Safer, but forgets casual info |
| B) Silent + audit | Keep proactive storage but log everything, user can review/delete | Convenient, but less transparent |
| C) Hybrid | Auto-store low-sensitivity facts, prompt for sensitive ones | Best UX, more complex |

**Recommendation:** Option C (hybrid). Low-sensitivity facts
(`user likes pie`, `user is a software engineer`) auto-store with
audit. Sensitive facts (names, locations, health info) require
explicit consent. The sensitivity field already exists in
`MemoryFact.Sensitivity`.

### 4.3 Audit post-hook

Emit a structured audit event for the entire pipeline:

```json
{
  "timestamp": "...",
  "sessionId": "...",
  "pipeline": {
    "router": { "intent": "screen_observe", "confidence": 0.91 },
    "policy": { "allowed": ["screen_capture"], "permissions": ["ScreenRead"] },
    "executor": { "toolCalls": [...], "roundTrips": 2 },
    "postHooks": { "memoryWrites": 0, "permissionsRevoked": 0 }
  }
}
```

---

## Phase 5: Observability

**Goal:** When something breaks, know which layer lied.

### 5.1 Structured logging per stage

| Log event              | Stage    | Content                              |
|------------------------|----------|--------------------------------------|
| `ROUTER_OUTPUT`        | Router   | Full `RouterOutput` JSON             |
| `POLICY_DECISION`      | Policy   | Allowed/forbidden tools, permissions |
| `PERMISSION_CHECK`     | Policy   | Token status per required capability |
| `EXECUTOR_TOOL_CALL`   | Executor | Tool name, args, result, duration    |
| `EXECUTOR_RESPONSE`    | Executor | Final text, round trips              |
| `POSTHOOK_MEMORY`      | Post     | What was stored/skipped              |
| `POSTHOOK_CLEANUP`     | Post     | Permissions revoked, state cleanup   |

### 5.2 Debug mode

Add a `--debug-routing` flag (or setting) that dumps the full pipeline
to console/log for every message. Invaluable for diagnosing "why
didn't it call the right tool?"

---

## Implementation Order

The phases above describe the architecture. The implementation order
is different — we prioritize what gives the most stability first.

### Sprint 1: Policy Gate + Tool Filtering

**Why first:** This is the single biggest win. It eliminates tool
collisions without changing the classifier or permission model.

1. Create `PolicyGate.cs` in `packages/agent/`
2. Define the intent→tool mapping table
3. Replace `BuildMemoryOnlyToolsAsync` with `PolicyGate.Evaluate`
4. Filter tools before `RunToolLoopAsync`
5. Add `POLICY_DECISION` audit event
6. Tests: verify each intent gets correct tools, forbidden tools absent

**Estimated scope:** ~200 lines new code, ~50 lines modified

### Sprint 2: Structured Router Output

**Why second:** The policy gate works with the current 3 intents, but
richer classification makes it more precise.

1. Define `RouterOutput` record
2. Update `ClassifyIntentAsync` to produce `RouterOutput`
3. Add heuristic detectors for new intents (`screen_observe`,
   `file_task`, `system_task`)
4. Keep LLM classification as refinement layer
5. Map `RouterOutput` → old `ChatIntent` for compatibility
6. Tests: classification accuracy for each new intent

**Estimated scope:** ~300 lines new code, ~100 lines modified

### Sprint 3: Wire Permissions

**Why third:** Policy gate must exist first so we know what
permissions to request per intent.

1. Add `ToolCapabilities` mapping (tool name → Capability)
2. Add permission check in `RunToolLoopAsync`
3. Add permission request callback (UI or auto-deny)
4. Wire `PermissionBroker` into `AgentOrchestrator` constructor
5. Add `PERMISSION_CHECK` audit events
6. Tests: tool denied without permission, granted with token

**Estimated scope:** ~150 lines new code, ~100 lines modified

### Sprint 4: Post-Hooks + Memory Consent

**Why last:** Requires the most design decisions (proactive vs.
explicit) and touches the most code paths.

1. Create `PostHookRunner.cs` in `packages/agent/`
2. Move memory write logic from executor to post-hook
3. Implement sensitivity-based auto-store vs. prompt
4. Remove `memory_store_facts` from executor tool set
5. Add `POSTHOOK_MEMORY` audit events
6. Tests: low-sensitivity auto-stores, high-sensitivity prompts

**Estimated scope:** ~250 lines new code, ~150 lines modified

---

## Decision Log

Decisions that need to be made before/during implementation:

| # | Question | Options | Decision |
|---|----------|---------|----------|
| 1 | Memory write consent model | Strict / Silent+audit / Hybrid | TBD |
| 2 | Permission UX for common ops | Per-call / Per-session / Per-capability | TBD |
| 3 | Router confidence threshold | 0.5 / 0.7 / 0.8 | TBD |
| 4 | Should `chat_only` have memory tools? | Yes (proactive) / No (strict) | TBD |
| 5 | How to handle intent misclassification | Retry / Fallback / Ask user | TBD |
| 6 | Tool groups mutually exclusive? | Strict / Allow combos via policy | TBD |

---

## Risk Assessment

| Risk | Impact | Mitigation |
|------|--------|------------|
| Small LLM can't produce reliable JSON for RouterOutput | High | Keep heuristic fallbacks as primary, LLM as refinement |
| Permission prompts slow down common tasks | Medium | Per-session grants, "Always allow" for safe capabilities |
| Policy table gets large as tools grow | Low | Group by capability, not individual tool |
| Memory post-hook loses context (no longer mid-conversation) | Medium | Pass full conversation history to post-hook |
| Migration breaks existing behavior | High | Keep old ChatIntent as compatibility layer during transition |

---

## Files That Will Be Modified

| File | Sprint | Change |
|------|--------|--------|
| `packages/agent/SirThaddeus.Agent/AgentOrchestrator.cs` | 1-4 | Router, policy gate integration, permission checks, post-hooks |
| `packages/agent/SirThaddeus.Agent/PolicyGate.cs` | 1 | **NEW** — deterministic tool filter |
| `packages/agent/SirThaddeus.Agent/RouterOutput.cs` | 2 | **NEW** — structured classification |
| `packages/agent/SirThaddeus.Agent/PostHookRunner.cs` | 4 | **NEW** — post-execution side effects |
| `packages/agent/SirThaddeus.Agent/IMcpToolClient.cs` | 3 | Maybe extend for permission token passing |
| `packages/permission-broker/` | 3 | Wire into agent flow |
| `tests/SirThaddeus.Tests/AgentOrchestratorTests.cs` | 1-4 | New tests per sprint |
| `tests/SirThaddeus.Tests/PolicyGateTests.cs` | 1 | **NEW** — policy table tests |
| `tests/SirThaddeus.Tests/RouterOutputTests.cs` | 2 | **NEW** — classification tests |

---

## Non-Goals (V1)

- **Perplexica integration** — search provider routing is separate
  from tool routing. Perplexica would slot in as a `web_search`
  provider, not a new tool group.
- **Service mode / workers** — persistence layer is a separate
  workstream (see architectural-design.md).
- **Multi-agent orchestration** — one agent, one pipeline.
- **Dynamic policy learning** — policy table is static code, not
  ML-driven.
