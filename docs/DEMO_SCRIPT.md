# Golden Demo Script

This is the 3-5 minute v1 demo. It shows the core hybrid product surface only: shell, local workspace, model settings, chat, MCP permissioning, tool activity, wiki/canvas, diagnostics, and stop controls.

Do not use this demo to showcase beta or deferred features. If a step in this script breaks, fix the bug — do **not** rewrite the demo to avoid it.

## Pre-Demo Checklist

Run this **at most 30 minutes before** the demo, on the demo machine.

- [ ] Build or install a current package.
- [ ] Confirm no stale `Thaddeus.Runtime.exe`, `Thaddeus.Shell.exe`, `SirThaddeus.McpServer.exe`, or `SirThaddeus.VoiceHost.exe` processes are running (`Get-Process Thaddeus*`).
- [ ] Start LM Studio, Ollama, or another OpenAI-compatible endpoint if using a real model.
- [ ] Confirm the endpoint is reachable:
  - LM Studio: `http://127.0.0.1:1234/v1`
  - Ollama OpenAI shim: `http://127.0.0.1:11434/v1`
- [ ] Prepare a local demo folder with one harmless text or Markdown file if web search is flaky.
- [ ] Reset or review permission policy so the demo will show at least one prompt.
- [ ] Network is reachable (web search step needs it).
- [ ] Browser zoom is 100%; window is at least 1280x800.
- [ ] Audio is muted unless the voice section is being demoed (it is **not** in the golden script — voice is Beta).
- [ ] Demo prompts are pasted into a scratch buffer for fast copy.
- [ ] Open [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) in case you need to explain beta/deferred boundaries.

## Demo Arc

| Time | Step | What To Say | What To Show |
| --- | --- | --- | --- |
| 0:00-0:30 | Launch from shell | "Sir Thaddeus v1 starts as a local shell that supervises a loopback runtime and opens a local workspace." | Run `dotnet run --project src/Thaddeus.Shell/Thaddeus.Shell.csproj` from source, or launch `Thaddeus.Shell.exe` from a package if available. |
| 0:30-0:50 | Show workspace/runtime | "The UI is a React workspace served by the local runtime. API access is loopback and token-gated." | Show the workspace home, runtime state badge, and navigation. Hover the version pill. |
| 0:50-1:15 | Confirm model settings | "The model endpoint is explicit. LM Studio, Ollama, and other OpenAI-compatible endpoints use the same settings surface." | Open Settings → Models, show provider/base URL/model ID, click **Test connection**, show the returned model list. |
| 1:15-1:45 | Start chat | "Chat is threaded and streams assistant output." | Open Chat, create a new thread. |
| 1:45-2:30 | Ask for a tool | "Now I will ask for something that requires an MCP tool, so the app has to ask before acting." | Send a primary or fallback prompt from the section below. |
| 2:30-3:00 | Permission prompt | "The permission prompt shows what access is requested and lets me approve once, for the session, always, or deny." | Read the four verbs aloud. Approve once or for the session. Avoid always during the public demo. |
| 3:00-3:30 | Streamed answer and tool activity | "The answer streams back while tool activity stays visible. Source cards land below the reply." | Show assistant response, tool-activity pills, and source cards inline. |
| 3:30-4:10 | Save into wiki/canvas | "Useful output can become durable local knowledge." | Open Wiki, create a page such as `Demo Notes`, paste the useful summary into it, save. |
| 4:10-4:35 | Wiki assistant action | "Wiki pages have assistant actions for page chat, draft, and selected-text rewrite." | Select a paragraph, choose **Rewrite → Tighten** (or Clarify). The selection is replaced. Show the revisions dropdown briefly. |
| 4:35-4:50 | Activity/diagnostics | "Every turn is auditable. Logs path is one click away." | Open Activity → click the latest entry. Open Diagnostics — point at state, uptime, build version, PID, **Logs path**. |
| 4:50-5:00 | Stop-all/kill | "If anything ever feels wrong — runaway tool loop, model talking to itself, whatever — this is the kill." | Hover the red kill switch in the header. Don't click during a live demo. |

