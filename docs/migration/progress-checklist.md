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
- [ ] Cross-platform publish scripts

## Docs

- [x] `/docs/migration/avalonia-parity.md`
- [x] `/docs/runtime/ipc-contract.md` (events + endpoints)
- [ ] `/docs/build/publish.md` (how to build for win/linux/mac)

## Validation

- [ ] Smoke script passes
- [ ] 5 regression validations pass
- [x] `dotnet build SirThaddeus.sln -m:1 -v m` passes
- [x] `dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -m:1 -v m` passes

## Notes

- Legacy desktop UI project has been removed from solution default build in this pass; source still exists on disk for controlled retirement.
