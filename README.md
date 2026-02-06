# Sir Thaddeus

Local-first Windows agent runtime: **desktop UI is optional**, the runtime is designed to run **tray-only/headless**, talk to a **local LLM (LM Studio)**, and execute actions through an **MCP tool server**.

## What exists right now (high signal)

- **Layered architecture is in place**: Frontend (WPF/tray/hotkeys) → Agent orchestrator → LLM client (LM Studio) → MCP server (stdio JSON-RPC).
- **Command Palette is agent-driven**: typed requests go to the LLM; tool calls flow through MCP.
- **Headless mode works**: `--headless` starts without the overlay window (tray + hotkeys + background agent still run).
- **PTT → agent → TTS pipeline is wired**: transcription is still a placeholder, but the end-to-end pipeline is testable.
- **Audit log is always-on**: `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`.

## Architecture (4 layers)

```mermaid
flowchart LR
  subgraph frontend [Layer 1: Frontend (apps/desktop-runtime)]
    Tray[System Tray]
    Overlay[WPF Overlay (optional)]
    PTT[Push-to-Talk]
    TTS[Text-to-Speech]
    Palette[Command Palette]
  end

  subgraph agent [Layer 2: Agent Orchestrator (packages/agent)]
    Loop[Agent Loop]
    Context[Conversation History]
  end

  subgraph llm [Layer 3: LLM Client (packages/llm-client)]
    LmStudio[LM Studio (OpenAI-compatible)]
  end

  subgraph mcp [Layer 4: MCP Tool Server (apps/mcp-server)]
    Server[MCP Server (stdio)]
    Tools[Tools: BrowserNavigate / FileRead / FileList / SystemExecute / ScreenCapture]
  end

  PTT -->|audio file (placeholder today)| Loop
  Palette -->|typed request| Loop
  Loop -->|chat + tools| LmStudio
  LmStudio -->|tool_calls| Loop
  Loop -->|tools/call| Server
  Server -->|tool result| Loop
  Loop -->|final text| TTS
  Loop -->|events| Overlay
  Tray --> Overlay
```

### Layer responsibilities (sanity check)

| Layer | Project(s) | Responsibility | Talks to |
|---|---|---|---|
| **Frontend** | `apps/desktop-runtime` | Hotkeys, tray, overlay, PTT capture trigger, TTS output | Agent orchestrator (in-process) |
| **Agent** | `packages/agent` | Conversation loop + tool execution orchestration | LLM client + MCP client |
| **LLM client** | `packages/llm-client` | OpenAI-style `/v1/chat/completions` calls | LM Studio HTTP server |
| **MCP server** | `apps/mcp-server` | Exposes tools over MCP stdio | Desktop runtime (child process) |

## Project structure

```
sir-thaddeus/
├── apps/
│   ├── desktop-runtime/              # WPF overlay + tray + hotkeys + PTT + TTS
│   └── mcp-server/                   # MCP tool server (stdio)
├── packages/
│   ├── agent/                        # Agent orchestration loop
│   ├── llm-client/                   # LM Studio / OpenAI-compatible client
│   ├── config/                       # %LOCALAPPDATA% settings.json management
│   ├── core/                         # State machine, runtime controller
│   ├── audit-log/                    # JSONL audit logging
│   ├── permission-broker/            # Time-boxed permission token management (legacy path)
│   ├── tool-runner/                  # Tool execution w/ permission enforcement (legacy path)
│   ├── invocation/                   # Legacy regex command planning/execution
│   ├── observation-spec/             # Observation spec schema + validation
│   └── local-tools/
│       └── Playwright/               # Playwright browser tool (not MCP-wired yet)
└── tests/                            # Unit tests
```

## Prerequisites

- **Windows 10/11**
- **.NET 8 SDK**
- **LM Studio** running a local server (OpenAI-compatible)
  - Default expected base URL: `http://localhost:1234`
  - Endpoint used: `/v1/chat/completions`

## Configuration

On first run, the desktop runtime creates:

- **Settings**: `%LOCALAPPDATA%\SirThaddeus\settings.json`
- **Audit log**: `%LOCALAPPDATA%\SirThaddeus\audit.jsonl`
- **PTT audio folder**: `%LOCALAPPDATA%\SirThaddeus\audio\`

Example settings file:

```json
{
  "llm": {
    "baseUrl": "http://localhost:1234",
    "model": "local-model",
    "maxTokens": 2048,
    "temperature": 0.7,
    "systemPrompt": "You are a helpful assistant with access to local tools. Use tools when needed. Be concise."
  },
  "audio": {
    "pttKey": "F13",
    "ttsEnabled": true
  },
  "ui": {
    "startMinimized": false,
    "showOverlay": true
  },
  "mcp": {
    "serverPath": "auto"
  }
}
```

Notes:
- `audio.pttKey` supports `F1`..`F24` or hex virtual keys like `0x7C`.
- `mcp.serverPath = "auto"` resolves to the built `SirThaddeus.McpServer.exe` in the repo output folders.

## Building & tests

```powershell
dotnet build
dotnet test
```

## Running

### Desktop runtime (overlay + tray)

```powershell
dotnet run --project apps/desktop-runtime/SirThaddeus.DesktopRuntime
```

### Headless (tray + hotkeys, no overlay window on startup)

```powershell
dotnet run --project apps/desktop-runtime/SirThaddeus.DesktopRuntime -- --headless
```

You can still show the overlay later from the tray menu.

### MCP server standalone (for inspection)

```powershell
dotnet run --project apps/mcp-server/SirThaddeus.McpServer
```

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+Space` | Open Command Palette |
| `F13` (default; configurable via settings) | Push-to-Talk trigger (keyboard hook) |

## Sanity check: does the architecture behave like we expect?

1. **Start LM Studio** and ensure the local server is running.
2. Run the desktop runtime (normal or headless).
3. Open `%LOCALAPPDATA%\SirThaddeus\audit.jsonl` and confirm startup events:
   - `APP_STARTUP`
   - `LLM_CLIENT_CREATED`
   - `MCP_SERVER_STARTED` and `MCP_SERVER_INITIALIZED` (if MCP server path resolved correctly)
4. Press `Ctrl+Space` → in the command palette, try a tool-forcing prompt:
   - `Use SystemExecute to run 'whoami' and paste the output.`
5. Headless-only quick check:
   - Run with `--headless` and confirm **no overlay window** appears at startup.
   - Tray icon exists; `Ctrl+Space` still opens the palette.
   - Hold/release the PTT key once and confirm you get a spoken response (TTS).

## MCP tools exposed today

The MCP server currently exposes:

- `BrowserNavigate(url)` — HTTP fetch + excerpt (Playwright is available in the repo but not wired through MCP yet)
- `FileRead(path)` — reads up to 1MB
- `FileList(path)` — lists up to 100 entries
- `SystemExecute(command)` — **allowlisted commands only**
- `ScreenCapture(target)` — stub (returns acknowledgement)

## Known gaps (intentionally called out)

- **Transcription**: PTT currently creates a minimal WAV placeholder; Whisper transcription is not integrated yet.
- **Permission enforcement**: the legacy ToolRunner path has explicit tokens/prompts, but MCP tool calls are not yet gated by those tokens.
- **Screen capture**: stub only.
- **Playwright via MCP**: Playwright tool exists, but MCP uses a simpler HTTP navigation tool for now.

## More docs

See [project-notes/architectural-design.md](project-notes/architectural-design.md).

## License

See LICENSE file.
