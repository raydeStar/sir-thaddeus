<div align="center">
  <img src="assets/svg/sir-thaddeus.svg" alt="Sir Thaddeus" width="180" />

  <h1>Sir Thaddeus</h1>

  <p><strong>A local-first AI workspace for controlled agentic workflows.</strong></p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases">
      <img src="https://img.shields.io/github/v/release/raydeStar/sir-thaddeus?color=blue&label=Release" alt="Latest release" />
    </a>
    <a href="https://github.com/raydeStar/sir-thaddeus/blob/main/LICENSE">
      <img src="https://img.shields.io/github/license/raydeStar/sir-thaddeus" alt="Apache 2.0 license" />
    </a>
    <img src="https://img.shields.io/badge/runtime-.NET%2010-blue" alt=".NET 10 runtime" />
    <img src="https://img.shields.io/badge/UI-React%20%2B%20Vite-black" alt="React + Vite UI" />
  </p>
</div>

---

Sir Thaddeus is a chat-and-tools workspace that runs on your machine, talks to
a model **you** chose, only uses tools **you** approved, and keeps every action
visible and stoppable.

It is designed for power users — developers, researchers, technical operators —
who want agentic workflows without giving up control or sending data to a
provider they don't host.

> **What this is not.** Not an unattended automation platform. Not a hosted
> service. Not a cloud product. There is no sign-up. There is no telemetry.

<div align="center">
  <img src="assets/images/sir-thaddeus-screenshot.png" alt="Sir Thaddeus workspace screenshot" width="800" />
  <br /><sub><i>Workspace screenshot — chat, tool activity, and permission prompt.</i></sub>
</div>

---

## Why this exists

Most AI assistants are easy to use because they make decisions for you. Sir
Thaddeus is the opposite: it surfaces every decision and lets you say yes or no.

