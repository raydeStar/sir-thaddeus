# Sir Thaddeus v0.3.0 — Manual QA Checklist

A walkthrough of every user-visible feature in the front end, organized so you can shuffle through and decide what to keep, polish, or cut. Branch: `task/fix-near-me-and-status-check-routing`.

> **Legend:** `(?)` = suspected wired but not verified end-to-end; *(Windows only)* = does not exist on macOS/Linux.

---

## 1. Chat & Conversation

- [ ] **Send message to assistant** — Type and submit a chat. *Trigger:* `/chat`, type, Enter. *Expect:* streaming reply. *File:* [chat.$threadId.tsx](web/src/routes/chat.$threadId.tsx)
- [ ] **Create new thread** — Start a fresh conversation. *Trigger:* "+" / "New Conversation" in `/chat`. *Expect:* new thread in sidebar.
- [ ] **Auto-title on first message** — Untitled threads get a 5–8 word summary derived from your first message. *Trigger:* send any first message. *Expect:* sidebar title updates from "Untitled".
- [ ] **Rename thread** — Edit a conversation title. *Expect:* updates everywhere immediately.
- [ ] **Pin / unpin thread** — Right-click or use the thread menu. *Expect:* pinned items sort to top.
- [ ] **Delete thread** — Remove conversation from list and storage.
- [ ] **Streaming text response** — Reply appears incrementally, not all at once. *Edge case:* watch for visible chunk lag.
- [ ] **Conversation history persistence** — Refresh the browser; messages survive. Navigate away and back; thread state is preserved.
- [ ] **Source cards with rich metadata** — Featured + standard cards render below replies that used `web_search`, with favicons, thumbnails, excerpts. *Trigger:* "what's the weather in Portland" or any web-search-driven query. *File:* [SourceCards.tsx](web/src/components/SourceCards.tsx)
- [ ] **Memory-aware greetings** — First message of a new thread can pull shallow profile + memo context. *Trigger:* set "About Me" in Settings, start a new thread, ask a greeting. *Expect:* assistant references something it knows without you re-stating it.
- [ ] **Tool-call display** — When the agent calls a tool, the call/result is visible in the thread (not silent).

---

## 2. Voice *(Windows only)*

- [ ] **Push-to-talk hotkey** — Hold the configured key to record. *Expect:* recording indicator, transcribed text on release. *File:* [WindowsGlobalShortcutAdapter.cs](src/Thaddeus.Shell/Platform/Windows/WindowsGlobalShortcutAdapter.cs)
- [ ] **Real-time STT transcription** — Speech is converted to text as you speak (Piper backend).
- [ ] **TTS playback** — Replies are spoken aloud when voice is on. *Trigger:* enable voice in Settings → Audio & Voice, send a prompt.
- [ ] **Voice settings (voice / speed / volume)** — Settings → Audio & Voice. *Expect:* dropdown of Piper voices; sliders apply to next playback.
- [ ] **VoiceHost health check** — Status indicator for the voice sidecar. *Expect:* "Connected" or "Unavailable" with green/red.
- [ ] **Disable voice mode** — Toggle in Settings → General. *Expect:* PTT inert; TTS silent. Re-enable restores both.

---

## 3. Tools (MCP)

### 3a. Web & Browser

- [ ] **Web search with auto-read** — Searches the web and summarizes top results. *Trigger:* current-info questions ("latest news on X"). *File:* `packages/mcp-tools-core/WebSearchTools.cs`
  - *Edge case:* weather questions should route to `weather_geocode` first, NOT `web_search`.
  - *Edge case:* recency tunes per query type — day for news, week for prices, any for stable reference.
- [ ] **Read single web page** — Fetch + extract from a specific URL. *Trigger:* "read this page: [URL]".
- [ ] **Web search categories (general / news)** — Scope to news sources for current events.

### 3b. Location & Places

- [ ] **Weather forecast** — Geocode then forecast pipeline. *Trigger:* "weather in Portland tomorrow".
  - *Edge case:* accepts `location` and `place` aliases; flat and nested coords (small models emit either).
- [ ] **Places discovery (near me)** — Find bakeries / parks / gas stations nearby. *Trigger:* "parks near me". *Note:* recently fixed near-me location guard — verify it actually uses your location and doesn't refuse.
- [ ] **Places deep-dive lookup** — Hours, reviews, phone, address for a specific business. *Trigger:* "is Trader Joe's open?".
- [ ] **Status check routing** — "Is X open right now" routes to places lookup, not web search. *(recently fixed — verify.)*
- [ ] **Retailer availability fast path** — Deterministic fallback when site search returns nothing. *Trigger:* "is X in stock at [retailer]". *(recent commit a7c912c.)*
- [ ] **Retailer stock-price fact fast path** — Direct price extraction. *Trigger:* "what's [stock] price". *(recent commit e4a914d.)*

### 3c. Memory & Knowledge

- [ ] **Memory retrieval (conversation-scoped)** — Recalls relevant facts during a conversation.
- [ ] **Memory append** — "Remember that I like hiking" stores a fact.
- [ ] **User profile card** — About-Me / display name flows into greetings.

