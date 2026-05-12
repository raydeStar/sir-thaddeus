# Feature Gap Matrix

This matrix is the quickest way to review Sir Thaddeus for completion. It is intentionally product-oriented rather than code-volume-oriented: a feature only counts as `Complete` if it is present in the active hybrid workspace and belongs in the v1 core promise, not merely because related code exists somewhere in the repo.

Use this alongside [ARCHITECTURE.md](ARCHITECTURE.md). If you need the short human-readable version first, start with [ARCHITECTURE_EXECUTIVE_SUMMARY.md](ARCHITECTURE_EXECUTIVE_SUMMARY.md).

## Status Legend

| Status | Meaning |
| --- | --- |
| `Complete` | Implemented in the active hybrid runtime/workspace surface, with no major architectural gap remaining. |
| `Beta` | Present, but not a core v1 promise; requires live validation, machine setup, or UX polish before promotion. |
| `Deferred` | Explicitly out of v1 scope even if supporting code or older notes exist. |
| `Partial` | Implemented, but still limited by platform scope, placeholder UX, manual validation needs, or transitional architecture. |
| `Missing` | Not present in the active hybrid product surface. |
| `Legacy-only` | Exists only in the legacy headless/harness path and should not be counted as part of the active hybrid product. |

## Product Surface And Host

| Capability | Status | Present Now | Gap Or Caveat | Primary Evidence |
| --- | --- | --- | --- | --- |
| Hybrid runtime host | `Complete` | Loopback REST + WebSocket host, workspace bootstrap, local storage composition, sidecar orchestration | Intentionally loopback-only | [src/Thaddeus.Runtime/Program.cs](../src/Thaddeus.Runtime/Program.cs), [src/Thaddeus.Runtime/Api/StateApi.cs](../src/Thaddeus.Runtime/Api/StateApi.cs), [src/Thaddeus.Runtime/Api/WorkspaceHostingExtensions.cs](../src/Thaddeus.Runtime/Api/WorkspaceHostingExtensions.cs) |
| Browser workspace | `Complete` | Active routes for chat, wiki, history, activity, memory, routines, settings, diagnostics, onboarding, and compact mode | Compact mode exists but is beta/minimal | [web/src/routes/](../web/src/routes/), [docs/hybrid-shell.md](hybrid-shell.md) |
| Shell-managed launch and shutdown | `Complete` | Ensures runtime is running, performs IPC handshake, opens workspace window, tears runtime down on exit | Richest behavior is on Windows | [src/Thaddeus.Shell/Program.cs](../src/Thaddeus.Shell/Program.cs) |
| Compact panel | `Beta` | Dedicated route and shell launcher exist | Current route is only an idle pill with status and kill button; transcript/PTT UX is not there yet | [web/src/routes/compact.tsx](../web/src/routes/compact.tsx), [src/Thaddeus.Shell/Windows/CompactPanelLauncher.cs](../src/Thaddeus.Shell/Windows/CompactPanelLauncher.cs) |
| Tray integration | `Beta` | Tray controller, menu entries, close-to-tray path, Windows tray adapter | Windows-specific and still needs live validation on target machines | [src/Thaddeus.Shell/ShellSessionController.cs](../src/Thaddeus.Shell/ShellSessionController.cs), [src/Thaddeus.Shell/Platform/Windows/WindowsTrayAdapter.cs](../src/Thaddeus.Shell/Platform/Windows/WindowsTrayAdapter.cs) |
| Global shortcuts and shell push-to-talk hooks | `Beta` | Compact toggle, stop-all, and push-to-talk shortcut registration exist in the shell | Windows-specific and still dependent on live desktop validation | [src/Thaddeus.Shell/Program.cs](../src/Thaddeus.Shell/Program.cs) |

## Chat, Assistant, And Permissions

