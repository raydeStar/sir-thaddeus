<div align="center">
  <img src="assets/svg/sir-thaddeus.svg" alt="Sir Thaddeus logo" width="150" />

  <h1>Sir Thaddeus</h1>

  <p><strong>Your thoughts deserve a butler, not an audience.</strong></p>

  <p><em>A private, local-first AI assistant for Windows. No cloud. No telemetry. No thoughts traded for convenience.</em></p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases/latest"><strong>⬇ Download for Windows</strong></a>
    &nbsp;·&nbsp;
    <a href="https://github.com/raydeStar/sir-thaddeus">★ Star on GitHub</a>
    &nbsp;·&nbsp;
    <a href="#trust-model">Trust Model</a>
    &nbsp;·&nbsp;
    <a href="#quick-start">Quick Start</a>
  </p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases">
      <img src="https://img.shields.io/github/v/release/raydeStar/sir-thaddeus?color=blue&label=Release" alt="Latest release" />
    </a>
    <a href="https://github.com/raydeStar/sir-thaddeus/blob/main/LICENSE">
      <img src="https://img.shields.io/github/license/raydeStar/sir-thaddeus" alt="Apache 2.0 license" />
    </a>
    <img src="https://img.shields.io/badge/.NET-10-blue" alt=".NET 10" />
    <img src="https://img.shields.io/badge/MCP-tools-green" alt="MCP tools" />
    <img src="https://img.shields.io/badge/telemetry-none-black" alt="No telemetry" />
  </p>
</div>

**Sir Thaddeus is a private AI assistant that runs on your machine.** Chat with local language models, run permissioned tools, and keep every prompt, note, and audit log on your own hardware. The runtime binds to loopback, the workspace lives on disk, and nothing phones home.

Built for developers protecting source code, consultants protecting client work, and anyone tired of renting their mind to the cloud.

![Sir Thaddeus local AI workspace screenshot — private AI chat with permissioned tools and visible audit trail](assets/images/sir-thaddeus-screenshot.png)

## What You Get

- **Local-first runtime** — bound to `127.0.0.1`, ships as a single executable, no cloud account required.
- **Permissioned tools** — every MCP tool call passes through a visible consent gate before it runs.
- **Private memory** — notes, wiki, routines, settings, and chat history live on your disk and nowhere else.
- **Model-flexible** — connect LM Studio, Ollama, or any OpenAI-compatible endpoint. Bring your own model.
- **Zero telemetry** — no analytics, no crash reports, no "anonymized" usage data. None.
- **Auditable** — every tool call, permission decision, and outcome is logged locally for you to inspect.

## What You Can Do

- Chat with streaming responses.
- Connect local models through LM Studio or Ollama.
- Run MCP-powered tools with explicit permission prompts.
- Build a local wiki and durable workspace memory.
- Track tool activity, diagnostics, and audit logs.
- Use stop-all controls when a turn or sidecar needs to end.
- Try beta voice, push-to-talk, compact panel, clipboard, and screen tools.

## Quick Start

### Download

