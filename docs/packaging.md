# Sir Thaddeus — Packaging notes (v2 hybrid runtime)

This file describes how to produce distributable builds of the Phase 8 hybrid
runtime, and the known gaps that still require platform-specific work.

## What ships

The v2 hybrid shell consists of a single self-contained binary,
`Thaddeus.Runtime`, which:

- Hosts the React web UI (embedded under `wwwroot/`).
- Exposes the local REST + WebSocket API on `127.0.0.1:<random-port>`.
- Persists chat threads, memos, automations, and settings to
  `~/.thaddeus/` (or the lock-file directory when overridden).

There is no separate UI process. Users open the printed URL in any modern
browser, or — eventually — a thin Tauri/Photino wrapper.

## Building a self-contained binary

```pwsh
# 1) Build the web bundle and sync to wwwroot.
cd web
npm install
npm run build
Copy-Item dist/index.html ../src/Thaddeus.Runtime/wwwroot/index.html -Force
Copy-Item dist/assets/* ../src/Thaddeus.Runtime/wwwroot/assets/ -Recurse -Force
cd ..

# 2) Publish the runtime for one or more RIDs.
pwsh dev/package-runtime.ps1 -Rids win-x64,osx-arm64,linux-x64
```

Outputs land at `artifacts/publish/<rid>/Thaddeus.Runtime[.exe]` as a single
file with everything embedded (managed assemblies, native dependencies, web
bundle).

## Known gaps

These are deferred from Phase 8.2 because they require platform tooling and/or
signing certificates that this branch cannot acquire:

| Gap | Notes |
|---|---|
| **Windows MSIX** | The single-file `.exe` runs anywhere, but a polished install needs an MSIX manifest, a code-signing cert, and either the MSIX Packaging Tool or `MakeAppx.exe`. |
| **macOS `.app` bundle** | The single-file binary works from a terminal but the Mac UX expects an `.app` bundle plus a Developer ID certificate for notarisation. |
| **Linux desktop integration** | A `.desktop` file and an icon need to be produced; AppImage is a likely target. |
| **Auto-update channel** | No update server, no signing, no code-update flow. Defer to a separate phase. |

These do not block the v1 release: the single-file binary is a complete,
runnable artifact on every supported platform. Power users on day 1 launch it
from a terminal; a polished installer is a Phase 9+ concern.