### 3d. Files & Documents

- [ ] **File read (multi-format)** — PDF, DOCX, XLSX, CSV, RTF, Markdown, plain text, JSON, source code. *Trigger:* "read this file: [path]". *Edge case:* 10 MB cap; allowed-roots only; truncates at default 4000 chars.
- [ ] **File list with preview** — Browse directories. *Trigger:* "what files are in my Documents folder".

### 3e. System & Utilities

- [ ] **System command (allowlist)** — `whoami`, `hostname`, `date`, `systeminfo`, `dotnet`. *Edge case:* shell metacharacters (`& | > < ; \` $`) blocked.
- [ ] **Time / date utilities** — Offline answer, no web call. *Trigger:* "what time is it" / "today's date".
- [ ] **Holiday calendar lookup** — "Is today a holiday?" / "When is Thanksgiving?".
- [ ] **Math / unit conversion** — Advanced math engine + structured lookups. *Trigger:* "convert 50 mph to km/h" / "integral of x^2".

### 3f. Clipboard *(Windows only)*

- [ ] **Clipboard read** — "What's on my clipboard?".
- [ ] **Clipboard write** — "Copy this to my clipboard: [text]". *Expect:* paste works elsewhere.

### 3g. Screen Reading *(Windows only)*

- [ ] **Layered screen capture** — UIA tree → browser URL extraction → HTTP page read → OCR fallback. *Trigger:* "what's on my screen". *Edge case:* 30s hard timeout; OCR text capped at 8000 chars.
- [ ] **Full-screen capture** — Entire monitor, not just active window. *Trigger:* "show me the full screen".

### 3h. Imperative Tool Selection

- [ ] **"use web_search" / "try file_read" honors user choice** — Small models won't fabricate "I can't do that" when you explicitly name a tool. *(recent commit b75e098.)*

---

## 4. Permissions & Safety

- [ ] **Permission prompt modal** — Modal with tool name + reason + (Deny / Once / Session / Always).
- [ ] **Time-boxed tokens** — "Session" tokens expire on restart; verify by granting Session, restarting, expecting re-prompt.
- [ ] **Deny** — Tool does not run; clear error message.
- [ ] **Grant once** — Runs once; next call prompts again.
- [ ] **Grant session** — Runs without prompt for the rest of the session.
- [ ] **Grant always** — Runs without prompt forever (until revoked).
- [ ] **STOP kill switch** — Red STOP button or hotkey halts everything and revokes all permissions. *Expect:* audit log records `PERMISSION_REVOKE_ALL`. *(README brand promise — must work.)*
- [ ] **Audit log viewer** — `/activity` lists every tool call, permission decision, outcome.
- [ ] **Activity detail** — Click an entry to see arguments and result.
- [ ] **Pending permissions list** — Outstanding prompts visible somewhere accessible.
- [ ] **Tool budgets** — Runaway loops are bounded (no infinite tool spirals).
- [ ] **Panic mode toggle (?)** — All tools require explicit approval. Verify it's actually wired to a UI control, not just an internal API.

---

## 5. Routines

- [ ] **Seeded templates on first boot** — 5 default routines: Morning Launch, Evening Shutdown, Fitness Check-In, Project Focus, Weekly Review. *Expect:* present on first launch; not overwritten after edit.
- [ ] **List routines** — `/routines` shows all with name, description, enabled state.
- [ ] **Create routine** — New routine form. *Expect:* appears in list.
- [ ] **Edit routine** — Modify name, description, items.
- [ ] **Delete routine** — Cascade-deletes runs.
- [ ] **Enable / disable** — Disabled routines hide from default list.
- [ ] **Run a routine** — Click Run; checklist appears; check items off.
- [ ] **Run history** — Past runs with timestamps and completion status; sealed-run immutability.
- [ ] **No background fire** — Leave the app idle; nothing runs automatically. *(This is a brand promise — Routines is explicitly user-invoked. A meta-test asserts no `IHostedService` was added.)*

---

## 6. Memory (Memos)

- [ ] **Create memo** — Title + body + optional comma-separated tags. *Expect:* appears in list.
- [ ] **List / browse memos** — `/memory` shows all, sorted by pin then date.
- [ ] **Tag memos** — Comma-separated; tags appear on card.
- [ ] **Pin memo** — Pinned memos sort to top.
- [ ] **Edit memo** — Title / body / tags.
- [ ] **Delete memo** — Removed from store.
- [ ] **Automatic recall in chat** — Save a memo, ask a related question in a *new* thread, expect agent to reference it.

---

## 7. Profiles & Personalities

- [ ] **User profile (display name, alias, about-me)** — Settings → General. *Expect:* greeting prompt reflects identity.
- [ ] **Load AI personality** — Pre-built or user-defined.
- [ ] **Create custom personality** — Name, description, system prompt.
- [ ] **Import personality** — From JSON file.
- [ ] **Export personality** — Download JSON.

---

## 8. Settings & Configuration

