# Sprint summary — 2026-04-19 (overnight run)

A morning brief for Mark. Scannable top-to-bottom. Grep for `[!]`
if you only have two minutes.

## TL;DR

- Two branches landed during this sprint:
  - **`task/production-coherence`** — merged via PR #155 before you slept.
  - **`task/production-coherence-pt2`** — sitting unmerged on the remote.
    Five commits. Not pushed automatically; you'll want to push it when
    you're up.
- Every commit was built and tested green before moving to the next.
- End-of-sprint totals: `dotnet build SirThaddeus.sln` clean,
  **2,190 tests pass / 0 fail / 0 skip** (2,145 main + 45 Windows).

## [!] What you are most likely to notice when you run it

1. **Logs now actually exist.** Every component writes rolling daily
   JSON logs to `%LocalAppData%\SirThaddeus\logs\{component}\`. If a
   test session seems silent, check there first. PowerShell snippet
   in `docs/observability.md`.
2. **Startup diagnostics run at launch.** If LM Studio isn't running,
   you'll see an **Error** log line at launch:
   `[startup] llm.reachable: failed — LLM endpoint at http://localhost:1234 unreachable: …`.
   Same for VoiceHost. They are advisory — startup still proceeds.
3. **The UI startup is slightly slower when something is broken.** Up
   to ~2s while the reachability probe times out. In the happy path
   (LM Studio up) it's effectively free.
4. **Assembly version now matches the tag.** Release binaries at tag
   `v1.2.3` now report `FileVersion 1.2.3.0` and
   `InformationalVersion "1.2.3+<commit>"`. Dev builds report
   `0.0.0-dev+<commit>` so you can tell a shipped binary from a
   half-baked one at a glance.

## What landed, by concern

### Observability (pt1 + pt2)

- New `packages/logging/SirThaddeus.Logging/` — Serilog wrapped as an
  MEL provider. Single `LoggingBootstrap.UseSirThaddeusLogging(...)`
  entry point for `IHostApplicationBuilder` components; a
  `BuildSerilogLogger(...)` variant for apps without a host (UI).
- All 4 entry points wired: headless-runtime, voice-host, mcp-server,
  ui-avalonia.
- mcp-server routes its console output to **stderr only** because
  stdout is the MCP stdio transport.
- 10 silent `catch { }` sites converted to logged catches. Severity
  chosen per case: Debug for "intentionally tolerated" paths, Warning
  for "user-visible degradation", Error/Fatal for actual failures.
  Files touched:
  - packages: SearxngProvider, ContentExtractor, UiaScreenReader (5
    sites), VoiceSessionOrchestrator, FrontmatterParser,
    AgentOrchestrator.ContinuityAndUtility
  - apps: VoiceBackendSupervisor, LocalTextToSpeechPlaybackService,
    VoiceHost piper download proxy (converted from
    `Console.Error.WriteLine` to `ILogger`)
- `docs/observability.md` — conventions, triage snippets, no-silent-catch
  policy.

### Startup diagnostics (pt1 + pt2)

- New `packages/startup-diagnostics/SirThaddeus.StartupDiagnostics/`.
  Advisory checks; never blocks startup.
- Three checks: `llm.reachable`, `voicehost.reachable`,
  `logs.writable`.
- VoiceHost check returns **Warning** (not Failed) when the port is
  closed, because VoiceHost is usually launched on demand — you don't
  want a scary Error log line for a normal cold start.
- Wired into headless-runtime and ui-avalonia.
- 6 unit tests covering skip/ok/warning/fail paths.

### Permission-model integration tests (pt1)

- `tests/SirThaddeus.Tests/Agent/Policy/PermissionEnforcementIntegrationTests.cs`
- 6 tests, each named for the audit gap it closes:
  - gate denial blocks `AuditedMcpToolClient` from invoking the inner
    tool, audits "blocked"
  - gate grant lets it through
  - a broker-issuing gate carries the token id into the audit log
  - an expired token is rejected by `EnforcingToolRunner` before the
    tool can run
  - `RevokeAll` (the STOP-ALL path) invalidates active tokens and
    writes a `PERMISSION_REVOKE_ALL` event
  - missing-token calls are rejected without touching the broker