1. Go to [Releases](https://github.com/raydeStar/sir-thaddeus/releases).
2. Download the latest Windows package.
3. Unzip it.
4. Run `Launch Sir Thaddeus.cmd` or `Thaddeus.Runtime.exe`.

Windows may show SmartScreen until the app is signed. Verify the checksum beside the ZIP before running a release build.

### Connect A Model

Sir Thaddeus works best with an OpenAI-compatible endpoint.

| Provider | Base URL | Notes |
| --- | --- | --- |
| LM Studio | `http://127.0.0.1:1234/v1` | Start the local server, load an instruction-tuned model, then test it in Settings. |
| Ollama | `http://127.0.0.1:11434/v1` | Uses Ollama's OpenAI-compatible API. Pull and run a model first. |
| Custom | Your `/v1` endpoint | Use any compatible local or hosted endpoint. |

## Build From Source

Requirements:

- Windows 10/11 for the richest desktop shell experience today.
- .NET SDK `10.0.103` or a compatible feature band.
- Node.js and npm for the React workspace.
- PowerShell 5.1 or newer.

```powershell
git clone https://github.com/raydeStar/sir-thaddeus.git
cd sir-thaddeus
dotnet restore SirThaddeus.sln

Push-Location web
npm ci
npm run build
Pop-Location

dotnet run --project src/Thaddeus.Shell/Thaddeus.Shell.csproj
```

To run the loopback runtime directly:

```powershell
dotnet run --project src/Thaddeus.Runtime/Thaddeus.Runtime.csproj
```

## Trust Model

Every promise on the front of the box maps to something concrete in the code:

| Promise | How it works |
| --- | --- |
| Local-first | The runtime binds to `127.0.0.1`. Nothing listens on the network. |
| Per-launch access | A bearer token rotates each launch — old tokens die when the app does. |
| Tool consent | MCP tool calls pass through a permission gate before they run. |
| Auditability | Tool calls, permission decisions, and outcomes are logged locally. |
| Stop control | `/api/stop-all` aborts active turns and sidecar processes. |
| No telemetry | No analytics. No crash reports. No "anonymized" usage data. None. |

Sir Thaddeus is a local-first AI workspace, not a hardened security product. The goal is simple: useful AI work you can inspect, interrupt, and own. If you need air-gapped operation, see [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md) for the network boundary in detail.

## Status

Sir Thaddeus is Windows-first today.

Stable v1 surface:

- Hybrid shell and local runtime.
- React workspace for chat, history, settings, diagnostics, memory, routines, and wiki.
- MCP tool boundary with explicit permission decisions.
- Local and OpenAI-compatible model configuration.
- Visible activity feed and audit trail.

Beta:

- Voice, ASR, and TTS.
- Push-to-talk.
- Tray integration.
- Compact panel.
- Clipboard and screen tools.

Deferred:

- Polished installer.
- Auto-update.
- Scheduled unattended automations.
- Cross-platform desktop parity.

See [V1_SCOPE.md](V1_SCOPE.md), [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md), and [docs/ROADMAP.md](docs/ROADMAP.md) for the release boundary.

## Developer Commands

Bootstrap:

```powershell
./dev/bootstrap.ps1
```

Build:

```powershell
dotnet build SirThaddeus.sln -c Release --no-restore
```

Test:

```powershell
./dev/test.ps1 -Configuration Release -Restore $true -SkipScreenObserveHarness
```

Package:

```powershell
./dev/release-package.ps1 -Runtime win-x64
```

Smoke test:

```powershell
./dev/smoke-test.ps1 -SkipLaunch
```

## Documentation

- [README_FIRST_RUN.md](README_FIRST_RUN.md): release package first-run guide.
- [docs/DEMO_SCRIPT.md](docs/DEMO_SCRIPT.md): demo and recording script.
- [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md): current limitations.
- [docs/ROADMAP.md](docs/ROADMAP.md): planned work.
- [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md): architecture overview.
- [docs/RELEASE_CHECKLIST.md](docs/RELEASE_CHECKLIST.md): release readiness checklist.
- [CHANGELOG.md](CHANGELOG.md): release notes.

## Try It

The best argument for a local AI butler is using one.

1. **[Download the latest Windows release](https://github.com/raydeStar/sir-thaddeus/releases/latest)** and unzip it.
2. Point it at LM Studio, Ollama, or your own endpoint.
3. Watch your first MCP tool call ask permission before it runs.

If something feels off, [open an issue](https://github.com/raydeStar/sir-thaddeus/issues). If something works, [star the repo](https://github.com/raydeStar/sir-thaddeus) — it helps the next privacy-minded person find it.

## License

Sir Thaddeus is licensed under the [Apache License 2.0](LICENSE).
