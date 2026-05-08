# Sir Thaddeus — Golden demo script

**Length:** 3–5 minutes.
**Goal:** Show the v1 trust loop end-to-end — local model, permission boundary,
visible tool activity, durable wiki, stop control. No surprises, no recovery.

If a step in this script breaks, fix the bug — do **not** rewrite the demo
to avoid it.

---

## Pre-demo checklist

Run this **at most 30 minutes before** the demo, on the demo machine.

- [ ] LM Studio (or your chosen OpenAI-compatible server) is running.
      Confirm a chat-capable model is loaded.
- [ ] No leftover `Thaddeus.Runtime` or `Thaddeus.Shell` processes are running
      (`Get-Process Thaddeus*` on Windows).
- [ ] `~/.thaddeus/` exists and is writable. Optional: archive it for a
      genuinely-empty demo.
- [ ] Network is reachable (web search step needs it).
- [ ] Audio is muted unless the voice section is being demoed (it is **not**
      in the golden script — voice is Beta).
- [ ] Browser zoom is 100%; window is at least 1280×800.
- [ ] Demo prompts are pasted into a scratch buffer for fast copy.

---

## The arc

Twelve steps, paced so the room sees the value of each. Don't editorialize
between steps — let the UI do the talking.

### 1. Launch from the shell *(20 seconds)*

```
dotnet run --project src/Thaddeus.Shell
```

> "Sir Thaddeus is a desktop workspace. The shell starts a local runtime, then
> opens the workspace UI."

The shell prints the loopback URL. The webview opens. Workspace renders.

### 2. Show the runtime briefly *(15 seconds)*

Click the runtime-state badge in the header (top right). Hover the version
pill in the sidebar.

> "Everything runs on `127.0.0.1`. The runtime is a single binary, the UI
> talks to it over loopback with a per-launch bearer token. Nothing is
> reachable from the network."

### 3. Confirm the model *(20 seconds)*

Sidebar → **Settings** → **Models**.

Show the provider preset (LM Studio), base URL, the **Test connection**
button. Click **Test connection**. Show the model list returned.

> "We're talking to whatever local model the user already runs."

### 4. Start a chat *(10 seconds)*

Sidebar → **Home** or **Chat** → **+ New chat**. Type the first prompt.

### 5. Ask for something that needs a tool *(15 seconds)*

**Primary prompt:**

```
What's the latest stable release of .NET? Cite a source.
```

This forces `web_search`. Send.

### 6. Permission prompt *(15 seconds)*

The permission modal appears. Read the title aloud:

> "Allow `web_search`? Reach out to the internet."

Show the four options in order of escalation: **Deny / Allow once / For session / Always**.

### 7. Approve *(5 seconds)*

Click **For session**. The decision persists for the rest of the run.

### 8. Stream the answer + show tool activity *(45 seconds)*

The reply streams. Above it, tool-activity pills show `web_search` started
and finished. Below it, source cards render with favicons and excerpts.

> "Every tool call is on the message that triggered it. If you ever wonder
> what made the model say something, it's right here."

### 9. Move output into the wiki *(30 seconds)*

Sidebar → **Wiki** → **+ New page**. Paste a paragraph from the chat reply.
Save.

> "The wiki is local, durable, versioned. This isn't ephemeral chat memory —
> it's notes that survive restart and that the assistant can read."

### 10. Wiki assistant action *(45 seconds)*

In the new wiki page, select two or three sentences. Use **Rewrite** →
**Tighten** (or **Clarify**).

The selection is replaced with the assistant's tightened version. Show the
revision history dropdown briefly — every save is a revision you can roll back.

> "Same model, same permission boundary, same audit log. The wiki just gets
> the same agentic surface as chat."

### 11. Activity & diagnostics *(20 seconds)*

Sidebar → **Activity** → click the latest entry.

Show: kind (`ChatTurn`), status (`Ok`), thread link, started/completed times,
detail.

Sidebar → **Diagnostics**. Show: state, uptime, thread count, voice
(probably "disabled"), build version, PID, **Logs path** (`~/.thaddeus/logs/`).

> "Every turn is auditable. The logs path is one click away — no hunting in
> AppData."

### 12. Stop control *(10 seconds)*

Header → red **kill switch**. Hover only — don't click during a live demo.

> "If anything ever feels wrong — runaway tool loop, model talking to itself,
> whatever — this is the kill. It tears down sidecars and exits the runtime."

End on this slide. Don't kill the runtime mid-demo unless the room asked.

---

## Demo prompts

### Primary (works with web search reachable)

- `What's the latest stable release of .NET? Cite a source.`
- `What's the weather in Olympia, WA tomorrow?` *(routes to weather, not web)*
- `Read this file: README.md` *(if a file root is allowlisted)*
- `Summarize the main idea of the file we just read into a wiki page.`

### Fallback (offline / web-search flaky)

If the demo machine cannot reach the internet:

- `What time is it?` *(offline tool)*
- `Convert 50 mph to km/h.` *(offline math)*
- `Read this file: docs/ARCHITECTURE_PUBLIC.md and tell me the layers.`
- `Open a new wiki page and draft a short summary of what you just read.`

These exercise: tool boundary, permission prompt, file allowlist, wiki
draft action — without depending on the network.

---

## What not to show

- **Voice / push-to-talk.** Voice is Beta; PTT depends on global hotkey
  registration that is finicky to demo on a borrowed machine.
- **Tray integration.** Same — depends on Windows shell state.
- **Compact panel.** Phase-2 stub. Skip.
- **Profile / personality admin.** Deferred from v1.
- **Settings → Advanced → limits.** Saved but not yet enforced; the help
  text already says this, but it raises the wrong question.
- **`/settings/$category` URLs** of any kind. Use the in-page tabs.

---

## If something breaks live

The goal is to demo the **trust loop**, not to demo perfection. Acceptable
recoveries:

- **Permission modal doesn't fire** → you probably already chose **Always**
  for that tool. Mention it, move on.
- **Web search returns nothing** → switch to a fallback prompt. Don't retry
  the failing prompt.
- **Streaming stalls** → click the kill switch, restart the runtime, narrate
  the recovery. ("This is what stop looks like in practice.") Then continue
  from step 5.

Unacceptable: pretending the runtime is fine when it isn't, or running
manual fix-up commands in a terminal during the demo.
