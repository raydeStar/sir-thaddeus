# Sir Thaddeus Packaging Notes

This file describes the current distributable packages for the hybrid product
surface: the loopback runtime, embedded web workspace, MCP server, and optional
voice/search sidecars.

## What ships

The release archive is a wrapped folder named
`sir-thaddeus-<rid>-<version>-<profile>` where profile is `full` or `lite`.
The staged package includes:

- `Thaddeus.Runtime[.exe]`, the loopback REST/WebSocket host that also serves
  the built React workspace from `wwwroot/`.
- `SirThaddeus.McpServer[.exe]`, the stdio MCP sidecar.
- The bundled SearXNG search payload when available.
- `SirThaddeus.VoiceHost[.exe]` and heavyweight voice/Playwright payloads in
  full packages.
- First-run documentation, settings template, launcher scripts where relevant,
  and SHA-256 checksums.

The lite package keeps the same runtime and MCP surface but skips heavyweight
bundled voice, Playwright, and SearXNG payloads. On Linux and macOS, lite
packages also exclude `SirThaddeus.VoiceHost`.

Runtime state remains local to the machine, with settings managed by the web UI
at `%USERPROFILE%\.thaddeus\runtime-settings.json` on Windows and equivalent
profile locations on other platforms.

## Building release packages

Windows release packaging is driven by:

```pwsh
.\dev\fetch-assets.ps1
.\dev\build-searxng-package.ps1
.\dev\release-package.ps1 -Version v0.1.0
.\dev\release-package.ps1 -Version v0.1.0 -LiteBundle
```

Full packages require the bundled voice and SearXNG payloads to be present.
Lite packages can be built without them and should be smoke-tested with:

```pwsh
.\dev\smoke-test.ps1 -SkipLaunch -AllowRuntimeAssetDownload
```

Outputs land under `artifacts/release/`, for example:

- `sir-thaddeus-win-x64-v0.1.0-full.zip`
- `sir-thaddeus-win-x64-v0.1.0-lite.zip`
- matching `.sha256.txt` and `-contents.sha256.txt` files

Linux and macOS archives are produced natively with:

```pwsh
pwsh dev/package-cross.ps1 -Runtime linux-x64 -Version v0.1.0
pwsh dev/package-cross.ps1 -Runtime osx-arm64 -Version v0.1.0
```

Use `-LiteBundle` with `dev/package-cross.ps1` for the smaller cross-platform
profile. Cross-platform archives use `.tar.gz` on Linux/macOS runners.

`dev/package-runtime.ps1` still exists as a lower-level helper for publishing
the runtime binary, but the release package scripts above are the supported
distribution path.

## Known gaps

These remain outside the current release package scope:

| Gap | Notes |
|---|---|
| **Windows MSIX** | The zip package runs without an installer; MSIX still needs manifest/signing work. |
| **macOS `.app` bundle** | The tarball includes launch scripts, but a polished `.app` bundle and notarization are separate work. |
| **Linux desktop integration** | A `.desktop` file, icon, and AppImage/deb/rpm packaging are not yet produced. |
| **Auto-update channel** | No signed update server or in-app update flow is shipped. |

These gaps do not block distributing the current archives; they are installer
and update-channel polish rather than missing runtime functionality.