- [ ] **Settings tabs render** — General, Models, Audio & Voice, Files, Location, Advanced.
- [ ] **General tab** — Name, display name, theme (light / dark), about-me.
- [ ] **Models tab** — Provider, base URL, key (masked), model name.
- [ ] **LM provider presets** — LM Studio / Ollama / OpenAI / Custom auto-fill base URL; key only required where appropriate.
- [ ] **Test LLM connectivity** — "Test Connection" returns model list or clear error.
- [ ] **Audio & Voice tab** *(Windows)* — Piper voice dropdown, speed, volume.
- [ ] **Files tab** — Allowed file roots; add / remove.
- [ ] **Location tab** — Default city / coords for weather and places fallback.
- [ ] **Advanced tab** — SearXNG URL, web search mode, cache TTL.
- [ ] **Save / apply** — Changes persist across restart.
- [ ] **Safe mode recovery** — Corrupt `settings.json` → boot with defaults → flag clears on successful load and persists cleared. *(recent fix cde8c05.)*

---

## 9. Tray & Shell *(Windows only)*

- [ ] **Tray icon present** — Custom branded icon, not generic. *(recent commit 87a82aa.)*
- [ ] **"At your service, sir"** — Open / restore workspace from tray.
- [ ] **"Stand down"** — Stop-all from tray (same as in-app STOP).
- [ ] **"Dismiss"** — Exit cleanly.
- [ ] **Minimize-to-tray** — Closing main window hides to tray, doesn't exit.
- [ ] **Global PTT hotkey works while app is in background.**

---

## 10. Diagnostics & Observability

- [ ] **Runtime version badge** — Sidebar shows `v0.3.0` (release) or `dev` (local). Hover for full version.
- [ ] **Diagnostics page** — `/diagnostics` shows state, uptime, thread count, voice availability, build version, PID.
- [ ] **Refresh button** — Re-fetches and uptime increments.
- [ ] **Runtime state snapshot** — `Idle | RequestingPermission | Processing | Stopping`.
- [ ] **Startup diagnostics fire** — On launch, advisory results for `llm.reachable`, `voicehost.reachable`, `logs.writable`. *Expect:* visible in startup log; never blocks startup.
- [ ] **Logs path discoverable** — User can find `%LocalAppData%\SirThaddeus\logs\` from Settings or docs.
- [ ] **Status pill / connection indicator** — Top-bar pill reflects connected / disconnected / error states. *(recent commit ead8d7c.)*

---

## 11. Onboarding

- [ ] **First-run onboarding flow** — 4-step: welcome → privacy → voice → done.
- [ ] **Configure LLM during onboarding** — Provider + endpoint test.
- [ ] **Skip voice setup** — Completes without enabling voice.
- [ ] **Onboarding-completed flag persists** — Doesn't repeat on next launch.

---

## 12. Headless Runtime (Terminal — separate test path)

Launch with `dotnet run --project apps/headless-runtime/SirThaddeus.HeadlessRuntime` or `./dev/terminal.ps1`.

- [ ] **`/help`** — Lists all commands.
- [ ] **`/reset`** — Clears memory and context.
- [ ] **`/tools`** — Reports detected MCP tool count.
- [ ] **`/whoami`** (alias `/w`) — Shows user + assistant identity.
- [ ] **`/quickstart`** — Common workflow examples.
- [ ] **`/exit`** — Clean exit.
- [ ] **`--tools` flag** — `dotnet run -- --tools` loads MCP tools.
- [ ] **Profile-aware prompt** — Prompt reads `preferred_name` from shared profile store (e.g. `mark <-> sir-thaddeus`).
- [ ] **`/profile user` subcommands** — show, set-alias, set-display-name, set-about-me.
- [ ] **`/profile thaddeus` subcommands** — show, load, create, set-alias, export, import.
- [ ] **`/undo`** — Restores most recent profile / settings change.

---

## 13. Runtime Management API (light touch — verify they don't 500)

- [ ] **`GET /api/health`** — `{ status, version, pid, startedAt }`.
- [ ] **`GET /api/runtime-info`** — `managedByShell` flag distinguishes shell-spawned vs standalone.
- [ ] **`POST /api/runtime/stop`** — Graceful shutdown when shell-managed.

---

## Known Limitations

- **(?) "Deeper thinking" / extended reasoning mode** — referenced in code; verify there's a UI control, not just an internal flag.
- **Knowledge graph visualization** — not implemented; no UI route for it.
- **Multi-language UI** — English only.
- **Mobile / tablet support** — not designed for; Windows desktop only for full experience.

## Removed in v0.3.0 (don't expect these)

- **Automations / scheduled background agents** — ripped out and replaced with user-invoked **Routines**. If you find any UI element labeled "Automations" or any route that 404s, that's a leftover bug.

---

## Tester Notes

1. Complete onboarding on first run to set up LLM and (optional) voice.
2. Every tool call requires explicit approval — Deny / Once / Session / Always.
3. STOP must always halt execution and revoke permissions immediately. This is a brand promise.
4. Voice (PTT + TTS) is **Windows only** at v0.3.0.
5. All data stays local. No telemetry.
6. Logs: `%LocalAppData%\SirThaddeus\logs\{component}\` — daily rolling JSON.
7. If `settings.json` is corrupted, app boots with defaults; safe-mode flag self-clears on next successful load.
