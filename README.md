<div align="center">
  <img src="assets/svg/banner.svg" alt="Sir Thaddeus — a private, local-first AI assistant for Windows" width="100%" />

  <p><strong>Your thoughts deserve a butler, not an audience.</strong></p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases/latest"><strong>⬇ Download for Windows</strong></a>
    &nbsp;·&nbsp;
    <a href="https://github.com/raydeStar/sir-thaddeus">★ Star on GitHub</a>
    &nbsp;·&nbsp;
    <a href="#trust-model">Trust Model</a>
    &nbsp;·&nbsp;
    <a href="#quick-start">Quick Start</a>
    &nbsp;·&nbsp;
    <a href="#measured-not-guessed">Measured, Not Guessed</a>
  </p>

  <p>
    <a href="https://github.com/raydeStar/sir-thaddeus/actions/workflows/ci-pr.yml">
      <img src="https://github.com/raydeStar/sir-thaddeus/actions/workflows/ci-pr.yml/badge.svg?branch=master" alt="CI status" />
    </a>
    <a href="https://github.com/raydeStar/sir-thaddeus/releases/latest">
      <img src="https://img.shields.io/github/v/release/raydeStar/sir-thaddeus?color=blue&label=Release" alt="Latest release" />
    </a>
    <a href="LICENSE">
      <img src="https://img.shields.io/github/license/raydeStar/sir-thaddeus" alt="Apache 2.0 license" />
    </a>
    <img src="https://img.shields.io/badge/platform-Windows-0078D6" alt="Windows" />
    <img src="https://img.shields.io/badge/telemetry-none-black" alt="No telemetry" />
  </p>
</div>

**Sir Thaddeus is a local AI assistant for Windows that runs fully offline.** Point it at a local model server — [LM Studio](https://lmstudio.ai/), Ollama, or any OpenAI-compatible endpoint — and chat, run permissioned tools, and build a private workspace without a cloud account. Tools are wired through the [Model Context Protocol (MCP)](https://modelcontextprotocol.io/) and every call passes a visible consent gate before it runs. The runtime binds to loopback, your data lives on disk, and there is no telemetry: no cloud, nothing phones home.

Built for developers protecting source code, consultants protecting client work, and anyone tired of renting their mind to the cloud.

![Sir Thaddeus local AI workspace screenshot — private AI chat with permissioned tools and a visible audit trail](assets/images/sir-thaddeus-screenshot.png)

## What You Get

- **Local-first runtime** — binds to `127.0.0.1` on an ephemeral port, ships as a self-contained Windows executable, no cloud account required.
- **Permissioned tools** — every MCP tool call crosses a process boundary and a visible consent gate (`Deny / Once / Session / Always`) before it runs.
- **Private memory** — threads, semantic memory, routines, settings, and a Markdown wiki live on your disk and nowhere else.
- **Model-flexible** — connect LM Studio, Ollama, or any OpenAI-compatible endpoint. Bring your own model.
- **Zero telemetry** — no analytics, no crash reports, no "anonymized" usage data. None.
- **Auditable** — every tool call, permission decision, and outcome is appended to a local audit log you can inspect.

## What You Can Do

- Chat with streaming responses against a configured local or hosted model.
- Run MCP-powered web, file, memory, and system tools behind explicit permission prompts.
- Build a durable local wiki and workspace memory, with page chat, draft generation, and selected-text rewrite.
- Review tool activity, diagnostics, per-turn traces, and audit logs.
- Use stop-all and kill controls when a turn or sidecar needs to end.
- Try beta voice, push-to-talk, compact panel, clipboard, and screen tools.

For how the pieces fit together — shell, loopback runtime, React workspace, assistant pipeline, MCP boundary, permission gate, and local storage — read [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md).

<a id="measured-not-guessed"></a>
## Measured, Not Guessed

Most "AI assistant" projects claim capability. This one measures it, publishes the losses, and deletes the shortcuts that would have inflated the numbers.

Sir Thaddeus ships with a benchmark harness that grades a small local model (a **1.2B** parameter model served through LM Studio) honestly. Every number below is strict value-graded and reported across multiple runs, not a single lucky pass:

- **Tools change what a small model can do — and the harness proves it.** On a set of math probes the 1.2B model scores **0 / 6 unaided**. Give it a `calculator` tool and the same probes go to **5 / 6**. A sandboxed `python_eval` tool takes a 20-item compute suite from roughly **0% to 43%**. The remaining misses are reasoning and comprehension limits the tools can't fix — and the harness says so instead of hiding them.
- **The shortcuts are gone.** The harness deletes about **1,900 lines** of hardcoded-answer solvers that used to regex-match benchmark phrasings and emit canned answers before the model ever ran. What's left is genuine instruction-following and the real tool loop.
- **The scorer grades value, not shape.** The old scorer was correctness-blind on bare-answer items: a confidently *wrong* number could score ~1.0 because it had the right *shape*. Strict-answer items now hard-gate on the actual value (numeric tolerance for decimals, exact match for letters), so a wrong answer fails.
- **The harness verifies its own instruments.** A sandbox canary runs a trivial container before every `python_eval` suite; if Docker is wedged, the run **aborts** rather than silently scoring every tool call as a failure and reading it as "the model got worse."
- **Negative results are reported honestly.** Majority-vote self-consistency was measured three ways and **rejected** for this model family: it was flat on the Python suite at twice the cost, and it *cost* **−10 points** on MMLU-Pro (37.9% → 27.9%). The default configuration ships self-consistency **off**, and the harness records the loss.

A one-command model-intake rig ([`dev/model-intake.ps1`](dev/model-intake.ps1)) turns any new model release into a scorecard plus a recommended configuration, so the "which model, which settings" question gets a measured answer instead of a guess.

The engineering that these numbers describe — the assistant pipeline, the tool loop, and the reasoning steps — lives in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). The suites themselves live under [`tools/SirThaddeus.Harness/Suites/`](tools/SirThaddeus.Harness/Suites/) (`solver-probe`, `solver-probe-calc`, `python-probe`, `knowledge-probe-open`, and more), and [docs/TESTING.md](docs/TESTING.md) explains how to run them.

