# Roadmap

This roadmap keeps v1 focused. It does not turn beta or deferred features into v1 commitments.

> **Discipline:** moving an item up a milestone requires deleting something else from that milestone, not adding to it.

## v1.0 Power-User Release

Goal: present Sir Thaddeus publicly as a credible local-first AI workspace for controlled agentic workflows.

- Lock scope in [V1_SCOPE.md](archive/V1_SCOPE.md).
- Keep the hybrid shell/runtime/workspace as the only public product surface.
- Validate chat, model settings, MCP permissioning, tool activity, wiki/canvas, manual routines, diagnostics, and stop controls.
- Keep voice, push-to-talk, tray, global shortcuts, compact panel, clipboard/screen tools, and Windows desktop observation hooks clearly labeled beta.
- Keep scheduled automations, profile/personality admin, polished installers, auto-update, cross-platform desktop UX parity, and advanced audit-search/admin panes deferred.
- Ship clear docs, demo script, known limitations, and release checklist.

## v1.1 Polish And Ergonomics

Goal: improve confidence and daily usability without changing the core architecture.

- Tighten first-run setup and model endpoint guidance.
- Improve permission policy review and reset ergonomics.
- Polish the compact panel if it remains in scope after v1 validation.
- Strengthen activity/diagnostics readability.
- Improve package smoke testing and release artifact consistency.
- Add or improve screenshots and a recorded demo GIF.
- Expand live Windows validation for tray, global shortcuts, push-to-talk, and desktop observation hooks.
- Enforce Settings → Advanced → Limits in the runtime (saved today, not yet enforced).
- Wire `/api/profile` into the v2 hybrid runtime; add a minimal display-name / about-me UI.
- Generate and pin the screen-observe harness fixture suite.
- Fix documentation drift as code moves.

## v2.0 Broader Distribution

Goal: move from power-user release to broader installation and platform confidence.

- Signed Windows installer or MSIX.
- macOS app bundle and notarization path if macOS desktop support is still desired.
- Linux desktop packaging if Linux desktop support is still desired.
- Auto-update design and signing strategy.
- Cross-platform desktop UX parity plan.
- More complete admin surfaces for audit review and policy management.
- Personality administration UI in the workspace, with import/export.
- Hardening pass: written threat model, attack-surface review, fuzzing on the loopback API.
- Revisit scheduled automations only after v1 manual routines and permission/audit workflows have proven stable.

## Things That Are Explicitly **Never**

These are not "later" — they are out, regardless of demand:

- **Telemetry**, even anonymized.
- **Cloud account**, even optional.
- **Background autonomous agents** that fire without a user gesture.
- **Bypass paths** for the permission gate.

## How To Propose A Change

Open an issue tagged `roadmap`. State:

1. Which milestone you're targeting.
2. Which item from that milestone you're willing to **remove** to make room.
3. Why the swap improves the v1 promise (or earns the right to expand it).

PRs that move items between milestones without an issue first will be asked to file the issue.
