# Sir Thaddeus — Architecture (10-minute read)

This is the public-facing architecture summary. A senior engineer should
finish it in ten minutes and know enough to navigate the code.

For the full version with package maps, sequence diagrams, and historical
notes, see [`docs/ARCHITECTURE.md`](ARCHITECTURE.md). For the v2 hybrid-shell
specifics, see [`docs/hybrid-shell.md`](hybrid-shell.md).

---

## One-paragraph summary

Sir Thaddeus is two cooperating processes plus a webview. The **Shell**
supervises a long-lived **Runtime**; the Runtime is an ASP.NET Core minimal
API on `127.0.0.1` that hosts the **React workspace** under `wwwroot/` and
exposes REST + WebSocket. Chat turns flow through an agent loop that talks
to a user-configured **OpenAI-compatible model** and to an **MCP server**
hosting the toolset. Every tool call crosses a **permission gate** that
asks the user, persists the answer, and writes to an audit log. Storage is
local files under `~/.thaddeus/`. **Voice** is an optional sidecar pair
(`VoiceHost` + `voice-backend`) and is Beta in v1.

```
┌──────────────────────────────────────────────────────────────────────┐
│  Thaddeus.Shell  (Windows; supervises, owns tray + global hotkeys)   │
│                          │                                           │
│                          │ launches + monitors                       │
│                          ▼                                           │
│  Thaddeus.Runtime  (ASP.NET Core, 127.0.0.1:<port>, bearer token)    │
│      ├─ wwwroot/                                                     │
│      │     └─ React workspace (web/dist → wwwroot)                   │
│      ├─ /api/* REST + /ws WebSocket                                  │
│      ├─ Agent loop ──▶ LLM client ──▶ user's OpenAI-compatible model │
│      ├─ Permission gate                                              │
│      ├─ MCP client ───▶ apps/mcp-server (stdio) ──▶ tool packages    │
│      └─ Storage:  ~/.thaddeus/{threads,wiki,memos,routines,logs}     │
│                                                                      │
│   (optional, Beta)                                                   │
│   VoiceHost ◀──── audio ────  React workspace                        │
│      └─ voice-backend (Python sidecar for ASR)                       │
└──────────────────────────────────────────────────────────────────────┘
```

---

## Layers

### 1. Shell — `src/Thaddeus.Shell` *(Windows)*

A small Windows-host process. Its job is **only** ergonomics:

- Start `Thaddeus.Runtime` as a managed child process.
- Open the embedded webview pointed at the runtime's loopback URL with the
  per-launch token.
- Own the tray icon and tray menu ("At your service" / "Stand down" /
  "Dismiss").
- Register global shortcuts (push-to-talk, Stop-all) via
  `WindowsGlobalShortcutAdapter`.
- Monitor the runtime; if the user kills it, the shell exits.

The Shell owns no business logic. Removing it leaves a working product —
you just lose the tray and global hotkeys.

### 2. Runtime — `src/Thaddeus.Runtime`

The product. An ASP.NET Core minimal API host bound to `127.0.0.1:<random-port>`.
Single-file publishable for `win-x64`, `osx-arm64`, `linux-x64`.

Responsibilities:

- **HTTP host.** Serves the static React bundle from `wwwroot/`.
- **REST API.** `Api/ChatApi.cs`, `Api/MemoryApi.cs`, `Api/RoutinesApi.cs`,
  `Api/SettingsApi.cs`, `Api/ActivityApi.cs`, `Api/StateApi.cs`,
  `Api/PermissionsApi.cs`, `Api/AudioApi.cs`. Each endpoint checks the
  bearer token middleware (`AuthFailureTracker` rate-limits abuse).
- **WebSocket** (`/ws`). `WebSocketBroadcaster` pushes runtime-state and
  event-bus messages to the workspace.
- **Agent loop.** `Chat/AssistantRouter.cs` and `Chat/LmStudioAssistant.cs`
  orchestrate the turn: build context, call the model, parse `tool_calls`,
  cross the permission gate, dispatch to MCP, stream tokens back.
- **Permission gate.** `ToolPermissionGate` brokers every tool call.
  Decisions persist in `runtime-settings.json` and audit-log to
  `~/.thaddeus/logs/audit.jsonl`.
- **State machine.** `State/RuntimeStateMachine.cs` governs
  `Idle → Listening → Transcribing → Thinking → ExecutingTools →
  AwaitingPermission → Speaking → Stopping`.
