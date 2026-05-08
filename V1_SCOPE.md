# Sir Thaddeus v1 Scope

Sir Thaddeus v1 is a local-first AI workspace for controlled agentic workflows: chat, MCP-powered tools, explicit permissions, local storage, diagnostics, and durable wiki/canvas knowledge.

This file is the v1 scope lock. If a proposed task expands beyond this page, it belongs after v1 unless it fixes a blocker in the current surface. **Pulling a row off the Core list is not an acceptable path; fixing the row that fails is.**

## Target User

Sir Thaddeus v1 is for technical power users who are comfortable running local tools, configuring an OpenAI-compatible model endpoint, reviewing permission prompts, and validating a local-first workflow before broader distribution polish exists.

## Core v1 Promise

Sir Thaddeus v1 lets a user run a local workspace, chat with a configured model, grant or deny explicit tool permissions, inspect tool activity, and preserve useful output in local wiki/canvas knowledge without pretending the product is a polished consumer installer.

If it acts, you see it. If you press **STOP**, it stops.

## Product Surface That Counts

The v1 product surface is limited to:

- [src/Thaddeus.Shell/](src/Thaddeus.Shell/)
- [src/Thaddeus.Runtime/](src/Thaddeus.Runtime/)
- [web/](web/)
- [apps/mcp-server/](apps/mcp-server/)
- [packages/mcp-shared/](packages/mcp-shared/)
- [packages/mcp-tools-core/](packages/mcp-tools-core/)
- [packages/mcp-tools-windows/](packages/mcp-tools-windows/)
- [packages/wiki/](packages/wiki/)
- supporting runtime/tool/storage packages used by the hybrid surface

The legacy runtime in [apps/headless-runtime/](apps/headless-runtime/) may remain for harness and transitional workflows, but it is not the public v1 product.

## Core v1 Features

Each row must work end-to-end on v1 day one. The corresponding step in
[docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md) is the gate.

- Hybrid shell/runtime launch.
- Local loopback workspace hosting.
- React workspace UI.
- Threaded chat with streaming.
- Local/OpenAI-compatible model configuration.
- MCP tool boundary.
- Permission prompts and persisted permission policy.
- Tool activity visibility.
- Activity feed and diagnostics.
- Wiki/canvas CRUD, revisions, import/export, and search.
- Wiki assistant actions: page chat, draft, and selected-text rewrite.
- Manual routines and run history.
- Stop-all and kill controls.
- File/document tools when stable under permission gating.

## Beta Features

These may remain available, but they must not be marketed as core v1 promises:

- Voice / ASR / TTS.
- Push-to-talk.
- Tray integration.
- Global shortcuts.
- Compact panel.
- Windows desktop observation hooks.
- Clipboard and screen tools.

If a Beta item is broken on a user's machine, v1 still ships: the core experience does not regress.

## Explicitly Deferred

Do not add these to v1:

- Scheduled automations.
- Profile/personality administration in the v2 workspace.
- Polished installers.
- Auto-update.
- Cross-platform desktop UX parity.
- Advanced audit-search/admin pane.

## Non-Goals

- Redesigning the UI.
- Rewriting the agent loop.
- Promoting voice as the headline feature.
- Removing the legacy runtime unless separate tests prove it is dead.
- Claiming production-grade security beyond local-first loopback hosting, per-launch tokens, explicit permissions, visible activity, and local auditability.
- Claiming cross-platform desktop parity.
- Adding unattended background autonomy.
- Telemetry of any kind, including anonymized.
- Cloud sync, account, or multi-device state.

## Release-Readiness Checklist

v1 ships when **all** of the following are true. If any row fails, v1 does not ship — feature work to fix that row is the only acceptable path.

- [ ] Public README describes the hybrid product, not the legacy runtime.
- [ ] Core v1 features are documented as current and validated.
- [ ] Beta features are labeled beta everywhere public-facing.
- [ ] Deferred features are not described as v1 commitments.
- [ ] Demo script can be completed without relying on voice, tray, global shortcuts, or compact mode.
- [ ] Local model setup is documented for LM Studio, Ollama, and custom OpenAI-compatible endpoints.
- [ ] Stub assistant smoke test passes.
- [ ] Local/OpenAI-compatible model smoke test passes, or is explicitly skipped with reason.
- [ ] Permission prompt and tool activity flow is manually validated.
- [ ] Wiki/canvas CRUD, revisions, import/export, search, and assistant actions are validated.
- [ ] Routines are validated as manual workflows only.
- [ ] Stop-all and kill controls are validated.
- [ ] `dotnet build SirThaddeus.sln` is green at Release.
- [ ] `dev/test.ps1 -Configuration Release -SkipScreenObserveHarness` is green.
- [ ] `cd web && npm install && npm run build && npm run typecheck && npm run lint` is green.
- [ ] [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md) walked end-to-end with sign-off.
- [ ] Any skipped GPU, voice, harness, or live integration work is recorded in [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md).
