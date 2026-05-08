# Sir Thaddeus — Roadmap

This roadmap reinforces [`V1_SCOPE.md`](../V1_SCOPE.md). Nothing here lets v1
scope drift; everything is either in v1.0 (the current cut), v1.1 (polish on
the same surface), or v2.0 (broader distribution). If a feature isn't on the
list, we are not building it on speculation.

> **Discipline:** moving an item up a milestone requires deleting something
> else from that milestone, not adding to it.

---

## v1.0 — Power-user release *(current cut)*

Ship the v2 hybrid surface as a credible v1.0 for power users on Windows,
with the runtime running honestly on macOS / Linux.

In scope (must work, see [`V1_SCOPE.md`](../V1_SCOPE.md) for the full list):

- Hybrid Shell + Runtime launch (Windows-primary).
- Loopback workspace, per-launch token, no telemetry.
- Threaded streaming chat, source cards, tool-activity pills.
- Local / OpenAI-compatible model configuration with provider presets.
- MCP tool boundary, permission prompts (Deny / Once / Session / Always),
  audit log on disk.
- Wiki / canvas: CRUD, revisions, import/export, search, page chat, draft,
  selected-text rewrite.
- Manual routines with run history. **No background firing.**
- Activity feed, diagnostics page, logs path discoverable.
- Stop-all + kill controls.

Non-blocking but in v1.0 because the work was already done:

- Theme picker (Light / Dark / System).
- Memory (memos): create, edit, pin, delete, Markdown body.
- Routine inline enable/disable + create-from-list.

Beta in v1.0 (works, not the headline):

- Voice ASR/TTS, push-to-talk, tray, global shortcuts, compact panel,
  Windows screen / clipboard tools.

---

## v1.1 — Polish and ergonomics

The next minor release. Goal: reduce the gap between "core works" and
"feels finished" without expanding the surface.

Targets:

- **Enforce Settings → Advanced → Limits.** They're saved today; wire them
  into `ToolPermissionGate` and the agent loop. Add tests.
- **Compact panel beyond stub.** Implement transcript stream + PTT in the
  small surface. Keep it small — don't grow it into a second workspace.
- **Permission audit pane.** Read-only filter / search over
  `audit.jsonl`, surfaced in the Activity page. No new write paths.
- **Voice on macOS / Linux** as a documented Beta path: package the
  VoiceHost cross-platform, document the setup, leave it Beta.
- **Profile in the workspace** — minimal: display name, alias, about-me,
  persisted to settings. Wire `/api/profile` into `Thaddeus.Runtime`. No
  personality admin yet.
- **Memo full-text search** on the Memory page. The store already has the
  data; just expose it.
- **Routines: pre-set templates editor.** Today the seeded templates are
  immutable until the user touches them; let users reset to defaults
  explicitly.
- **Diagnostics: copy-to-clipboard for support bundles.** One button → zips
  recent logs + settings (with secrets masked) → clipboard.

Out of scope for v1.1:

- Scheduled automations. Still no.
- Polished installer. Defer to v2.0.
- Personality admin UI. Defer to v2.0.
- Mobile / web-hosted UI.

---

## v2.0 — Broader distribution

The version where Sir Thaddeus stops being a "build it from source / unzip
the binary" project and becomes something a non-power-user can install.
This is the milestone where we earn the right to claim "production-grade
ergonomics."

Targets:

- **Polished installers.**
  - Windows MSIX with code signing.
  - macOS `.app` bundle with notarization.
  - Linux AppImage / Flatpak with `.desktop` integration.
- **Auto-update channel.** Update server, signed payloads, in-app update
  flow with explicit user consent.
- **Cross-platform desktop UX parity.** Tray, global hotkeys, embedded
  webview wrapper on macOS and Linux.
- **Personality admin in the workspace.** Load / create / import / export.
- **Hardening pass.** Threat model written down, attack surface review,
  fuzzing on the loopback API.
- **First-class observability.** Structured event stream, optional
  redacted bug-report bundle, log-rotation defaults audited.

Out of scope for v2.0:

- Cloud sync. Still not a goal.
- Multi-user / team mode. Different product.
- Hosted / SaaS deployment. Different product.

---

## Things that are explicitly **never**

These are not "later" — they are out:

- **Telemetry**, even anonymized.
- **Cloud account**, even optional.
- **Background autonomous agents** that fire without a user gesture.
- **Bypass paths** for the permission gate. If it acts, you saw it.

---

## How to propose a change to this roadmap

Open an issue tagged `roadmap`. State:

1. Which milestone you're targeting.
2. Which item from that milestone you're willing to **remove** to make room.
3. Why the swap improves the v1 promise (or earns the right to expand it).

PRs that move items between milestones without an issue first will be
asked to file the issue.
