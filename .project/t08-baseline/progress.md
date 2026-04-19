# Harden the Baseline Profile

- Status: Done
- Branch: `task/harden-baseline-profile`
- Started: 2026-04-01
- Finished: 2026-04-01
- Objective: Ship a real Baseline preset with typed-first first-run defaults, no voice-sidecar autostart, and earlier permission confirmation for filesystem access while preserving existing tray, stream, stop, and audit behavior.

## Completed

- Added an explicit `baseline` product preset marker to settings and moved default TTS, VoiceHost autostart, and SearXNG autostart to baseline-off.
- Raised settings schema to v4 and covered fresh-default creation plus migration preservation of explicit voice/search opt-ins.
- Added audited file-permission preflight so early explicit file actions, `FileTask` routing, and chat fallback-to-file all stop before file tools are exposed or executed.
- Extended Explain lane so screen-observe explain/summarize requests can answer from grounded screen context instead of forcing clarification.

## Validation

- `dotnet build SirThaddeus.sln --no-restore -c Release`: pass, 0 errors.
- `dotnet test SirThaddeus.sln -c Release --no-build`: pass, 2091 passed, 0 failed.
- `./dev/harness.ps1 --suite smoke --judge none`: initial run failed due inherited `runtimeSafety.safeMode=true` and stale Debug runtime locks, then passed 8/8 after clearing stale `SirThaddeus.HeadlessRuntime` processes and rerunning against a sandboxed `ST_SETTINGS_PATH` with safe mode cleared.