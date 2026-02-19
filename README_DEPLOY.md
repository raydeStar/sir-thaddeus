# Deployment Guide

This guide defines a repeatable production deployment workflow for the desktop runtime.

## 1) Preflight gate (required)

Run this before creating any release artifact:

```powershell
.\dev\preflight.ps1
```

What it does:

- validates local environment/restore via `.\dev\bootstrap.ps1`
- runs the full Release test suite via `.\dev\test_all.ps1`

If preflight fails, do not package or distribute.

## 2) Package release artifacts

Use the packaging script to run the preflight gate, publish all runtime binaries,
archive the output, and emit SHA-256 checksums.

```powershell
.\dev\release-package.ps1
```

Release packaging defaults to a **self-contained** build in Release mode.

Useful variants:

```powershell
# Skip preflight only if it was already run in this session
.\dev\release-package.ps1 -SkipPreflight

# Build deterministic versioned file names (recommended for releases)
.\dev\release-package.ps1 -Version v0.1.0
```

Outputs:

- publish directory: `.\artifacts\publish\win-x64\`
- staged package directory: `.\artifacts\stage\win-x64\`
- zipped package: `.\artifacts\release\sir-thaddeus-win-x64-<version-or-timestamp>.zip`
- zip checksum: `.\artifacts\release\sir-thaddeus-win-x64-<version-or-timestamp>.zip.sha256.txt`
- per-binary checksums: `.\artifacts\release\sir-thaddeus-win-x64-<version-or-timestamp>-binaries.sha256.txt`

### Required ZIP contents

- `SirThaddeus.DesktopRuntime.exe` (primary app executable)
- `SirThaddeus.McpServer.exe` (MCP sidecar process)
- `SirThaddeus.VoiceHost.exe` (voice sidecar process)
- required runtime DLLs and support files from publish output
- `README_FIRST_RUN.md` (first-run instructions)

### Recommended ZIP contents

- `SirThaddeus.Settings.template.json` (starter settings template)
- matching `.zip.sha256.txt` checksum file distributed beside the ZIP

## 3) Smoke test checklist

Run from the publish output folder:

```powershell
.\SirThaddeus.DesktopRuntime.exe --headless
```

Verify:

1. LLM connection status is healthy.
2. MCP tools are discoverable (non-zero tool count).
3. Chat response works for:
   - normal prompt
   - memory-aware personalization prompt
4. No internal markers appear in user-visible output (for example, tool/reference markers).
5. No unsupported capability claims appear (for example, email/send promises when no such tool exists).
6. Audit log continues to append entries in `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`.
7. VoiceHost launches on first voice use (check audit for `VOICEHOST_READY`).
8. VoiceHost health endpoint responds at `http://127.0.0.1:17845/health` with `ready: true`.

Notes:

- Normal user flow is **one-step**: launch `SirThaddeus.DesktopRuntime.exe` only.
- Do **not** require users to run backend scripts or terminal commands in production.

## 4) Packaging and release handoff

Recommended:

1. Attach the generated `.zip` package and matching `.sha256.txt` file.
2. Include:
   - release notes
   - pinned SDK/runtime notes
   - checksum/hash for the archive
3. Keep the previous known-good package available for rollback.

Tag-based GitHub release flow:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Expected release assets:

- `sir-thaddeus-win-x64-v0.1.0.zip`
- `sir-thaddeus-win-x64-v0.1.0.zip.sha256.txt`

## Optional code signing

For organizations that require Authenticode-signed binaries, follow:

- `project-notes/code-signing.md`

## 5) Post-deploy checks

After rollout on a clean machine/profile:

- start app, confirm tray + command palette behavior
- confirm memory DB initialization at `%LOCALAPPDATA%\SirThaddeus\memory.db`
- run one end-to-end query and verify tool activity/audit events
- confirm shutdown/restart behavior and settings persistence
- confirm VoiceHost process starts/stops with the runtime (check task manager)
- confirm `%LOCALAPPDATA%\SirThaddeus\voicehost-session.json` is created on first voice use

## 6) Dev troubleshooting only (not normal UX)

`.\dev\start-voice-backend.ps1` is retained for diagnostics and local debugging.

- Use it only when investigating backend startup issues.
- Do not include script execution as part of end-user setup instructions.