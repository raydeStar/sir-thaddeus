# Deployment Guide

This guide defines a repeatable production deployment workflow for the hybrid runtime.

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
.\dev\fetch-assets.ps1
.\dev\build-searxng-package.ps1
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

- publish directories: `.\artifacts\publish\<project>\win-x64\`
- staged package directory: `.\artifacts\stage\win-x64\`
- zipped package: `.\artifacts\release\sir-thaddeus-win-x64-v0.1.0-full.zip`
- zip checksum: `.\artifacts\release\sir-thaddeus-win-x64-v0.1.0-full.zip.sha256.txt`
- package contents checksum manifest: `.\artifacts\release\sir-thaddeus-win-x64-v0.1.0-full-contents.sha256.txt`

### Voice backend assets

Voice backend binaries (~320 MB) are hosted on GitHub Releases (`assets-v1` tag), not in the repo.
CI workflows run `dev\fetch-assets.ps1` automatically before packaging. For local release builds,
run it manually or let the build scripts handle it:

```powershell
.\dev\fetch-assets.ps1
```

End users get these assets automatically during the first-run onboarding wizard.

Packaging smoke validation now includes an offline dependency gate that verifies bundled `uv` + Python + wheelhouse can create a venv and install voice dependencies before release publish.

### Bundled SearXNG sidecar

CI workflows run `dev\build-searxng-package.ps1` automatically before packaging. For local release builds,
prepare or refresh the bundled `search/` payload manually:

```powershell
.\dev\build-searxng-package.ps1
```

Release packaging now fails if a valid bundled SearXNG payload cannot be staged.

### Required ZIP contents

- `Thaddeus.Runtime.exe` (primary app executable)
- `SirThaddeus.McpServer.exe` (MCP sidecar process)
- `SirThaddeus.VoiceHost.exe` (voice sidecar process)
- required runtime DLLs and support files from publish output
- `README_FIRST_RUN.md` (first-run instructions)

### Recommended ZIP contents

- `SirThaddeus.Settings.template.json` (starter settings template)
- matching `.zip.sha256.txt` checksum file distributed beside the ZIP
- matching `-contents.sha256.txt` manifest for all packaged files

## 3) Smoke test checklist

Run from the publish output folder:

```powershell
.\Thaddeus.Runtime.exe
```

Verify:

* [ ] LLM connection status is healthy.
* [ ] MCP tools are discoverable (non-zero tool count).
* [ ] Chat response works for normal prompt and memory-aware personalization prompt.
* [ ] No internal markers appear in user-visible output (e.g. tool/reference markers).
* [ ] No unsupported capability claims appear (e.g. email/send promises when no such tool exists).
* [ ] Audit log continues to append entries in `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`.
* [ ] VoiceHost launches on first voice use (check audit for `VOICEHOST_READY`).
* [ ] VoiceHost health endpoint responds at `http://127.0.0.1:17845/health` with `ready: true`.

Notes:

- Normal user flow is **one-step**: launch `Thaddeus.Runtime.exe` only.
- Do **not** require users to run backend scripts or terminal commands in production.

## 4) Packaging and release handoff

Recommended:

1. Attach the generated `.zip` package and matching `.sha256.txt` file.
2. Include:
    - release notes
    - pinned SDK/runtime notes
    - checksum/hash for the archive
    - package contents checksum manifest
3. Keep the previous known-good package available for rollback.

Tag-based GitHub release flow:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Expected release assets:

- `sir-thaddeus-win-x64-v0.1.0-full.zip`
- `sir-thaddeus-win-x64-v0.1.0-full.zip.sha256.txt`
- `sir-thaddeus-win-x64-v0.1.0-lite.zip`
- `sir-thaddeus-win-x64-v0.1.0-lite.zip.sha256.txt`

Every tagged release now builds full and lite packages for each platform:

- `sir-thaddeus-win-x64-<ver>-full.zip` and `sir-thaddeus-win-x64-<ver>-lite.zip`
- `sir-thaddeus-linux-x64-<ver>-full.tar.gz` and `sir-thaddeus-linux-x64-<ver>-lite.tar.gz`
- `sir-thaddeus-osx-arm64-<ver>-full.tar.gz` and `sir-thaddeus-osx-arm64-<ver>-lite.tar.gz`

Rolling releases now expose the same split by platform:

- `latest-dev` tracks the newest `dev` push and includes Windows/Linux/macOS artifacts
- `latest` tracks the newest `master` push and includes Windows/Linux/macOS artifacts

GitHub also shows automatic `Source code (zip)` / `Source code (tar.gz)` snapshots for each release tag; those are repository archives, not runnable packages.

Use the `skip_macos=true` promotion input for emergency hotfixes to avoid macOS CI minute cost.

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
