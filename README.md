<div align="center">
  <img src="assets/svg/sir-thaddeus.svg" alt="Sir Thaddeus logo" width="160" />

  <h1>Sir Thaddeus</h1>

  <p><strong>A local-first AI workspace for controlled agentic workflows.</strong></p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases">
      <img src="https://img.shields.io/github/v/release/raydeStar/sir-thaddeus?color=blue&label=Release" alt="Latest release" />
    </a>
    <a href="https://github.com/raydeStar/sir-thaddeus/blob/main/LICENSE">
      <img src="https://img.shields.io/github/license/raydeStar/sir-thaddeus" alt="Apache 2.0 license" />
    </a>
    <img src="https://img.shields.io/badge/.NET-10-blue" alt=".NET 10" />
    <img src="https://img.shields.io/badge/LLM-OpenAI--compatible-orange" alt="OpenAI-compatible local model endpoints" />
  </p>
</div>

Sir Thaddeus is a power-user AI workspace for chat, MCP-powered tools, explicit permissions, local storage, diagnostics, and durable wiki/canvas knowledge. It is designed for people who want useful agentic workflows without silent background autonomy or opaque cloud control.

The public v1 product surface is the hybrid shell/runtime/workspace stack: [src/Thaddeus.Shell/](src/Thaddeus.Shell/), [src/Thaddeus.Runtime/](src/Thaddeus.Runtime/), [web/](web/), [apps/mcp-server/](apps/mcp-server/), [packages/mcp-shared/](packages/mcp-shared/), [packages/mcp-tools-core/](packages/mcp-tools-core/), [packages/mcp-tools-windows/](packages/mcp-tools-windows/), and [packages/wiki/](packages/wiki/). The legacy terminal runtime in [apps/headless-runtime/](apps/headless-runtime/) remains for harness and transitional work, but it is not the main product surface.

![Sir Thaddeus workspace screenshot](assets/images/sir-thaddeus-screenshot.png)

No polished demo GIF is checked in yet. Use [docs/DEMO_SCRIPT.md](docs/DEMO_SCRIPT.md) to record the v1 demo without inventing features.

## What Makes It Different

- Local-first architecture: the workspace is served from a loopback runtime on your machine.
- Controlled tool use: tool calls cross an MCP boundary and go through explicit permission policy.
- Visible actions: chat streaming, tool activity, activity feed, diagnostics, and audit logs make work inspectable.
- Durable knowledge: wiki/canvas content, revisions, import/export, memos, routines, and run history live locally.
- Practical model support: LM Studio, Ollama's OpenAI-compatible shim, hosted OpenAI-compatible APIs, and custom endpoints all use the same settings surface.

## Requirements

- Windows 10/11 for the richest shell experience today.
- .NET SDK `10.0.103` or newer compatible feature band for source builds. See [global.json](global.json).
- Node.js and npm for the React workspace build.
- PowerShell 5.1 or newer for repo scripts.
- Optional: LM Studio, Ollama, or another OpenAI-compatible model endpoint.
- Optional beta: local voice sidecar assets and machine setup for ASR/TTS.

The loopback runtime and many packages are designed to build beyond Windows, but v1 desktop ergonomics are Windows-first. Do not read that as cross-platform desktop parity.

## Quick Start From Source

```powershell
git clone https://github.com/raydeStar/sir-thaddeus.git
cd sir-thaddeus
dotnet restore SirThaddeus.sln
```

Build the web workspace:

```powershell
Push-Location web
npm ci
npm run build
Pop-Location
```

Launch the hybrid shell:

```powershell
dotnet run --project src/Thaddeus.Shell/Thaddeus.Shell.csproj
```

If you want to run the loopback runtime directly, use:

```powershell
dotnet run --project src/Thaddeus.Runtime/Thaddeus.Runtime.csproj
```

The runtime binds to `127.0.0.1` on an ephemeral port and serves the local workspace with a per-launch token.

## Local Model Setup

Sir Thaddeus can run with the stub assistant for smoke testing, but the real workflow expects an OpenAI-compatible endpoint.

| Provider | Base URL | Notes |
| --- | --- | --- |
| LM Studio | `http://127.0.0.1:1234/v1` | Start the local server in LM Studio, load an instruction-tuned model, then select or test it in Settings. |
| Ollama | `http://127.0.0.1:11434/v1` | Uses Ollama's OpenAI-compatible shim. Pull and run the model first. |
| Custom | Your `/v1` endpoint | Any compatible endpoint can be used. Add an API key only when the endpoint requires one. |

