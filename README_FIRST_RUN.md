# Sir Thaddeus - First Run

This package is a self-contained Windows build. You do not need to install the .NET runtime.

## Quick start: Download, Unzip, Run

1. **Download** the release ZIP from the [Releases](https://github.com/raydeStar/sir-thaddeus/releases) page.
2. **Unzip** to a local folder, for example `C:\Apps\SirThaddeus\`.
3. **Run** `SirThaddeus.DesktopRuntime.exe`.

That's it. Everything else happens automatically.

> If Windows SmartScreen appears, click **More info -> Run anyway** after verifying the source.
> *Unsigned binaries may trigger AV heuristics; verify the checksum (see below).*

## What happens on first run

The **Setup Wizard** walks you through four steps:

1. **Connect your LLM** — the app scans for running local LLM servers (LM Studio, Ollama, etc.) and lets you pick one or enter a custom URL.
2. **Set your name & context** — optional operator alias and system context.
3. **Pick a personality** — choose the assistant's behavioral profile.
4. **Voice asset download** — while you go through steps 1-3, voice backend assets (~320 MB) download in the background from GitHub Releases. If the download isn't done by step 3, you'll see a progress bar. If it finishes early, this step is skipped automatically.

After setup, the app is fully operational and works **offline** for all subsequent launches.

> **Internet required**: An internet connection is needed on first run to download voice assets (STT model, TTS engine, Python runtime). After that, everything runs locally.

## (Recommended) Verify checksum

From PowerShell in the folder containing the ZIP:

```powershell
Get-FileHash ".\sir-thaddeus-win-x64-v0.1.0.zip" -Algorithm SHA256
```

Compare the hash with the value in the accompanying `.zip.sha256.txt` file.

## Prerequisites

- **Windows 10/11**
- **LM Studio** (or any OpenAI-compatible local LLM server)
  - Default expected base URL: `http://localhost:1234`
  - Tip: A known good model to start with is `qwen2.5-coder-7b-instruct` or similar instruction-tuned models.
- **Internet connection** on first run (for voice asset download)

## Validate first interaction

1. Send one normal chat message.
2. Trigger one approved tool action.
3. Confirm an audit entry was written (see paths below).

The MCP tool server (`SirThaddeus.McpServer.exe`) starts automatically as a child process when required.

## Local data paths

- Settings: `%LOCALAPPDATA%\SirThaddeus\settings.json`
- Memory DB: `%LOCALAPPDATA%\SirThaddeus\memory.db`
- Audit log: `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`

## Reset / delete local data

Close the app first, then remove any of these files for a clean reset:

- `%LOCALAPPDATA%\SirThaddeus\settings.json`
- `%LOCALAPPDATA%\SirThaddeus\memory.db`
- `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`
- `%LOCALAPPDATA%\SirThaddeus\voicehost-session.json`