## Primary Demo Prompts

Use one of these when the network and provider are healthy:

```text
What's the latest stable release of .NET? Cite a source.
```

```text
Search the web for the current status of LM Studio local server support. Summarize what matters for running Sir Thaddeus with a local OpenAI-compatible endpoint, and include sources.
```

```text
What's the weather in Olympia, WA tomorrow?
```

*(routes to `weather_geocode` → `weather_forecast`, not `web_search`)*

## Fallback Prompts

Use these if live web/search is flaky or if you want a fully local demo:

```text
Read README.md from this repository and summarize the v1 product promise, the beta features, and the deferred features. Ask for permission before reading files.
```

```text
Read docs/FEATURE_GAP_MATRIX.md and turn it into a five-bullet release review summary. Ask for permission before reading files.
```

```text
Look at docs/KNOWN_LIMITATIONS.md and draft a short, honest release note paragraph that explains what v1 is and is not.
```

```text
What time is it?
```

*(offline tool — no permission prompt, useful when permission system is already saturated)*

```text
Convert 50 mph to km/h.
```

*(offline math)*

## Wiki Assistant Examples

After saving demo output into a wiki page, use one of these:

```text
Rewrite the selected paragraph so it is more direct and suitable for a release note.
```

```text
Draft a short checklist from this page for someone validating the v1 release.
```

```text
Answer this from the page only: what are the v1 release boundaries?
```

## What Not To Show

- Voice, ASR, TTS, or push-to-talk as a core v1 promise.
- Tray integration or global shortcuts as fully validated everywhere.
- Compact panel beyond its beta/minimal state.
- Scheduled or unattended automation.
- Profile/personality administration in the v2 workspace.
- Installer polish or auto-update.
- Cross-platform desktop parity.
- Settings → Advanced → Limits — saved-but-not-yet-enforced; the help text says so but it raises the wrong question for a public demo.
- `/settings/$category` URLs of any kind. Use the in-page tabs.
- Any prompt that requires private data, credentials, or destructive file actions.

## Recovery Notes

The goal is to demo the **trust loop**, not to demo perfection. Acceptable recoveries:

- **Permission modal doesn't fire** → you probably already chose **Always** for that tool. Mention it, move on.
- **Web search returns nothing** → switch to a fallback prompt. Don't retry the failing prompt.
- **Streaming stalls** → click the kill switch, restart the runtime, narrate the recovery ("This is what stop looks like in practice"), then continue from the chat step.
- **Real model is not responding** → switch to a simpler prompt or use the stub assistant to show the workspace and permission model.
- **Permission was previously allowed** → reset policy or choose a different tool group so the prompt appears.
- **Voice sidecars start unexpectedly** → explain that voice is Beta and keep the demo focused on chat/tools/wiki.

Unacceptable: pretending the runtime is fine when it isn't, or running manual fix-up commands in a terminal during the demo.

## The 15-Second GIF (For The README)

For social/discovery use, you also want a 15-second GIF, not the full 5-minute demo. Recommended frame-by-frame:

| Time | Frame |
|---|---|
| 0-3s | Type a prompt: *"What's the latest stable release of .NET? Cite a source."* |
| 3-5s | Reply starts streaming. Permission modal pops up: **"Allow web_search? Reach out to the internet."** |
| 5-8s | Cursor hovers the four buttons. The verbs are the demo: **Deny · Once · Session · Always.** |
| 8-12s | Click "Once". Source cards stream in inline. |
| 12-15s | Cursor swings up to the red kill switch. End on it. |

Captions (so it works on mute):
- "Most agents do this silently."
- "This one asks first."
- "And stops when you say stop."

Drop the GIF into `assets/images/sir-thaddeus-demo.gif` and reference it in the README hero.