Small local models vary widely. If tool use is unreliable, use a stronger instruction-tuned model or keep the gatekeeper configuration conservative.

## Core v1 Features

- Hybrid shell/runtime launch.
- Local loopback workspace hosting.
- React workspace UI with chat, history, activity, memory, routines, settings, diagnostics, onboarding, wiki, and compact routes.
- Threaded chat with streaming assistant responses.
- Local/OpenAI-compatible model configuration and stub fallback.
- MCP tool boundary with manifest-driven tool metadata.
- Permission prompts with once, session, always, and deny decisions.
- Persisted permission policy and visible tool activity.
- Activity feed and diagnostics.
- Wiki/canvas CRUD, revisions, import/export, search, and assistant actions.
- Manual routines and run history.
- Stop-all and kill controls.
- File/document tools when used under permission gating.

## Beta And Deferred Features

Beta in v1:

- Voice / ASR / TTS.
- Push-to-talk.
- Tray integration.
- Global shortcuts.
- Compact panel.
- Windows desktop observation hooks.
- Clipboard and screen tools.

Deferred from v1:

- Scheduled automations.
- Profile/personality administration in the v2 workspace.
- Polished installers.
- Auto-update.
- Cross-platform desktop UX parity.
- Advanced audit-search/admin pane.

See [V1_SCOPE.md](V1_SCOPE.md), [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md), and [docs/ROADMAP.md](docs/ROADMAP.md) for the release boundary.

## Trust And Control Model

Sir Thaddeus is built around visible, bounded actions:

- The runtime listens on loopback, not the LAN.
- API calls use a per-launch bearer token.
- Tool calls go through MCP and permission policy.
- Dangerous or side-effecting tool groups can be approved once, approved for the session, always allowed, or denied.
- Tool activity, runtime activity, diagnostics, and audit logs are local review surfaces.
- Stop-all and kill controls are part of the v1 surface.

This is not a claim of production-grade security. It is a local-first trust model: loopback hosting, explicit permission prompts, visible actions, local persistence, and auditability.

## Known Limitations

- Windows has the most complete desktop shell behavior today.
- Voice depends on local sidecars, assets, models, drivers, and machine setup.
- The compact panel is a minimal beta surface.
- Tray and global shortcuts need live Windows validation before being promoted beyond beta.
- There is no scheduled unattended automation in v1.
- There is no polished installer or auto-update channel yet.
- Runtime portability exists before full desktop parity.
- Local model quality depends on the configured model and endpoint.
- Web/live-data quality depends on providers and network conditions.

Read the full list in [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md).

## Development Commands

Bootstrap dependencies:

```powershell
./dev/bootstrap.ps1
```

Build the web workspace:

```powershell
Push-Location web
npm ci
npm run build
Pop-Location
```

Build the .NET solution:

```powershell
dotnet build SirThaddeus.sln -c Release --no-restore
```

Launch the shell from source:

```powershell
dotnet run --project src/Thaddeus.Shell/Thaddeus.Shell.csproj
```

Create a single-file runtime publish:

```powershell
./dev/package-runtime.ps1 -Rids win-x64
```

Create a release package after validation:

```powershell
./dev/release-package.ps1 -Runtime win-x64
```

## Testing Commands

Fast web checks:

```powershell
Push-Location web
npm run typecheck
npm run build
Pop-Location
```

Normal .NET gate without the screen-observe harness:

```powershell
./dev/test.ps1 -Configuration Release -Restore $true -SkipScreenObserveHarness
```

Full preflight, including heavier harness behavior:

```powershell
./dev/preflight.ps1
```

Package smoke test after building a package:

```powershell
./dev/smoke-test.ps1 -SkipLaunch
```

Integration/model harness testing can require live local services and GPU availability. If those are not available, skip them explicitly and record the gap in [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md).

## Documentation Map

- [V1_SCOPE.md](V1_SCOPE.md) - v1 scope lock and release boundary.
- [docs/DEMO_SCRIPT.md](docs/DEMO_SCRIPT.md) - 3-5 minute golden demo.
- [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md) - honest v1 limitations.
- [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md) - practical readiness checklist.
- [docs/ROADMAP.md](docs/ROADMAP.md) - v1.0, v1.1, and v2.0 roadmap.
- [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md) - public architecture overview.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) - full architecture reference.
- [docs/FEATURE_GAP_MATRIX.md](docs/FEATURE_GAP_MATRIX.md) - subsystem completion matrix.
- [docs/packaging.md](docs/packaging.md) - packaging notes.

## License

Sir Thaddeus is licensed under the [Apache License 2.0](LICENSE).
