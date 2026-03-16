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
- Bundled `uv` + Python + wheelhouse can create a venv and install voice dependencies offline
- VoiceHost health endpoint responds
- UI shell launches in smoke mode
- Zip and checksum sidecars stay in sync when run against a packaged archive

## Cross-platform local packaging

To test the Linux or macOS packaging script locally (requires `pwsh` on the host):

```powershell
# Linux package (builds from any OS but produces .zip on Windows)
pwsh ./dev/package-cross.ps1 -Runtime linux-x64 -Version dev-local

# macOS package
pwsh ./dev/package-cross.ps1 -Runtime osx-arm64 -Version dev-local
```

Outputs go to `artifacts/release/sir-thaddeus-<rid>-<version>.*`.

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

Every tagged release and manual promote now builds **three packages in parallel** after preflight:

| Package | Runner | Format | Contents |
|---|---|---|---|
| `sir-thaddeus-win-x64-<ver>.zip` | `windows-latest` | zip | UI + Headless + MCP + VoiceHost + SearXNG sidecar + bundled voice/Python assets |
| `sir-thaddeus-linux-x64-<ver>.tar.gz` | `ubuntu-latest` | tar.gz | UI + Headless + MCP (self-contained) + launcher.sh |
| `sir-thaddeus-osx-arm64-<ver>.tar.gz` | `macos-latest` | tar.gz | UI + Headless + MCP (self-contained) + launch.command |

All three end up as GitHub Release assets.

### Free-tier minute budget

- Linux jobs cost 1× → nearly free
- macOS jobs cost 10× → ~45 min build = 450 equivalent minutes
- Use the `skip_macos=true` input on the promote workflow for emergency hotfixes to avoid macOS cost
- PR CI (`ci-pr.yml`) does **not** build macOS — only dev/master pushes trigger the Linux+Windows package step

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