- **Stop-all.** `RuntimeStopAllService` aborts in-flight turns and
  signals sidecars. The kill switch in the UI calls this then asks the
  runtime to exit.

### 3. Web workspace — `web/`

React 18 + Vite 5 + TanStack Router + Zustand + Tailwind. Built into
`web/dist/`, then synced into `src/Thaddeus.Runtime/wwwroot/` for shipping.

Routes (the actual file-based routes, not aspirational ones):

- `/` Home — quick-start hero, recent threads, PTT.
- `/chat` and `/chat/$threadId` — thread list and conversation view.
- `/wiki` — full wiki/canvas surface with revisions, search, import/export.
- `/history` — chat history with pin / rename / delete.
- `/activity` and `/activity/$entryId` — read-only activity log.
- `/memory` — memos with create / edit / pin / delete and Markdown rendering.
- `/routines`, `/routines/$id/edit`, `/routines/$id/run`, `/routines/$id/history` — manual checklists.
- `/settings` — tabbed settings (General / Models / Audio & Voice / Files / Location / Advanced). Legacy `/settings/$category` redirects here.
- `/diagnostics` — runtime introspection.
- `/onboarding` — first-run walkthrough.
- `/compact` — Beta stub (Phase 2).

State stores live under `web/src/stores/`. API wrappers under `web/src/lib/`
all funnel through `runtimeFetch()` (`web/src/lib/runtime.ts`), which:

- Reads the bearer token from `<meta>` tags injected by the runtime before
  serving `index.html`.
- Sends the token via `Authorization: Bearer …` and `X-Thaddeus-Token`.
- Falls back to `?access_token=` query for endpoints that strip headers
  (and for WebSockets, per RFC 6750 §2.3).

### 4. Assistant pipeline

A turn flows like this:

```
user prompt
  → Chat/AssistantRouter.SendAsync
      → optional gatekeeper LLM (small, fast) classifies the turn
      → primary LLM client builds the request with allowed tools only
      → stream tokens via SSE-style chunking back to /ws
      → for each tool_call:
          → ToolPermissionGate.RequestAsync (may emit a permission event)
          → on approval: MCP client → apps/mcp-server → tool result
          → on denial: feed the denial back to the model
      → finalize: persist assistant message, emit ChatTurn activity
```

The **gatekeeper** is optional and on by default. It's a small fast model
that pre-filters which tools the primary model is even shown. This keeps
small primary models from chasing irrelevant tools and avoids LM Studio
load/offload churn on single-GPU rigs (`reusePrimaryForGatekeeperOnSharedEndpoint`).

### 5. MCP tool boundary — `apps/mcp-server` + `packages/mcp-*`

Tools are not in the runtime process. They live in a separate **MCP
server** that the runtime spawns and talks to over stdio (the standard
MCP transport).

- `apps/mcp-server` — the host process. Loads tool packages, advertises
  tool manifests.
- `packages/mcp-tools-core` — cross-platform tools (web search, file read,
  document parse, math, time, holidays).
- `packages/mcp-tools-windows` — Windows-only tools (clipboard, screen
  capture, OCR). Loaded only on Windows; unavailable on macOS/Linux by
  design.
- `packages/mcp-shared` — shared manifest and request/response shapes.

The boundary is a process boundary. A tool cannot reach into the runtime;
the runtime can only see the tool's declared input/output. This is the
seam where the **permission gate** lives — the runtime intercepts every
`tools/call` before it crosses to the MCP server.

### 6. Permission gate

`ToolPermissionGate` is the single chokepoint for tool execution. The
contract:

- Each tool belongs to a **group** (`Web`, `Files`, `System`, `Screen`,
  `MemoryRead`, `MemoryWrite`).
- For each group, the user has a **policy** in `runtime-settings.json`:
  `off | ask | always`.
- The developer-override field (`developerOverride`) can clamp the entire
  permission system to a stricter mode.
- A tool call resolves against policy:
  - `off` → instant refusal.
  - `always` → run, log, no prompt.
  - `ask` → emit a permission-request event over WebSocket; suspend the
    turn; resume when the user answers; record the decision.
- The user's answer is one of: **Deny / Once / Session / Always**.
  `Session` lives in memory; `Always` writes to settings.
- Every decision and every tool call is appended to
  `~/.thaddeus/logs/audit.jsonl`.

### 7. Local storage — `~/.thaddeus/`

Everything user-visible lives here, all flat files:

