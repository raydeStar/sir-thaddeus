# Changelog

## 2026-03-16 — Avalonia Runtime + Production Hardening

### Highlights

- **Avalonia desktop runtime promoted** as the primary UI path, with desktop-runtime code moved to `LEGACY/` for controlled deprecation.
- **Headless runtime API modularized** into focused endpoint and helper partials for maintainability and safer future changes.
- **Memory pipeline hardening** completed: retrieval error/timeout resilience, conversation-scoped history wiring, and automatic chat/assistant chunk persistence restored.
- **Routing/Footman authority recalibration** added with typed block reasons and disagreement logging for safer deterministic behavior.

### Production Readiness Notes

- Solution build is green on current branch (`dotnet build SirThaddeus.sln --no-restore`).
- Memory-focused tests are green (conversation scoping and provider argument threading included).
- Documentation and migration notes were expanded under `docs/migration/` and runtime notes.

## 2026-03-04 — Terminal Runtime (optimizations branch)

![Headless Runtime Screenshot](assets/images/headless-shot.png)

### Highlights

- **Headless terminal runtime** — Chat-first CLI entry point with `/help`, `/reset`, `/tools`, `/exit`, profile management, and undo support.
- **Profile-aware prompt** — Reads `preferred_name` from the shared SQLite profile store so the prompt reflects the active identity (e.g., `raydestar <-> sir-thaddeus`).
- **Alias overrides** — Added `alias` field for both user and AI personality profiles to override display names in CLI and JSON.

### Profile Management

- **User profile commands** — `/profile user show`, `set-alias`, `set-display-name`, `set-about-me` for managing identity during a session.
- **Personality profile commands** — `/profile thaddeus show`, `load`, `create`, `set-alias`, `export`, `import` for AI personality configuration.
- **Settings undo** — `/undo` restores the most recent profile or settings change.

### Runtime & Architecture

- **Runtime host extraction** — Introduced shared `RuntimeHost` package for LLM options, MCP environment setup, and path resolution.
- **Audit logging** — `JsonLineAuditLogger` now runs independently for terminal sessions.
- **Terminal launcher** — `dev/terminal.ps1` builds MCP server and launches the headless runtime in a single step.

### Tools

- **MCP tool split** — Core tools (`McpTools.Core`) are cross-platform; Windows-specific tools (`McpTools.Windows`) load conditionally.
- **Tool loop hardening** — `ToolLoopExecutor` adds budget enforcement and improved test coverage.
