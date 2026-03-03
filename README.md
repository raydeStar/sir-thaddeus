<div align="center">
  <img src="assets/svg/sir-thaddeus.svg" alt="Sir Thaddeus" width="180" />

  <div style="font-size: 2em; font-weight: bold; margin-top: 0.5em;">Sir Thaddeus</div>
  
  <p>
    <strong>A permissioned AI runtime that runs on your machine.</strong>
  </p>

  <hr width="100%" />
</div>

## A Local‑First AI Copilot

Sir Thaddeus is a local-first AI runtime for Windows.
It connects to your own models, executes actions only with approval, and never operates in the background without consent.

Sir Thaddeus runs on your machine.

A local‑first, permissioned AI runtime for Windows. Connects to your local models (such as LM Studio) and executes actions only with explicit approval.

No telemetry by default. No background activity without consent. No hidden autonomy.
Every action is visible. Press STOP — and it stops.

---

## What It Feels Like To Use

Hold your push‑to‑talk hotkey.

Say:

> “When is the grocery store open?”

Before doing anything, Thaddeus proposes what he wants to do next.

You see:

- What access is requested
- Why it’s needed
- How long it will last

You approve.

He performs the action. The result appears. The permission expires automatically. The action is written to a local audit log.

Nothing runs silently. Nothing lingers.

That same interaction pattern applies to everything.

<div align="center">
  <img src="assets/images/sir-thaddeus-screenshot.png" alt="Sir Thaddeus screenshot depicting the front page" width="800" />
</div>

---

## Current Capabilities (V1)

### Interaction

- 🎙️ **Push‑to‑talk voice** (release to send)
- ⌨️ **Command palette** for typed workflows
- 🛑 **Global STOP kill switch**

### Local Intelligence

- 🧠 **Local LLM integration** (LM Studio supported)
- 🔍 **First‑principles reasoning** for breaking down problems and reframing logic puzzles
- 📚 **Lightweight document reading** (text‑based files)

### System Awareness (Permissioned)

- 🖥️ **Screen reading** (active window or full screen)
- 🌐 **Browser search and page reading**
- 📂 **File listing and file reading** (size‑limited, read‑only)
- 🧾 **Allowlisted system commands**

### Trust & Safety

- 🔐 **Explicit, time‑boxed permission tokens**
- 📜 **Local, append‑only audit log**
- 🚨 **Panic mode + safe mode fail‑closed gates**
- 🧮 **Tool budgets** to prevent runaway automation

### Optional (if service connected)

- 👀 **Background “watchers”** for website changes
- 🔔 **Local notifications** for monitored events

---

## Quick Start

No cloud account required. No telemetry by default.

Getting up and running takes a few minutes.

1. Go to the **Releases** page.
2. Download the latest release ZIP.
3. Unzip the archive.
4. Run `SirThaddeus.exe`\
   (Windows may show a security warning — choose *More Info → Run Anyway*.)
5. Start your local LLM runner.\
   *(Tested with LM Studio.)*
6. Follow the initial setup prompt inside the app.

That’s it.

---

## The Contract

1. **You are the principal.** He proposes actions; you approve them.
2. **Nothing runs silently.** If it acts, you see it.
3. **STOP always works.** The kill switch revokes permissions and halts execution immediately.

Sir Thaddeus is not designed to replace your agency. He is designed to extend it — with boundaries.

---

## Architecture (Five Layers)

Sir Thaddeus runs as a five-layer stack:

**propose -> validate -> execute -> observe -> verify -> (repair) -> repeat**

Every permissioned action flows through Layer 1.

```mermaid
flowchart LR
  subgraph loop [Layer 1: Loop - packages/agent]
    Loop[Bounded Agent Loop]
    Context[Run Context and History]
    Router[Intent Router]
    Gate[Policy Gate]
    Validate[Action and Completion Validation]
    Repair[Targeted Repair]
  end

  subgraph frontend [Layer 2: Interface - apps/desktop-runtime]
    Tray[System Tray]
    Overlay[WPF Overlay]
    PTT[Audio Input]
    Playback[Audio Playback]
    Palette[Command Palette]
  end

  subgraph model [Layer 3: Model - packages/llm-client]
    LmStudio[LM Studio / OpenAI-compatible]
  end

  subgraph tools [Layer 4: Tools - apps/mcp-server + packages/memory + memory-sqlite]
    Server[MCP Server - stdio]
    Toolset[Browser / File / System / Screen / WebSearch / Weather / Utilities]
    Memory[SQLite Memory and Retrieval]
  end

  subgraph voice [Layer 5: Voice - apps/voice-host + voice-backend]
    VoiceHost[VoiceHost Proxy]
    VoiceBackend[Voice Backend - Python]
    VoiceBackend --> VoiceHost
  end

  PTT -->|audio buffer| VoiceHost
  VoiceHost -->|transcribed text| Loop
  Palette -->|typed request| Loop

  Loop --> Router --> Gate
  Gate -->|allowed tools + budgets| Loop

  Loop -->|model prompt| LmStudio
  LmStudio -->|tool_calls / next action| Loop

  Loop --> Validate
  Validate -->|blocked/ok| Loop
  Validate -->|complete/partial/missing| Repair
  Repair -->|targeted follow-up| Loop

  Loop -->|tools/call| Server
  Server --> Toolset
  Server --> Memory
  Server -->|tool result| Loop

  Loop -->|final text| VoiceHost
  VoiceHost -->|audio stream| Playback

  Loop -->|events| Overlay
  Tray --> Overlay
```

### Layer responsibilities

| Layer | Project(s) | Responsibility | Talks to |
| --- | --- | --- | --- |
| **Layer 1: Loop** | `packages/agent` | Turn control plane: route, gate, validate, repair, complete | Interface, Model, Tools, Voice |
| **Layer 2: Interface** | `apps/desktop-runtime` | Tray, overlay, hotkeys, command palette, push-to-talk UX | Loop, Voice |
| **Layer 3: Model** | `packages/llm-client` | OpenAI-style model calls (`/v1/chat/completions`, `/v1/embeddings`) | LM Studio, Loop |
| **Layer 4: Tools** | `apps/mcp-server`, `packages/memory`, `packages/memory-sqlite` | MCP tools plus local memory retrieval/storage | Loop |
| **Layer 5: Voice** | `apps/voice-host`, `apps/voice-backend` | Local ASR/TTS transport and runtime | Interface, Loop |

---

## Project structure

```
sir-thaddeus/
├── apps/
│   ├── desktop-runtime/
│   ├── voice-host/
│   ├── voice-backend/
│   └── mcp-server/
├── assets/
├── packages/
├── tests/
├── tools/
└── project-notes/
```

---

## License

See LICENSE file.

