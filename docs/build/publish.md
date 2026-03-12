# Build and Publish (Avalonia + Headless)

Date: 2026-03-05

## Prerequisites

- .NET SDK `10.0.103` (see `global.json`)
- Windows packaging host for `win-x64` publish
- Bundled voice assets fetched via `./dev/fetch-assets.ps1`
- Bundled SearXNG sidecar payload prepared via `./dev/build-searxng-package.ps1`

## Local build validation

```powershell
dotnet build SirThaddeus.sln -m:1 -v m
dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -m:1 -v m
```

## Standard release packaging

```powershell
./dev/fetch-assets.ps1
./dev/build-searxng-package.ps1
./dev/release-package.ps1
```

Default behavior is now **full bundled Windows packaging**:

- includes Avalonia, the packaged headless runtime, bundled VoiceHost assets, and the bundled SearXNG sidecar
- is intended to work as an offline-friendly Windows zip without Docker or a preinstalled local toolchain
- fails the Release build if a valid `search/` payload cannot be staged

To create a smaller developer-oriented package that relies on runtime asset download/self-heal, run:

```powershell
./dev/release-package.ps1 -LiteBundle
```

Outputs:

- Staged folder: `artifacts/stage/win-x64`
- Zip archive: `artifacts/release/sir-thaddeus-win-x64-<version>.zip`
- Archive checksum: `artifacts/release/sir-thaddeus-win-x64-<version>.zip.sha256.txt`
- Package contents checksum manifest: `artifacts/release/sir-thaddeus-win-x64-<version>-contents.sha256.txt`

Primary UI executable in package root:

- `SirThaddeus.UI.Avalonia.exe`

Also included:

- `SirThaddeus.McpServer.exe`
- `SirThaddeus.VoiceHost.exe`
- `headless/SirThaddeus.HeadlessRuntime.exe`

## Package smoke validation

```powershell
./dev/smoke-test.ps1
```

The smoke gate validates:

- Required executables and assets are present
- VoiceHost health endpoint responds
- UI shell launches in smoke mode
- Zip and checksum sidecars stay in sync when run against a packaged archive

## Local runner modes

Start Avalonia UI flow:

```powershell
./dev/localrunner.ps1
```

Start terminal/headless flow:

```powershell
./dev/localrunner.ps1 --terminal
```

## CI/publish target notes

- `win-x64`: primary packaged target currently validated in repo scripts
- `linux-x64`, `osx-x64`, `osx-arm64`: planned publish matrix (UI runtime should remain independent of Windows-only legacy UI)

## Troubleshooting

- If `dotnet` cache locks files (Defender/process lock), rerun the failed command once.
- CI release, PR package, and promote workflows now smoke-test the packaged zip before publishing any release assets.
- If voice assets are missing, run:

```powershell
./dev/fetch-assets.ps1
```

- If the bundled SearXNG payload is missing or stale, run:

```powershell
./dev/build-searxng-package.ps1 -Force
```
