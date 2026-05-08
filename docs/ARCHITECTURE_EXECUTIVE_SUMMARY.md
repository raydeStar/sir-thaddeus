# Architecture Executive Summary

This is the short version of the Sir Thaddeus architecture. It is meant for quick review, release planning, or handing the project to another AI before drilling into the full detail in [ARCHITECTURE.md](ARCHITECTURE.md).

For completion status by subsystem, use [FEATURE_GAP_MATRIX.md](FEATURE_GAP_MATRIX.md).

## What Sir Thaddeus Is

Sir Thaddeus is a local-first assistant workspace made of five practical parts:

- a desktop shell that launches and supervises the app,
- a loopback runtime that hosts the API and workspace,
- a React workspace UI,
- an assistant pipeline that can call tools through MCP,
- and a beta voice sidecar for ASR and TTS.

The current v1 product surface is the hybrid runtime in [src/Thaddeus.Runtime/](../src/Thaddeus.Runtime/), the shell in [src/Thaddeus.Shell/](../src/Thaddeus.Shell/), and the workspace in [web/](../web/). The old terminal runtime in [apps/headless-runtime/](../apps/headless-runtime/) still exists, but it is now mainly a transitional and harness path rather than the main product UI.

## Current Shape

| Topic | Summary |
| --- | --- |
| Main entry point | The shell starts or attaches to the runtime, opens the workspace, and shuts things down cleanly when the session ends. |
| User experience | The active UI is browser-based, but it is hosted locally by the runtime and launched like a desktop app. |
| Assistant core | Chat flows through `AssistantRouter`, which chooses a stub assistant or the `LmStudioAssistant` pipeline based on settings and endpoint health. |
| Tools | Tool calls cross a stdio MCP boundary into a manifest-driven tool server rather than running directly in the UI or chat layer. |
| Voice | Voice is handled by a separate local VoiceHost process that the runtime probes, starts, and proxies; it is beta for v1. |
| Storage | Threads, memos, routines, settings, logs, audit data, and wiki content are stored locally. |

## What Is Solid Today

- The hybrid runtime, loopback API, and workspace route structure are in place and form the real v1 product surface.
- Chat is not just a thin text box. It has threads, streaming deltas, retry, source rendering, tool activity, and a live permission path.
- MCP integration is a real subsystem, with manifest metadata, tool grouping, permission gating, and restart-on-settings-change behavior.
- Wiki support is broad: it includes roots, folders, pages, revisions, import/export, search, and page-specific assistant actions.
- Routines are real and usable as a manual accountability workflow.
- The shell contains tray, shortcut, compact-panel, and stop-all plumbing, but tray, shortcuts, and compact mode stay beta for v1.

## Main Caveats

- The compact panel exists, but it is still a minimal idle surface rather than a full quick-interaction experience.
- Voice is architecturally present, but end-to-end behavior still depends on local sidecars, models, and machine setup.
- Windows has the richest desktop implementation. Cross-platform runtime support is ahead of cross-platform desktop UX parity.
- Scheduled or unattended automations are not part of the current routines surface. Routines are manual by design right now.
- Profiles and personality administration are not part of the active workspace surface.
- The legacy headless runtime is still needed for harness-related work, so the repo currently contains both the active v2 surface and the older path.
- Packaging works at the single-file runtime level, but installer polish and auto-update remain deferred.

## Best Review Questions

1. Is the hybrid shell the only surface you want to count as the product, or do you still need legacy headless parity before calling the app complete?
2. Are chat, permissions, wiki, routines, and diagnostics sufficient for the intended release without profile admin or scheduled automations?
3. Are the Windows-first beta surfaces acceptable as beta, or does the release require stronger live validation before they are shown publicly?
4. Are the packaging gaps acceptable for a power-user release, or do you need signed installers and update flow before calling it done?

## Recommended Input Set For Another AI

If you want another AI to reason about the current system without getting lost in older migration notes, give it the docs in this order:

1. [ARCHITECTURE_EXECUTIVE_SUMMARY.md](ARCHITECTURE_EXECUTIVE_SUMMARY.md)
2. [FEATURE_GAP_MATRIX.md](FEATURE_GAP_MATRIX.md)
3. [ARCHITECTURE.md](ARCHITECTURE.md)
4. [docs/hybrid-shell.md](hybrid-shell.md)
5. [docs/packaging.md](packaging.md)

That sequence gives it the current product shape first, the completion status second, and the full subsystem detail third.