| Capability | Status | Present Now | Gap Or Caveat | Primary Evidence |
| --- | --- | --- | --- | --- |
| Threaded chat with streaming replies | `Complete` | Thread CRUD, auto-titling, pin/rename/delete, streamed assistant deltas, retry latest reply | None architectural | [src/Thaddeus.Runtime/Api/ChatApi.cs](../src/Thaddeus.Runtime/Api/ChatApi.cs), [web/src/routes/chat.$threadId.tsx](../web/src/routes/chat.$threadId.tsx) |
| Local-model integration with stub fallback | `Complete` | LM Studio, Ollama OpenAI shim, custom OpenAI-compatible, hosted OpenAI, and stub fallback when unreachable | Final quality still depends on the configured model | [src/Thaddeus.Runtime/Chat/AssistantRouter.cs](../src/Thaddeus.Runtime/Chat/AssistantRouter.cs), [web/src/routes/settings.tsx](../web/src/routes/settings.tsx) |
| Footman gatekeeper / tool-family narrowing | `Complete` | Gatekeeper wiring, status endpoint, and tool-family narrowing path are present | Final tuning remains model-dependent | [src/Thaddeus.Runtime/Chat/LmStudioAssistant.cs](../src/Thaddeus.Runtime/Chat/LmStudioAssistant.cs), [src/Thaddeus.Runtime/Api/SettingsApi.cs](../src/Thaddeus.Runtime/Api/SettingsApi.cs) |
| Permission prompts and policy persistence | `Complete` | Permission queue, modal UI, once/session/always decisions, persisted group policy, WebSocket events | None architectural | [src/Thaddeus.Runtime/Tools/ToolPermissionGate.cs](../src/Thaddeus.Runtime/Tools/ToolPermissionGate.cs), [web/src/stores/permissionsStore.ts](../web/src/stores/permissionsStore.ts), [web/src/components/PermissionModal.tsx](../web/src/components/PermissionModal.tsx) |
| Personality and profile administration in the active workspace | `Deferred` | Underlying personality/profile concepts exist elsewhere in the repo | No active workspace admin surface for these concepts; explicitly out of v1 | [docs/hybrid-shell.md](hybrid-shell.md), [apps/headless-runtime/](../apps/headless-runtime/) |

## MCP, Tools, And Knowledge Hooks

| Capability | Status | Present Now | Gap Or Caveat | Primary Evidence |
| --- | --- | --- | --- | --- |
| Manifest-driven MCP boundary | `Complete` | Runtime spawns stdio MCP child, lists tools, builds model tool definitions, restarts child on relevant settings changes | None architectural | [src/Thaddeus.Runtime/Tools/McpClientHost.cs](../src/Thaddeus.Runtime/Tools/McpClientHost.cs), [apps/mcp-server/SirThaddeus.McpServer/Program.cs](../apps/mcp-server/SirThaddeus.McpServer/Program.cs), [packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs](../packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs) |
| Web and live-data MCP tools | `Complete` | Search, page fetch, place discovery, place lookup, weather, timezone, holidays, feeds, URL status | Output quality depends on providers and live network conditions | [packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/WebSearchTools.cs](../packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/WebSearchTools.cs), [packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs](../packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs) |
| File and document MCP tools | `Complete` | File read/list with preview/apply flow and document extraction for PDF, DOCX, XLSX, CSV, RTF, Markdown, and text | Permission- and root-scoped by design | [packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/FileTools.cs](../packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/FileTools.cs), [packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs](../packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs) |
| Memory audit workspace | `Complete` | Facts, nuggets, events, and profiles review surface; fact/nugget edit and delete; nugget pinning; reflection actions | Distinct from the lower-level agent memory MCP tools | [src/Thaddeus.Runtime/Api/MemoryAuditApi.cs](../src/Thaddeus.Runtime/Api/MemoryAuditApi.cs), [web/src/routes/memory.tsx](../web/src/routes/memory.tsx) |
| Agent memory MCP hooks | `Complete` | Retrieve, store, update, list, and delete memory facts are in the MCP manifest | Review/debug surface is thinner than the user memo workspace | [packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/MemoryTools.cs](../packages/mcp-tools-core/SirThaddeus.McpTools.Core/Tools/MemoryTools.cs), [packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs](../packages/mcp-shared/SirThaddeus.McpShared/ToolManifest.cs) |
| Windows desktop observation hooks | `Beta` | Screen capture, active-window lookup, clipboard read/write exist | Windows-only and should be treated as needing live validation rather than assumed complete everywhere | [packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/Tools/ScreenTools.cs](../packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/Tools/ScreenTools.cs), [packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/Tools/ClipboardTools.cs](../packages/mcp-tools-windows/SirThaddeus.McpTools.Windows/Tools/ClipboardTools.cs) |

## Wiki, Routines, And Diagnostics