| Path | Contents |
|---|---|
| `runtime.lock` | per-launch lock + bearer token + port. |
| `runtime-settings.json` | settings document (LLM, voice, audio, privacy, files, limits, permission policy). |
| `threads/*.json` | one file per chat thread. |
| `memos/*.json` | one file per memo. |
| `routines/*.json` | routines + run-history files. |
| `wiki/<root>/...` | wiki pages as Markdown + revisions + a metadata index. |
| `logs/thaddeus-runtime-*.log` | Serilog daily rolling. |
| `logs/audit.jsonl` | append-only tool / permission audit. |

All stores are interface-fronted (`IThreadStore`, `IMemoStore`,
`IRoutineStore`, `ISettingsStore`) so a future remote-store backend can
slot in without changing call sites — but v1 is local-files-only.

### 8. Wiki / canvas — `packages/wiki`

The wiki is a first-class workspace surface, not a side panel. It is
built around:

- **Roots** — top-level workspaces (you can have several).
- **Folders** and **pages**.
- **Revisions** — every save records a revision; preview and roll back
  are first-class actions.
- **Search** — across the active root or all roots.
- **Import / export** — archive a root for backup or transfer.
- **Trash** — soft-delete with restore and purge.

Pages are **Markdown** at rest. The editor (`web/src/components/wiki/WikiMarkdownEditor.tsx`)
is a Tiptap-based editor; serialization round-trips through Markdown.

The wiki shares the agent loop: page chat, draft, and selected-text
rewrite all run the same model + permission gate as the chat surface.

### 9. Voice sidecar (Beta) — `apps/voice-host`, `apps/voice-backend`, `src/Thaddeus.Tts.*`

Voice is **explicitly off the critical path** in v1.

- **VoiceHost** (.NET) is a sidecar that exposes ASR (`/asr`) and TTS
  (`/tts`) over loopback. It hosts swappable engines via `ITtsEngine`.
- **voice-backend** (Python) implements ASR via `faster-whisper` /
  `whisper.cpp`.
- **Thaddeus.Tts.Kokoro** — default TTS engine (KokoroSharp, CPU).
- **Thaddeus.Tts.Piper.Legacy** — legacy fallback for Windows machines
  that already have Piper voices configured.

The runtime does not embed voice. If VoiceHost is down, every other
feature still works.

### 10. Trust and failure model

What the system promises:

- **Loopback only.** No LAN listener. The runtime cannot be reached from
  another machine.
- **Bearer token per launch.** Rotated on every start; embedded in the
  SPA bootstrap meta tags. Browsers without the token cannot reach
  `/api/*` or `/ws`.
- **Permission-gated tools.** No code path in v1 lets a tool call skip
  the gate. There is a meta-test under `tests/runtime/` that asserts this.
- **No background fire.** No `IHostedService` runs user-affecting work
  on a timer. Routines is enforced by a meta-test in
  `tests/runtime/Routines/`.
- **Audit on disk.** `audit.jsonl` records every tool call, every
  permission decision, every routine run. Append-only, line-delimited,
  trivially `tail`-able.

What the system explicitly **does not** promise:

- Hardened multi-tenant security. The runtime trusts its own machine.
- Defence against a local user who has shell access. They can read
  `~/.thaddeus/`. So can the model server they configured.
- Cross-process isolation beyond the OS process boundary.
- Auto-recovery from a corrupted settings file beyond loading defaults
  with a "safe mode" flag (which clears on next successful load).

Failures are biased toward **visible refusal** rather than silent
fallback:

- A tool call that fails permission yields an explicit denial back to
  the model, which then explains it to the user.
- A model endpoint that times out surfaces the timeout, not a fabricated
  answer.
- A corrupted thread file is logged and skipped at load; subsequent
  saves succeed.
- Stop-all is the user's escape hatch when any of the above misbehaves.

---

## Where to read next

- For the full layer responsibilities and package map:
  [`docs/ARCHITECTURE.md`](ARCHITECTURE.md).
- For the hybrid-shell rationale and security notes:
  [`docs/hybrid-shell.md`](hybrid-shell.md).
- For settings shape and storage layout:
  [`docs/SETTINGS.md`](SETTINGS.md).
- For packaging and platform binaries:
  [`docs/packaging.md`](packaging.md).
- For what's intentionally out of scope:
  [`V1_SCOPE.md`](../V1_SCOPE.md) and
  [`docs/KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md).
