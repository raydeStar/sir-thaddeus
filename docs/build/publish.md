# Build and Publish (Hybrid Runtime)

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

Default behavior is now **full bundled packaging**:

- includes the hybrid runtime, bundled VoiceHost assets, and the bundled SearXNG sidecar
- is intended to work as an offline-friendly Windows zip without Docker or a preinstalled local toolchain
- fails the Release build if a valid `search/` payload cannot be staged

To create a smaller developer-oriented package that relies on runtime asset download/self-heal, run:

```powershell
./dev/release-package.ps1 -LiteBundle
```

Outputs:

- Staged folder: `artifacts/stage/win-x64`
- Full zip archive: `artifacts/release/sir-thaddeus-win-x64-<version>-full.zip`
- Lite zip archive: `artifacts/release/sir-thaddeus-win-x64-<version>-lite.zip`
- Archive checksums: `artifacts/release/sir-thaddeus-win-x64-<version>-<profile>.zip.sha256.txt`
- Package contents checksum manifest: `artifacts/release/sir-thaddeus-win-x64-<version>-<profile>-contents.sha256.txt`

Primary executable in package root:

- `Thaddeus.Runtime.exe`

Also included:

- `SirThaddeus.McpServer.exe`
- `SirThaddeus.VoiceHost.exe`

## Package smoke validation

```powershell
./dev/smoke-test.ps1
```

The smoke gate validates:

- Required executables and assets are present
- Bundled `uv` + Python + wheelhouse can create a venv and install voice dependencies offline
- VoiceHost health endpoint responds
- Hybrid runtime launches cleanly
- Zip and checksum sidecars stay in sync when run against a packaged archive

## Cross-platform local packaging

To test the Linux or macOS packaging script locally (requires `pwsh` on the host):

```powershell
# Linux package (full profile)
pwsh ./dev/package-cross.ps1 -Runtime linux-x64 -Version dev-local

# Linux package (lite profile)
pwsh ./dev/package-cross.ps1 -Runtime linux-x64 -LiteBundle -Version dev-local

# macOS package (full profile)
pwsh ./dev/package-cross.ps1 -Runtime osx-arm64 -Version dev-local

# macOS package (lite profile)
pwsh ./dev/package-cross.ps1 -Runtime osx-arm64 -LiteBundle -Version dev-local
```

Outputs go to `artifacts/release/sir-thaddeus-<rid>-<version>-<profile>.*`.

## Local runner modes

Start hybrid runtime flow:

```powershell
./dev/localrunner.ps1
```

Start terminal/headless flow:

```powershell
./dev/localrunner.ps1 --terminal
```

## CI/publish target notes

Every tagged release and manual promote now builds **full + lite packages for each platform** in parallel after preflight:

| Package | Runner | Format | Contents |
|---|---|---|---|
| `sir-thaddeus-win-x64-<ver>-full.zip` + `...-lite.zip` | `windows-latest` | zip | Full: Runtime + MCP + VoiceHost + SearXNG + bundled voice/Python; Lite: reduced optional bundled payloads |
| `sir-thaddeus-linux-x64-<ver>-full.tar.gz` + `...-lite.tar.gz` | `ubuntu-latest` | tar.gz | Full: Runtime + MCP + VoiceHost + launcher; Lite: Runtime + MCP + launcher |
| `sir-thaddeus-osx-arm64-<ver>-full.tar.gz` + `...-lite.tar.gz` | `macos-latest` | tar.gz | Full: Runtime + MCP + VoiceHost + launcher; Lite: Runtime + MCP + launcher |

All three end up as GitHub Release assets.

### Rolling releases

Pushes to `dev` and `master` now publish separate rolling artifacts too:

- `latest-dev` for the current `dev` branch head
- `latest` for the current `master` branch head

Each rolling release now carries:

- `sir-thaddeus-win-x64-<branch>-<sha>-full.zip`
- `sir-thaddeus-win-x64-<branch>-<sha>-lite.zip`
- `sir-thaddeus-linux-x64-<branch>-<sha>-full.tar.gz`
- `sir-thaddeus-linux-x64-<branch>-<sha>-lite.tar.gz`
- `sir-thaddeus-osx-arm64-<branch>-<sha>-full.tar.gz`
- `sir-thaddeus-osx-arm64-<branch>-<sha>-lite.tar.gz`

GitHub's built-in `Source code (zip)` and `Source code (tar.gz)` entries are automatic repository snapshots for the release tag. They are not the packaged app artifacts and are expected to appear separately from the uploaded platform bundles.

### Free-tier minute budget

- Linux jobs cost 1× → nearly free
- macOS jobs cost 10× → ~45 min build = 450 equivalent minutes
- Use the `skip_macos=true` input on the promote workflow for emergency hotfixes to avoid macOS cost
- Pull requests still do not build release packages
- Pushes to `dev` and `master` now publish Windows, Linux, and macOS rolling artifacts
- Tagged releases and promote runs publish the same three-platform layout with versioned names

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
