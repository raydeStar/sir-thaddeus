# Avalonia Parity Checklist (Phase 1)

Date: 2026-03-05

## Routing + orchestration parity

- [ ] Plain Q&A prompt routes without tool use.
- [ ] Web search intent routes into tool loop with policy gate.
- [ ] Logic/analysis prompt routes without unnecessary tool invocation.
- [ ] Multi-step request shows router -> policy -> tool loop progression in logs.

## Permission workflow parity

- [ ] Tool call requiring permission emits `tool.requested`.
- [ ] Approve path emits `tool.approved` and run continues.
- [ ] Deny path emits `tool.denied` and run degrades gracefully.

## STOP/cancel parity

- [ ] Active run can be cancelled via STOP command.
- [ ] Cancellation terminates tool loop quickly and emits `run.failed` with cancellation marker.
- [ ] No further token deltas are emitted after cancellation acknowledgement.

## Audit/log parity

- [ ] Audit stream includes route selection details.
- [ ] Audit stream includes policy decisions.
- [ ] Audit stream includes tool loop iteration count.
- [ ] Audit stream includes per-tool timing.

## Manual smoke sequence

1. Build: `dotnet build SirThaddeus.sln -m:1 -v m`
2. Tests: `dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -m:1 -v m`
3. Harness smoke: `dotnet tools/SirThaddeus.Harness/bin/Debug/net10.0/SirThaddeus.Harness.dll smoke --mode live --max-iters 1 --judge none`
4. Package smoke (optional): `./dev/smoke-test.ps1`
