# Avalonia Migration Progress

Date: 2026-03-05

## Code deliverables

- [x] .NET 10 upgrade completed (baseline pass), build + tests green
- [x] New `SirThaddeus.UI.Avalonia` project builds and runs
- [x] Old UI removed from default build and/or deleted
- [x] Headless runtime remains functional
- [x] Stable IPC boundary between UI and runtime (HTTP + SSE + contracts package)
- [x] Permissions workflow working end-to-end (request + approve/deny API)
- [x] Streaming + STOP works
- [x] Global STOP ALL tears down local backend servers and exits the Avalonia app
- [ ] Cross-platform publish scripts

## Docs

- [x] `/docs/migration/avalonia-parity.md`
- [x] `/docs/runtime/ipc-contract.md` (events + endpoints)
- [x] `/docs/build/publish.md` (how to build for win/linux/mac)

## Validation

- [x] Smoke script passes
- [x] 5 regression validations pass
- [x] `dotnet build SirThaddeus.sln -m:1 -v m` passes
- [x] `dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -m:1 -v m` passes

## Notes

- Legacy desktop UI project has been removed from solution default build in this pass; source still exists on disk for controlled retirement.
- Smoke validation now passes against a freshly built Avalonia package (`SirThaddeus.UI.Avalonia.exe`) with VoiceHost `/health` responsiveness and UI launch gate.
- Regression gate (5 tests): `CasualChat_NoToolCalls`, `WebLookup_CallsToolThenSummarizes`, `Executor_GrantsPermission_ExecutesTool`, `Executor_DeniesPermission_ReturnsFailure`, `SelfMemoryQuestion_ReturnsStoredFactsSummary`.
- Packaging defaults now use a lite profile (no bundled voice runtime/models/wheels and no `.playwright` runtime payload) to reduce zip size; full offline bundle remains available via `./dev/release-package.ps1 -FullBundle`.
- Avalonia STOP ALL now performs best-effort backend teardown (`HeadlessRuntime`, `McpServer`, `VoiceHost`, `voice-backend`) and then forces app shutdown.
