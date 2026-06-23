# Sir Thaddeus - First Run

This package is a self-contained Windows build. You do not need to install the .NET runtime.

## Quick start: Download, Unzip, Run

1. **Download** the release ZIP from the [Releases](https://github.com/raydeStar/sir-thaddeus/releases) page.
2. **Unzip** anywhere — you'll get one folder named `sir-thaddeus-win-x64-v…`.
3. **Open** that folder and run `Thaddeus.Runtime.exe`.

That's it. Everything else happens automatically.

> If Windows SmartScreen appears, click **More info -> Run anyway** after verifying the source.
> *Unsigned binaries may trigger AV heuristics; verify the checksum (see below).*

## What happens on first run

The **Setup Wizard** walks you through four short steps:

1. **Welcome** — confirm you understand Sir Thaddeus runs locally and stores
   data on your machine.
2. **Privacy** — review the privacy defaults (telemetry off, screen capture
   off, local-only mode). All adjustable later in Settings → General.
3. **Voice (optional)** — review the push-to-talk and stop-all hotkey
   bindings. Voice is a Beta feature in v1.0; you can finish onboarding
   without enabling it.
4. **Done** — onboarding completes; the workspace opens.

After onboarding, connect your model from **Settings → Models**: pick a
provider preset (LM Studio / Ollama / OpenAI / Custom), confirm the base
URL, and click **Test connection**.

> **Personality admin and display-name / about-me UI are Deferred from
> v1.0.** See [`V1_SCOPE.md`](V1_SCOPE.md) and
> [`docs/KNOWN_LIMITATIONS.md`](docs/KNOWN_LIMITATIONS.md).

After setup, the app is fully operational and works **offline** for all
subsequent launches (unless you chose a hosted provider like OpenAI).

> **Internet required if you opted into voice**: voice backend assets
> (STT model, TTS engine, Python runtime) download from GitHub Releases on
> first use. After that, voice runs locally too.

## (Recommended) Verify checksum

From PowerShell in the folder containing the ZIP:

```powershell
Get-FileHash ".\sir-thaddeus-win-x64-v0.1.0.zip" -Algorithm SHA256
```

Compare the hash with the value in the accompanying `.zip.sha256.txt` file.

## Prerequisites

- **Windows 10/11**
- **Visual C++ Redistributable 2015-2022** — required by the speech-to-text engine.
  Most PCs already have this. If Whisper crashes, install it from [Microsoft](https://aka.ms/vs/17/release/vc_redist.x64.exe).
- **LM Studio** (or any OpenAI-compatible local LLM server)
   - Default expected base URL: `http://localhost:1234` or `http://localhost:1234/v1`
  - Tip: A known good model to start with is `qwen2.5-coder-7b-instruct` or similar instruction-tuned models.
- **Internet connection** on first run (for voice asset download)

## Validate first interaction

1. Send one normal chat message.
2. Trigger one approved tool action.
3. Confirm an audit entry was written (see paths below).

The MCP tool server (`SirThaddeus.McpServer.exe`) starts automatically as a child process when required.

If the package includes a bundled SearXNG sidecar under `search\`, web search can auto-start it on `http://localhost:8080` when `webSearch.mode` is `auto` or `searxng`.
The bundled SearXNG source and license notice are included under `search\source\searxng-upstream\` and `search\THIRD_PARTY_NOTICES.md`.

## Local data paths

- Runtime settings: `%USERPROFILE%\.thaddeus\runtime-settings.json`
- Runtime lock and turn traces: `%USERPROFILE%\.thaddeus\runtime.lock`, `%USERPROFILE%\.thaddeus\turns\`
- Runtime memory DB: `%USERPROFILE%\.thaddeus\memory.sqlite`
- Legacy settings and audit log: `%LOCALAPPDATA%\SirThaddeus\settings.json`, `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`

## Reset / delete local data

Close the app first, then remove any of these files for a clean reset:

- `%USERPROFILE%\.thaddeus\runtime-settings.json`
- `%USERPROFILE%\.thaddeus\memory.sqlite`
- `%USERPROFILE%\.thaddeus\turns\`
- `%LOCALAPPDATA%\SirThaddeus\settings.json`
- `%LOCALAPPDATA%\SirThaddeus\memory.db`
- `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`
- `%LOCALAPPDATA%\SirThaddeus\voicehost-session.json`
