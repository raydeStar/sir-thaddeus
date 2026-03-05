# Build and Publish (Avalonia + Headless)

Date: 2026-03-05

## Prerequisites

- .NET SDK `10.0.103` (see `global.json`)
- Windows packaging host for `win-x64` publish
- Optional: GitHub release voice assets pre-fetched via `./dev/fetch-assets.ps1`

## Local build validation

```powershell
dotnet build SirThaddeus.sln -m:1 -v m
dotnet test tests/SirThaddeus.Tests/SirThaddeus.Tests.csproj -m:1 -v m
```

## Standard release packaging

```powershell
./dev/release-package.ps1
```

Outputs:

- Staged folder: `artifacts/stage/win-x64`
- Zip archive: `artifacts/release/sir-thaddeus-win-x64-<version>.zip`
- Checksums: `*.sha256.txt`

Primary UI executable in package root:

- `SirThaddeus.UI.Avalonia.exe`

Also included:

- `SirThaddeus.McpServer.exe`
- `SirThaddeus.VoiceHost.exe`

## Package smoke validation

```powershell
./dev/smoke-test.ps1
```

The smoke gate validates:

- Required executables and assets are present
- VoiceHost health endpoint responds
- UI shell launches in smoke mode

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
- If voice assets are missing, run:

```powershell
./dev/fetch-assets.ps1
```