## Quick Start

### Download

1. Go to [Releases](https://github.com/raydeStar/sir-thaddeus/releases).
2. Download the latest Windows package and unzip it.
3. Run `Launch Sir Thaddeus.cmd` or `Thaddeus.Runtime.exe`.

Windows may show SmartScreen until the app is signed. Verify the checksum beside the ZIP before running a release build. For a full walkthrough of the first-run wizard and local data paths, see [docs/FIRST_RUN.md](docs/FIRST_RUN.md).

### Connect A Model

Sir Thaddeus works best with an OpenAI-compatible endpoint. Configure it in **Settings → Models**.

| Provider | Base URL | Notes |
| --- | --- | --- |
| LM Studio | `http://127.0.0.1:1234/v1` | Start the local server, load an instruction-tuned model, then test it in Settings. |
| Ollama | `http://127.0.0.1:11434/v1` | Uses Ollama's OpenAI-compatible API. Pull and run a model first. |
| Custom | Your `/v1` endpoint | Any compatible local or hosted endpoint. |

<a id="trust-model"></a>
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

Sir Thaddeus is a local-first AI workspace, not a hardened security product. The goal is simple: useful AI work you can inspect, interrupt, and own. For the network boundary and the explicit non-promises, read [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md), [PRIVACY.md](PRIVACY.md), and [DISCLAIMER.md](DISCLAIMER.md).

## Build From Source

Requirements:

- Windows 10/11 for the richest desktop shell experience today.
- .NET SDK `10.0.103` or a compatible feature band (pinned in `global.json`).
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

Common developer commands:

```powershell
./dev/bootstrap.ps1                     # one-time environment setup + restore
./dev/test.ps1                          # fast unit-test loop
./dev/harness.ps1 --all --judge none    # conversation-level validation
./dev/release-package.ps1 -Runtime win-x64
```

See [docs/TESTING.md](docs/TESTING.md) for the full test and harness guide, and [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md) for the release workflow.

## Status

Sir Thaddeus is Windows-first today.

- **Stable v1** — hybrid shell and loopback runtime; React workspace for chat, history, settings, diagnostics, memory, routines, and wiki; MCP tool boundary with explicit permission decisions; local and OpenAI-compatible model configuration; visible activity feed and audit trail.
- **Beta** — voice (ASR/TTS), push-to-talk, tray integration, compact panel, clipboard and screen tools.
- **Deferred** — polished installer, auto-update, scheduled unattended automations, cross-platform desktop parity.

See [docs/archive/V1_SCOPE.md](docs/archive/V1_SCOPE.md), [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md), and [docs/ROADMAP.md](docs/ROADMAP.md) for the release boundary.

## Documentation

Start at the [documentation index](docs/README.md), which groups everything by audience. Quick jumps:

- [docs/FIRST_RUN.md](docs/FIRST_RUN.md) — release-package first-run guide.
- [docs/ARCHITECTURE_PUBLIC.md](docs/ARCHITECTURE_PUBLIC.md) — public architecture overview.
- [docs/TESTING.md](docs/TESTING.md) — tests and the benchmark harness.
- [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md) — honest boundaries.
- [docs/ROADMAP.md](docs/ROADMAP.md) — planned work.
- [CHANGELOG.md](CHANGELOG.md) — release notes.

## Try It

The best argument for a local AI butler is using one.

1. **[Download the latest Windows release](https://github.com/raydeStar/sir-thaddeus/releases/latest)** and unzip it.
2. Point it at LM Studio, Ollama, or your own endpoint.
3. Watch your first MCP tool call ask permission before it runs.

If something feels off, [open an issue](https://github.com/raydeStar/sir-thaddeus/issues). If something works, [star the repo](https://github.com/raydeStar/sir-thaddeus) — it helps the next privacy-minded person find it.

## Contributing & License

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the workflow and [SECURITY.md](SECURITY.md) for reporting a vulnerability. Sir Thaddeus is licensed under the [Apache License 2.0](LICENSE).