- **Local model, local data.** Threads, memos, wiki pages, and audit logs live
  in `~/.thaddeus/` (or `%LocalAppData%\SirThaddeus\` on Windows). The runtime
  binds `127.0.0.1:<random-port>` and accepts only the per-launch bearer token.
- **Tools cross a permission boundary.** Every MCP tool call (web fetch, file
  read, screen capture, system command) goes through a broker that asks you,
  records the decision, and can be revoked at any time.
- **Stop is real.** A red kill switch lives in the header. Pressing it tears
  down active tool calls, sidecars, and the runtime itself.

If "AI agent that you can actually audit" sounds boring, that's the point.

---

## Quickstart

### Requirements

- **.NET 10 SDK** for building from source. Single-file binaries are coming via
  [`dev/package-runtime.ps1`](dev/package-runtime.ps1); see
  [`docs/packaging.md`](docs/packaging.md).
- **Node.js 18+** (or any version compatible with Vite 5) to build the web UI
  during development.
- **A local model server**, OpenAI-compatible. We test against:
  - [LM Studio](https://lmstudio.ai/) (default — `http://127.0.0.1:1234/v1`)
  - [Ollama](https://ollama.com/) (`http://127.0.0.1:11434/v1`)
  - OpenAI hosted (`https://api.openai.com/v1`, requires API key)
  - Any other OpenAI-compatible endpoint via the **Custom** preset.

### Run from source (recommended for v1.0)

```bash
# 1) Build the web bundle and sync it into the runtime's wwwroot.
cd web
npm install
npm run build
cd ..

# (Windows / PowerShell) Copy the bundle into wwwroot.
Copy-Item web/dist/index.html src/Thaddeus.Runtime/wwwroot/index.html -Force
Copy-Item web/dist/assets/* src/Thaddeus.Runtime/wwwroot/assets/ -Recurse -Force

# (macOS / Linux)
cp web/dist/index.html src/Thaddeus.Runtime/wwwroot/index.html
cp -R web/dist/assets/. src/Thaddeus.Runtime/wwwroot/assets/

# 2) Run the runtime directly (development).
dotnet run --project src/Thaddeus.Runtime

# 3) Or launch the Windows shell (supervises the runtime, opens the webview).
dotnet run --project src/Thaddeus.Shell
```

The runtime prints a loopback URL such as
`http://127.0.0.1:54971/?access_token=...`. Open it in any modern browser, or
— on Windows — let `Thaddeus.Shell` open the embedded webview for you.

For self-contained binaries, see
[`dev/package-runtime.ps1`](dev/package-runtime.ps1) and
[`docs/packaging.md`](docs/packaging.md).

### First-run setup

1. Onboarding walks you through privacy defaults and a model probe.
2. Settings → **Models** → pick a provider preset and **Test connection**.
3. Send your first message. Approve any tool prompts that appear.

---

## What's in v1.0

For the full contract, see [`V1_SCOPE.md`](V1_SCOPE.md).

### Core v1 features

| Surface | What it does |
|---|---|
| **Hybrid runtime** | Single `Thaddeus.Runtime` binary; Shell supervises it on Windows. |
| **Workspace UI** | React + Vite + TanStack Router, served from the runtime over loopback. |
| **Chat** | Threaded, streaming, persistent. Tool activity inline. Source cards for web-search results. |
| **Models** | LM Studio / Ollama / OpenAI / custom OpenAI-compatible. Optional gatekeeper model for tool routing. |
| **MCP tools** | Web search, file read, document parse (PDF / DOCX / XLSX / CSV / RTF / Markdown / text), system allowlist, math, time, holidays. |
| **Permissions** | Deny / Once / Session / Always, with persisted policy and per-call audit. |
| **Wiki / canvas** | CRUD, revisions, import/export, search, page chat, draft, selected-text rewrite. |
| **Routines** | User-invoked checklists with run history. No background firing. |
| **Activity & diagnostics** | Live activity feed, runtime state pill, diagnostics page (uptime, threads, voice, build, logs path). |
| **Stop-all / kill** | Red kill switch in the header; `/api/stop-all` aborts in-flight tool work. |

### Beta — works on the maintainer's box; not the headline

- Voice (Whisper.cpp ASR + KokoroSharp TTS, Piper as legacy fallback).
- Push-to-talk global hotkey.
- Tray integration and minimize-to-tray.
- Compact panel (`/compact`) — Phase-2 stub today.
- Clipboard read / write and screen capture tools (Windows-only).

### Deferred — not in v1, intentionally

- Scheduled / unattended automations (removed in 0.3.0).
- Profile / personality admin in the workspace UI.
- Polished installers (MSIX, signed `.app`, AppImage).
- Auto-update.
- Cross-platform desktop UX parity (macOS/Linux run the runtime; Shell ergonomics are Windows-first).

See [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md) for the
honest version.

---

## Trust and control

| Property | How it's enforced |
|---|---|
| Local-first | Runtime binds `127.0.0.1` only. No outbound calls except those triggered by a tool you approved. |
| Per-launch token | Bearer token rotates each launch. Browsers cannot reach the runtime without it. |
| Permission gate | Every MCP tool call goes through `ToolPermissionGate`. Decisions are persisted to settings. |
| Audit | `~/.thaddeus/logs/audit.jsonl` records every tool call, permission decision, and outcome. |
| Stop | `/api/stop-all` aborts active turns and sidecar processes. |
| No telemetry | None. Not anonymized, not opt-in. The setting exists in case the policy ever changes. |

---

## Architecture (short version)

- **Shell** (`src/Thaddeus.Shell`) — Windows supervisor. Starts the runtime,
  opens the embedded webview, owns tray + global hotkeys. Optional.
- **Runtime** (`src/Thaddeus.Runtime`) — ASP.NET Core minimal API on loopback.
  Hosts `wwwroot/` (the built React bundle), exposes REST + WebSocket.
- **Web** (`web/`) — React + Vite + TanStack Router workspace.
- **MCP server** (`apps/mcp-server`) — stdio MCP server hosting the toolset.
- **Tools** (`packages/mcp-tools-core`, `packages/mcp-tools-windows`) — split
  between cross-platform (`net10.0`) and Windows-only.
- **Wiki** (`packages/wiki`) — durable Markdown-backed knowledge with revisions.
- **Voice** (`apps/voice-host`, `apps/voice-backend`, `src/Thaddeus.Tts.*`) —
  optional sidecars for ASR and TTS. Beta.

For a 10-minute read, see [`docs/ARCHITECTURE_PUBLIC.md`](docs/ARCHITECTURE_PUBLIC.md).
For the full version, [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

---

## Development

```bash
# Solution build (Debug)
dotnet build SirThaddeus.sln

# Solution build + tests (Release)
pwsh dev/test.ps1 -Configuration Release

# Full preflight (bootstrap + Release tests)
pwsh dev/preflight.ps1

# Web typecheck + build
cd web && npm install && npm run typecheck && npm run build

# Web E2E (Playwright)
cd web && npm run test:e2e
```

For platform packaging, see [`docs/packaging.md`](docs/packaging.md).

---

## Documentation

- [`V1_SCOPE.md`](V1_SCOPE.md) — what v1 is and is not.
- [`docs/DEMO_SCRIPT.md`](docs/DEMO_SCRIPT.md) — golden 3–5 minute demo.
- [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md) — honest boundaries.
- [`docs/RELEASE_CHECKLIST.md`](docs/RELEASE_CHECKLIST.md) — pre-release gate.
- [`docs/ROADMAP.md`](docs/ROADMAP.md) — v1.0 → v1.1 → v2.0.
- [`docs/ARCHITECTURE_PUBLIC.md`](docs/ARCHITECTURE_PUBLIC.md) — architecture in 10 minutes.
- [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — full architecture.
- [`docs/SETTINGS.md`](docs/SETTINGS.md) — settings reference.
- [`docs/hybrid-shell.md`](docs/hybrid-shell.md) — Phase-1 hybrid notes.
- [`docs/packaging.md`](docs/packaging.md) — building self-contained binaries.
- [`PRIVACY.md`](PRIVACY.md), [`SECURITY.md`](SECURITY.md), [`CONTRIBUTING.md`](CONTRIBUTING.md).

---

## License

Apache 2.0. See [`LICENSE`](LICENSE).