| Capability | Status | Present Now | Gap Or Caveat | Primary Evidence |
| --- | --- | --- | --- | --- |
| Wiki canvas CRUD and revisions | `Complete` | Roots, folders, pages, move/rename/delete/restore/purge, revisions, graph endpoint, search, import/export | None architectural | [src/Thaddeus.Runtime/Api/WikiApi.cs](../src/Thaddeus.Runtime/Api/WikiApi.cs), [web/src/routes/wiki.tsx](../web/src/routes/wiki.tsx) |
| Wiki assistant actions | `Complete` | Page chat, draft generation, and selected-text rewrite endpoints | Quality depends on active assistant/model configuration | [src/Thaddeus.Runtime/Wiki/WikiPageAssistantService.cs](../src/Thaddeus.Runtime/Wiki/WikiPageAssistantService.cs), [src/Thaddeus.Runtime/Api/WikiApi.cs](../src/Thaddeus.Runtime/Api/WikiApi.cs) |
| Manual routines and run history | `Complete` | Routine CRUD, checklist execution, run patching, completion, discard, history | Explicitly user-driven and local-first | [src/Thaddeus.Runtime/Api/RoutinesApi.cs](../src/Thaddeus.Runtime/Api/RoutinesApi.cs), [web/src/routes/routines.tsx](../web/src/routes/routines.tsx) |
| Scheduled or background automations | `Deferred` | No active scheduler or unattended background run path in the hybrid runtime | The current API is intentionally manual; do not count package references or comments as shipped scheduling | [src/Thaddeus.Runtime/Api/RoutinesApi.cs](../src/Thaddeus.Runtime/Api/RoutinesApi.cs), [docs/hybrid-shell.md](hybrid-shell.md) |
| Activity feed and basic diagnostics | `Complete` | Activity list/detail, diagnostics endpoint, runtime info, state and health surfaces | Good operational baseline already exists | [src/Thaddeus.Runtime/Api/ActivityApi.cs](../src/Thaddeus.Runtime/Api/ActivityApi.cs), [src/Thaddeus.Runtime/Api/StateApi.cs](../src/Thaddeus.Runtime/Api/StateApi.cs), [web/src/routes/activity.tsx](../web/src/routes/activity.tsx), [web/src/routes/diagnostics.tsx](../web/src/routes/diagnostics.tsx) |
| Advanced diagnostics or audit-search panes | `Deferred` | No dedicated advanced audit search/admin surface in the active workspace | Review still relies on logs, activity feed, and raw artifacts more than rich in-app admin tools | [docs/hybrid-shell.md](hybrid-shell.md), [web/src/routes/diagnostics.tsx](../web/src/routes/diagnostics.tsx) |

## Voice, Platform, And Release Readiness

| Capability | Status | Present Now | Gap Or Caveat | Primary Evidence |
| --- | --- | --- | --- | --- |
| Voice-host supervision and health APIs | `Beta` | Runtime can ensure the voice host, expose health, and warm it up | Present, but voice is not a core v1 promise | [src/Thaddeus.Runtime/Voice/VoiceHostProcessSupervisor.cs](../src/Thaddeus.Runtime/Voice/VoiceHostProcessSupervisor.cs), [src/Thaddeus.Runtime/Api/VoiceApi.cs](../src/Thaddeus.Runtime/Api/VoiceApi.cs) |
| End-to-end browser ASR/TTS path | `Beta` | Browser mic capture, ASR proxy, TTS proxy, Piper voice enumeration, voice warmup path all exist | Still depends on live sidecar health, models, and machine setup | [src/Thaddeus.Runtime/Api/VoiceApi.cs](../src/Thaddeus.Runtime/Api/VoiceApi.cs), [web/src/lib/voiceApi.ts](../web/src/lib/voiceApi.ts), [web/src/routes/chat.$threadId.tsx](../web/src/routes/chat.$threadId.tsx) |
| Windows desktop voice ergonomics | `Beta` | Shell push-to-talk phases and global shortcut hooks exist | Real-world Windows validation still matters more than code inspection here | [src/Thaddeus.Shell/Program.cs](../src/Thaddeus.Shell/Program.cs), [src/Thaddeus.Runtime/Api/VoiceApi.cs](../src/Thaddeus.Runtime/Api/VoiceApi.cs) |
| Self-contained runtime packaging | `Complete` | Single-file runtime packaging flow is documented and wired | Installer polish is a separate concern | [docs/packaging.md](packaging.md), [src/Thaddeus.Runtime/Thaddeus.Runtime.csproj](../src/Thaddeus.Runtime/Thaddeus.Runtime.csproj) |
| Polished installers and auto-update | `Deferred` | No MSIX, macOS app bundle, Linux desktop packaging, or update channel counted as done | Explicitly listed as deferred | [docs/packaging.md](packaging.md) |
| Cross-platform parity with Windows desktop UX | `Deferred` | Core runtime and MCP layers are cross-platform-oriented | Full tray, shortcut, and desktop automation parity is not in v1 | [README.md](../README.md), [docs/hybrid-shell.md](hybrid-shell.md) |
| Legacy terminal runtime | `Legacy-only` | Still exists and still matters for harness work | Should not be confused with the main v2 product surface | [apps/headless-runtime/](../apps/headless-runtime/), [docs/hybrid-shell.md](hybrid-shell.md) |

## Recommended Review Order

1. Read [ARCHITECTURE_EXECUTIVE_SUMMARY.md](ARCHITECTURE_EXECUTIVE_SUMMARY.md) for the short product picture.
2. Use this matrix to decide which areas are actually done, partial, missing, or legacy-only.
3. Use [ARCHITECTURE.md](ARCHITECTURE.md) when you need full subsystem detail.
4. Use [docs/hybrid-shell.md](hybrid-shell.md) and [docs/packaging.md](packaging.md) to confirm what the hybrid product surface explicitly claims.