- These test the brand promises from the README. If one regresses,
  the README is lying.

### Release hygiene (pt1)

- Root `Directory.Build.props` — default `VersionPrefix=0.0.0`,
  `VersionSuffix=dev`. Overridable by `-p:Version=`.
- `dev/release-package.ps1` and `dev/package-cross.ps1` grew a
  `Get-AssemblyVersion` helper and thread `-p:Version=` into every
  `dotnet publish`. Normalizes `refs/tags/v1.2.3` → `1.2.3`.

### Documentation / honesty (pt1)

- README top line no longer claims tri-platform parity. Windows is
  named as the full-experience platform; macOS/Linux scoped to the
  cross-platform headless runtime and MCP toolkit. Push-to-talk and
  UIA screen reading feature bullets tagged *(Windows only)*.

## [!] What I deliberately did NOT do

- **AgentOrchestrator partial-class refactor.** The audit called this
  out but it's the biggest piece of surgery in the punch list and
  risks regressions across the whole agent. I chose not to start it
  overnight without you watching. A good follow-up branch.
- **A new AgentOrchestrator integration test** (beyond the ones in
  `tests/SirThaddeus.Tests/OrchestratorDecompositionIntegrationTests.cs`
  that already exist). That file plus the 3,478-line
  `AgentOrchestratorTests.cs` already cover the surface reasonably;
  adding a third file felt like duplication.
- **Wiring StartupDiagnostics into voice-host / mcp-server.** They're
  backend sidecars — self-checking their own LLM reachability doesn't
  help an operator. Skipped intentionally.
- **Code signing.** `project-notes/code-signing.md` still says
  unsigned. Not a one-overnight task.
- **An update mechanism.** Users still have to re-download the ZIP.

## How to test yourself

```powershell
# 1. Full build
dotnet build .\SirThaddeus.sln

# 2. Full tests
dotnet test .\SirThaddeus.sln --no-build

# 3. See logs appear
#    Launch the UI or headless runtime, then:
Get-ChildItem "$env:LOCALAPPDATA\SirThaddeus\logs\*\*.log" |
  Sort-Object LastWriteTime -Descending | Select-Object -First 3

# 4. See startup diagnostics fire (with LM Studio closed)
#    Look for "[startup] llm.reachable: failed" in the log

# 5. Verify version wiring
dotnet publish .\apps\voice-host\SirThaddeus.VoiceHost\SirThaddeus.VoiceHost.csproj `
  -c Release -p:Version=1.2.3
#    Then inspect the published dll's FileVersion — should be 1.2.3.0
```

## If something's broken, where to look

| Symptom | First place to check |
| --- | --- |
| UI doesn't start | `%LocalAppData%\SirThaddeus\logs\ui-avalonia\` — Fatal log line |
| Tools silently fail | `%LocalAppData%\SirThaddeus\audit.jsonl` still works, plus `logs\headless-runtime\` now captures more |
| `dotnet test` red | `PermissionEnforcementIntegrationTests.cs` or `StartupDiagnosticsTests.cs` — these are new; the rest is pre-existing |
| Build red | Almost certainly a NuGet restore issue; `dotnet restore --force` |
| Startup hangs ~2s | Expected when LM Studio is closed — diagnostics probe timing out |

## Branch state

```
master
  └── 8f0cd6c (HEAD at push time) Merge PR #155: task/production-coherence
       └── task/production-coherence-pt2 (local + remote)
            11041a7 feat(diagnostics): VoiceHost reachability check
            c2d4cb4 feat(ui-avalonia): run startup diagnostics
            9b848af fix(voice-host): Console.Error.WriteLine -> ILogger
            edb4158 fix(packages): second pass silent catches
            c6c9978 docs(changelog): add 2026-04-19 entry
            (this file lives in this branch too)
```

Open a PR for `task/production-coherence-pt2` when you're ready. The
branch is behind master by the merge commit only; it'll fast-forward-merge
cleanly.
