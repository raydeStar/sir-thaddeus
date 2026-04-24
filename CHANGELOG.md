# Changelog

## 0.3.0 — 2026-04-24

First versioned release. Previous builds reported `0.0.0-dev`; from here
on, `Assembly.GetName().Version` and `package.json` report a real SemVer
and the binary identifies itself in logs, diagnostics, and the UI
runtime-version badge.

### Routines replaces Automations

- **Rip-out**: the Phase 7.2 Automations feature is gone (AutomationRunner,
  AutomationScheduler, ScheduleMath, JsonFileAutomationStore,
  AutomationsApi, the propose_automation virtual tool + its interceptor,
  the per-thread tool allowlist on `ToolPermissionGate`, the AutomationRun
  activity kind, and all web-side automation routes). Scheduled background
  agent execution was drifting away from the "local-first, permissioned,
  visible" trust model — pulling it out lets us ship v1 without a feature
  we'd have had to either finish or keep apologizing for.
- **Routines** is the replacement: user-invoked repeatable checklists
  (Morning Launch, Evening Shutdown, Fitness Check-In, Project Focus,
  Weekly Review — seeded on first boot, idempotent across reboots, never
  overwritten by the seeder after a user edits them). New shared-types
  records, file-backed store with cascade delete and sealed-run
  immutability, REST surface for CRUD + run lifecycle, web routes for
  list/run/edit/history, and audit events for every lifecycle transition.
  No background fire. No silent side effects. A meta-test asserts no
  new `IHostedService` gets added under `Thaddeus.Runtime.Routines` so
  the "no scheduler" property is enforced in the test suite.

### Chat

- **Structured source cards** alongside assistant replies. The agent
  already emitted a `SOURCES_JSON` block when `web_search` fired; the new
  `SourceCardExtractor` parses, dedupes, and orders it into an
  `AgentSource` list, flowed through `AgentResponse.Sources` and
  `ChatMessageSource` to the web store. `SourceCards.tsx` renders
  featured + standard cards with favicons, thumbnails, and excerpts.
  Smoke test covers the end-to-end flow.

### Agent behavior

- **Weather intent routing** forces the first tool-loop round into
  `weather_geocode` → `weather_forecast` when the prompt is clearly a
  weather question, instead of falling through to `web_search` and
  getting worse answers from search snippets.
- **Imperative tool requests** ("use web_search", "try file_read") now
  set `tool_choice` to the named tool so small local models can't
  fabricate "I can't do that" errors when the user explicitly asked for
  a tool that exists.
- **`WeatherTools`** accepts both `location`/`place` aliases for
  geocode and both flat and nested coordinate shapes for forecast —
  small models emit either depending on context.
- **Offline-path wording** leads with the best-effort answer (training
  data, time utilities) and adds a freshness caveat, instead of
  refusing outright. `DeterministicUtilityEngine` now answers
  "what time is it" locally with "The current local time is …" so
  downstream matchers pick up the keyword.
- **`ToolCallRedactor.SummarizeToolListCapabilities`** compacts verbose
  tool-manifest responses into per-category summaries instead of
  truncating mid-tool.

### Settings

- **Clear recovered safe mode**: once SafeMode tripped from
  `settings_json_corrupt` or `unsupported_settings_schema_v*`, the flag
  stayed true forever. Now the first successful load against a
  supported schema clears it and persists the cleared state.
- **Fix `FilesSettings` equality**: default record equality was
  reference-comparing `AllowedRoots`, so a JSON round-trip (which
  deserializes into `List<string>`) never matched a fresh `Defaults()`
  (which produces `string[]`). Three latent round-trip tests had been
  red because of this.

### Shell

- **Butler voice** on tray menu and windows ("At your service, sir",
  "Stand down", "Dismiss", "At the ready") instead of generic verbs.
  Tray loads a custom icon with a P/Invoke fallback to the app icon.

### Build / dev / release

- **Repository versioning**: `Directory.Build.props` VersionPrefix
  bumped to `0.3.0`. `web/package.json` and
  `packages/shared-types/ts/package.json` aligned. Local dev builds
  report `0.3.0-dev`; CI release tags still override via `-p:Version`.
- **`tests/shell/Thaddeus.Shell.Tests.csproj` builds again**: resolved
  NU1605 (bumped `Microsoft.Extensions.Logging.Abstractions` to 10.0.3
  to match the transitive graph, dropped the unused direct
  `Microsoft.Extensions.Logging` ref), added
  `InternalsVisibleTo("Thaddeus.Shell.Tests")` on `Thaddeus.Shell.csproj`
  so the tests can see `ShellSessionController.OpenWorkspaceMenuId` /
  `StopAllMenuId`, and added a missing `using Xunit;` that the dead
  build had been hiding. Shell now contributes **41 tests** to the green
  suite (was 0).
- **`localrunner.ps1`** pre-builds both Shell and Runtime so the
  supervisor's `--no-build` spawn path is honest, and adds Shell to the
  kill-list.
- **Playwright global-setup** pre-builds the SPA and Release runtime so
  `wwwroot/` bundles match the embedded binary at test time.
