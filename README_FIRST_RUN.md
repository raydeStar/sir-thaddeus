# Sir Thaddeus - First Run

This package is a self-contained Windows build. You do not need to install the .NET runtime.

## 1) Extract and launch

**What do I run?** Normal user flow is **one-step**: launch `SirThaddeus.DesktopRuntime.exe` only. Do not try to start MCP/VoiceHost manually.

1. Extract the ZIP to a local folder, for example `C:\Apps\SirThaddeus\`.
2. Open the extracted folder.
3. Double-click `SirThaddeus.DesktopRuntime.exe`.

### ⚠ Background Initialization (First Run)
Sir Thaddeus launches **instantly**, but the voice engine (ASR/TTS) initializes in the background. On your first run:
- **Status Banner**: A banner at the top of the chat window will show "Downloading models..." or "Starting voice backend...".
- **PTT Disabled**: The "Hold to Talk" button is disabled until this process completes.
- **Async Startup**: You can type messages and use other features immediately while the voice engine warms up.

If Windows SmartScreen appears, use **More info -> Run anyway** after you verify the source.
*Note: Unsigned binaries may trigger AV heuristics; verify checksum and source.*

## 2) (Recommended) Verify checksum

From PowerShell in the extracted parent folder:

```powershell
Get-FileHash ".\sir-thaddeus-win-x64-v0.1.0.zip" -Algorithm SHA256
```

Compare the hash with the value in the accompanying `.zip.sha256.txt` file.

## 3) Connect to LM Studio

1. Start LM Studio.
2. Start the local OpenAI-compatible server in LM Studio.
3. In the app settings, set `llm.baseUrl` to your local endpoint (default is usually `http://localhost:1234`).
   *(Tip: A known good model to start with is `qwen2.5-coder-7b-instruct` or similar instruction-tuned models).*
4. Save settings and run a quick test message.

## 4) Validate first interaction

1. Send one normal chat message.
2. Trigger one approved tool action.
3. Confirm an audit entry was written (see path below).

The MCP tool server (`SirThaddeus.McpServer.exe`) starts automatically as a child process when required.

## 5) Local data paths

- Settings: `%LOCALAPPDATA%\SirThaddeus\settings.json`
- Memory DB: `%LOCALAPPDATA%\SirThaddeus\memory.db`
- Audit log: `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`

## 6) Reset / delete local data

Close the app first, then remove any of these files if you want a clean reset:

- `%LOCALAPPDATA%\SirThaddeus\settings.json`
- `%LOCALAPPDATA%\SirThaddeus\memory.db`
- `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`
- `%LOCALAPPDATA%\SirThaddeus\voicehost-session.json`
