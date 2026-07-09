# Documentation

Everything beyond the [project README](../README.md), grouped by who needs it.
Trust-model documents ([PRIVACY](../PRIVACY.md), [DISCLAIMER](../DISCLAIMER.md))
and the standard project files ([CONTRIBUTING](../CONTRIBUTING.md),
[SECURITY](../SECURITY.md), [CHANGELOG](../CHANGELOG.md)) live at the repo root.

## For Users

Getting Sir Thaddeus running and understanding what it will and won't do.

- [FIRST_RUN.md](FIRST_RUN.md) — download, unzip, run; the first-run wizard, prerequisites, and local data paths.
- [SETTINGS.md](SETTINGS.md) — every setting, what it controls, and where it is stored.
- [FOLDER_ACCESS.md](FOLDER_ACCESS.md) — how file and folder permissions work.
- [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) — the honest boundaries of the current release.

## For Developers

Building, testing, and understanding the system.

- [ARCHITECTURE_PUBLIC.md](ARCHITECTURE_PUBLIC.md) — the short public overview: shell, runtime, workspace, assistant pipeline, MCP boundary, permission gate, storage.
- [ARCHITECTURE.md](ARCHITECTURE.md) — the full subsystem-by-subsystem architecture, grounded in the current code.
- [ARCHITECTURE_EXECUTIVE_SUMMARY.md](ARCHITECTURE_EXECUTIVE_SUMMARY.md) — the one-page version for quick review or handoff.
- [TESTING.md](TESTING.md) — the fast unit loop, the conversation-level harness, and the benchmark suites.
- [DEPLOYMENT.md](DEPLOYMENT.md) — the release-packaging workflow, preflight gate, and handoff checklist.
- [LOGGING.md](LOGGING.md) — per-turn traces, the audit log, and how to answer "why did the assistant do that?".
- [observability.md](observability.md) — diagnostics and runtime health surfaces.
- [hybrid-shell.md](hybrid-shell.md) — how the shell and loopback runtime cooperate.
- [lm-studio-performance.md](lm-studio-performance.md) — tuning a local LM Studio endpoint.
- [runtime/ipc-contract.md](runtime/ipc-contract.md) — the shell ↔ runtime IPC handshake.
- [packaging.md](packaging.md) and [build/publish.md](build/publish.md) — packaging internals and publish steps.

## Project

Scope, status, and release process.

- [ROADMAP.md](ROADMAP.md) — planned work and where deferred items land.
- [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md) — the v1 release-readiness gate.
- [FEATURE_GAP_MATRIX.md](FEATURE_GAP_MATRIX.md) — completion status by subsystem.
- [FEATURES_QA.md](FEATURES_QA.md) — the manual QA walkthrough of every user-visible feature.
- [DEMO_SCRIPT.md](DEMO_SCRIPT.md) — the demo and recording script.

## Archive

Historical documents, kept for context.

- [archive/V1_SCOPE.md](archive/V1_SCOPE.md) — the v1.0 scope lock.
- [migration/](migration/) — Avalonia parity notes, pipeline migration, and non-transferrable functionality from the earlier terminal runtime.
