# Sir Thaddeus — v1.0 Scope

This is the contract for v1.0. It exists to make it impossible for future work
to expand v1 scope by accident. If a feature is not on the **Core v1** list, it
is either Beta, Deferred, or a Non-goal — even if it works today.

---

## One-line positioning

> A local-first AI workspace for controlled agentic workflows: chat,
> MCP-powered tools, explicit permissions, local storage, diagnostics, and
> durable wiki/canvas knowledge.

## Target user

A power user — developer, researcher, or technical operator — who:

- runs a local model (LM Studio, Ollama, or any OpenAI-compatible endpoint),
- wants chat plus tools without a cloud account or telemetry,
- understands what an MCP server is and is comfortable approving tool calls,
- prefers explicit control over background autonomy.

Not aimed at consumers, not aimed at IT-managed deployments, not aimed at
unattended automation.

## Core v1 promise

Sir Thaddeus runs on your machine, talks to a model you chose, only uses
tools you approved, and keeps every action visible and stoppable.

If it acts, you see it. If you press **STOP**, it stops.

---

## Core v1 features (must work, must be documented, must ship)

The v1 product surface is:

- `src/Thaddeus.Shell/`
- `src/Thaddeus.Runtime/`
- `web/`
- `apps/mcp-server/`
- `packages/mcp-*` and `packages/wiki/`
- the runtime/tool/storage packages used by the hybrid surface

The features that must work end-to-end on v1 day one:

| # | Feature | Surface |
|---|---|---|
| 1 | Hybrid shell launch (Shell supervises Runtime) | `Thaddeus.Shell` |
| 2 | Local loopback workspace hosting (127.0.0.1, per-launch token) | `Thaddeus.Runtime` |
| 3 | React workspace UI in the embedded webview | `web/` |
| 4 | Threaded chat with streaming responses | `web/`, `Thaddeus.Runtime/Chat` |
| 5 | Local-model / OpenAI-compatible model configuration (LM Studio, Ollama, OpenAI, custom) | Settings → Models |
| 6 | MCP tool boundary (every tool call brokered through the runtime) | `apps/mcp-server`, `packages/mcp-*` |
| 7 | Permission prompts (Deny / Once / Session / Always) and persisted policy | Permission modal |
| 8 | Tool-activity visibility (pills on the message that triggered them) | Chat UI |
| 9 | Activity feed and diagnostics page | `/activity`, `/diagnostics` |
| 10 | Wiki/canvas CRUD, revisions, import/export, search | `/wiki` |
| 11 | Wiki assistant actions: page chat, draft, selected-text rewrite | `/wiki` |
| 12 | Manual routines (user-invoked checklists) and run history | `/routines` |
| 13 | Stop-all and kill controls (header) | `RuntimeStopAllService` |
| 14 | File / document tools, gated by permissions and allowlisted roots | `mcp-tools-core` |

Each feature has a row in [`docs/RELEASE_CHECKLIST.md`](docs/RELEASE_CHECKLIST.md).

---

## Beta features (present, not promoted)

These work but are not the headline. They depend on local sidecars, OS
integrations, or hardware that we cannot guarantee on every user's machine.
Mention them in passing; do not lead with them.

- **Voice / ASR / TTS** (Whisper.cpp, KokoroSharp, Piper). Works on Windows
  with the bundled VoiceHost; macOS/Linux paths exist but are unverified.
- **Push-to-talk** global hotkey.
- **Tray integration** (right-click menu, minimize-to-tray).
- **Global shortcuts** (Stop-all hotkey).
- **Compact panel** (`/compact`). Phase-2 stub today.
- **Windows desktop observation hooks** (UI Automation, screen reader).
- **Clipboard / screen capture tools** (Windows-only, gated by permissions).

If something on this list is broken on a user's machine, v1 is still shipped:
the core experience does not regress.

---

## Deferred (explicitly not in v1)

Stating these explicitly so contributors stop asking.

- **Scheduled / unattended automations.** Removed in 0.3.0. Do not re-add.
- **User profile / personality admin in the v2 workspace.** The runtime API
  does not expose `/api/profile` or `/api/personalities`; the headless
  terminal still has them. v1 ships without admin UI for either.
- **Polished installers** (MSIX, signed `.app` bundle, AppImage).
- **Auto-update channel.**
- **Cross-platform desktop UX parity.** The single-file binary runs on
  macOS/Linux from a terminal; the polished Shell UX is Windows-first.
- **Advanced audit-search / admin pane.** Activity page is read-only; no
  filter/search beyond what the page already shows.

---

## Non-goals

Things v1 will never claim, regardless of how much they technically work:

- Cloud sync, account, or multi-device state.
- Telemetry of any kind, including anonymized.
- Production-grade security beyond local-first, loopback, per-launch token,
  permission gates, and audit logs. We are not a hardened multi-tenant
  service.
- Replacing your judgment. The agent proposes; you approve; you stop.
- Unbounded autonomous agents that run freely on your machine.
- Mobile / tablet / web-hosted versions.

---

## Release-readiness checklist (gate)

v1 ships when **all** of the following are true:

- [ ] `dotnet build SirThaddeus.sln` is green at Release.
- [ ] `dev/test.ps1 -Configuration Release` is green.
- [ ] `cd web && npm install && npm run build` is green.
- [ ] `cd web && npm run typecheck` is green.
- [ ] Every Core v1 row above passes the corresponding step in
      [`docs/RELEASE_CHECKLIST.md`](docs/RELEASE_CHECKLIST.md).
- [ ] [`docs/DEMO_SCRIPT.md`](docs/DEMO_SCRIPT.md) runs end-to-end without
      manual recovery.
- [ ] `README.md` lists Core v1 features and labels Beta/Deferred items
      clearly.
- [ ] `docs/KNOWN_LIMITATIONS.md` matches reality on a fresh Windows install.
- [ ] No code path advertises a Deferred feature as available.

If any row fails, v1 does not ship — feature work to "fix" the row is the
only acceptable path. Pulling rows off this list is not.