- **Build artifacts gitignored**: `src/Thaddeus.Runtime/wwwroot/assets/`
  and `wwwroot/index.html` (regenerated by hybrid-shell auto-sync on
  every `dotnet build`, with hash-suffixed filenames that churned with
  every change), plus `.playwright-mcp/`, `test-results/`, and loose
  repo-root `*.png` screenshots.

### Verified

- `dotnet build SirThaddeus.sln` — 0 errors, 0 warnings.
- Full test suite — **2,345 pass, 0 fail, 0 skip**
  (2,190 main + 114 runtime + 41 shell).

## 2026-04-19 — Production Coherence (observability, diagnostics, permission proofs)

This release is connective-tissue work: the features were already here,
but the pieces that prove they actually work — logs, startup checks,
permission-model tests, version-in-binary — were missing or scattered.

### Observability

- **New `SirThaddeus.Logging` package** (Serilog as an
  `Microsoft.Extensions.Logging` provider). Every component now writes
  rolling daily JSON log files under
  `%LocalAppData%\SirThaddeus\logs\{component}\` plus a human console
  sink. The `mcp-server` component routes its console output to stderr
  because stdout is the MCP stdio transport.
- **All four entry points wired** — `headless-runtime`, `voice-host`,
  `mcp-server`, and `ui-avalonia` all bootstrap through the shared
  module. Environment override: `SIRTHADDEUS_LOG_LEVEL`.
- **Silent `catch { }` blocks replaced with logged catches** across
  `SearxngProvider`, `ContentExtractor`, `UiaScreenReader` (five sites),
  `VoiceSessionOrchestrator`, `LocalTextToSpeechPlaybackService`,
  `VoiceBackendSupervisor`, `FrontmatterParser`, and
  `AgentOrchestrator.ContinuityAndUtility`. Each preserves its existing
  fallback behavior but now emits a Debug/Warning log line with the
  originating exception so tail-the-logs triage actually works.
- **`VoiceHost` ad-hoc `Console.Error.WriteLine` calls** in the piper
  download proxy were replaced with structured `ILogger` calls via
  `ILoggerFactory` injection on the endpoint lambda.
- **`docs/observability.md`** documents paths, format, levels, triage
  snippets, and the "no silent catch" policy.

### Startup diagnostics

- **New `SirThaddeus.StartupDiagnostics` module** with three advisory,
  non-blocking checks:
  - `llm.reachable` — probes `{baseUrl}/v1/models` with a 2s timeout;
    any HTTP response proves something is listening. Empty baseUrl →
    Skipped.
  - `voicehost.reachable` — probes the VoiceHost `/health` endpoint;
    Skipped when `VoiceHostEnabled=false`, Warning (not Failed) when
    the port is closed, because VoiceHost is typically launched on
    demand.
  - `logs.writable` — verifies `%LocalAppData%\SirThaddeus\logs\` is
    writable, because an unwritable path is a common "the app is
    silent" root cause.
- **Wired into `headless-runtime` and `ui-avalonia`** so Ok / Warning /
  Failed results show up in the startup log before the chat window
  opens or the first prompt arrives. Diagnostics failures are caught
  and downgraded so they never block startup.

### Permission-model proof

- **Six integration tests** in
  `tests/SirThaddeus.Tests/Agent/Policy/PermissionEnforcementIntegrationTests.cs`
  that exercise the real enforcement paths in
  `AuditedMcpToolClient` and `EnforcingToolRunner`:
  - gate denial blocks tool execution and audits the reason,
  - gate grant allows tool execution,
  - a broker-issuing gate's token id flows into the audit trail,
  - a token that has expired mid-loop is rejected before the tool
    runs,
  - `RevokeAll` (the STOP-ALL path) invalidates active tokens and
    emits a `PERMISSION_REVOKE_ALL` audit event,
  - missing-token calls are rejected without touching the broker.

### Release hygiene

- **Root `Directory.Build.props`** gives local dev builds a SemVer of
  `0.0.0-dev` so `Assembly.GetName().Version` reports an obviously
  non-release value; `dotnet publish -p:Version=1.2.3` overrides it.
- **`dev/release-package.ps1` and `dev/package-cross.ps1`** now parse
  the incoming tag (handling `refs/tags/vX.Y.Z`), strip the leading
  `v`, and thread the version into every `dotnet publish` call, so a
  v1.2.3 release actually ships binaries with `FileVersion 1.2.3.0`
  and `InformationalVersion "1.2.3+<commit>"`.

### Documentation / honesty pass

- **README** no longer claims Windows/macOS/Linux parity. Windows is
  named as the full-experience platform; the cross-platform headless
  runtime and MCP toolkit are called out separately. `Push-to-talk`
  and `UIAutomation screen reading` feature bullets are tagged
  *(Windows only)*.

### Verified

- `dotnet build SirThaddeus.sln` — 0 errors, 5 pre-existing warnings.
- Full test suite — **2,190 pass, 0 fail, 0 skip** (2,145 main + 45
  Windows). Added tests: 4 startup-diagnostics unit tests, 6 permission
  integration tests.

## 2026-03-16 — Avalonia Runtime + Production Hardening

### Highlights

- **Avalonia desktop runtime promoted** as the primary UI path, and legacy desktop-runtime code removed from the repository.
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
