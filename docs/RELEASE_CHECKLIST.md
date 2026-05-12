# v1 Release Checklist

Use this checklist before declaring Sir Thaddeus v1 ready. Mark skipped checks with the reason, especially when GPU, local model, voice sidecar, live web, or desktop interaction resources are unavailable.

> **Pass criterion:** every required box checked. Beta items may be skipped if explicitly noted, but they must be **explicitly skipped**, not silently.

## Clean Clone And Build

- [ ] Clone the repo into a clean directory.
- [ ] Confirm .NET SDK from [global.json](../global.json) is available.
- [ ] Confirm Node.js and npm are available.
- [ ] Run `dotnet restore SirThaddeus.sln`.
- [ ] Run `Push-Location web; npm ci; npm run build; Pop-Location`.
- [ ] Run `dotnet build SirThaddeus.sln -c Release --no-restore`.
- [ ] Run `Push-Location web; npm run typecheck; npm run lint; Pop-Location`.
- [ ] Run `pwsh dev/test.ps1 -Configuration Release -SkipScreenObserveHarness`.
      Expected: **0 failed**; the exact pass count changes as tests are added or retired.
- [ ] *(optional)* If `artifacts/harness-suites/screen-observe/` is populated, drop `-SkipScreenObserveHarness` and re-run. The suite fixtures are not checked in — see [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md).
- [ ] *(optional)* `pwsh dev/preflight.ps1` for the heavier bootstrap path.

## Runtime Launch

- [ ] Run `dotnet run --project src/Thaddeus.Runtime/Thaddeus.Runtime.csproj`.
- [ ] Confirm runtime binds to `127.0.0.1`.
- [ ] Confirm workspace bootstrap loads with a per-launch token.
- [ ] Confirm `/api/health` or the workspace state surface reports healthy runtime state.

## Shell Launch

- [ ] Run `dotnet run --project src/Thaddeus.Shell/Thaddeus.Shell.csproj`.
- [ ] Confirm the shell starts or attaches to the runtime.
- [ ] Confirm the workspace window opens.
- [ ] Confirm closing the shell does not leave unexpected child processes.

## Workspace Navigation

- [ ] Home route loads.
- [ ] Chat route loads.
- [ ] Wiki route loads.
- [ ] Routines route loads.
- [ ] Memory route loads.
- [ ] History route loads.
- [ ] Activity route loads.
- [ ] Diagnostics route loads.
- [ ] Settings route loads.
- [ ] Settings tab bar switches: General, Models, Audio & Voice, Files, Location, Advanced.
- [ ] `/settings/audio` (or any legacy category URL) **redirects to** `/settings`.
- [ ] Theme picker (Settings → General → Appearance) toggles Light / Dark / System and persists across reload.
- [ ] Compact route is checked only as beta/minimal.

## Chat Smoke Test With Stub Assistant

- [ ] Configure or force the stub assistant path.
- [ ] Create a new thread.
- [ ] Send a simple message.
- [ ] Confirm the UI streams or displays a reply.
- [ ] Confirm the thread persists after navigation or refresh.

## Chat Smoke Test With Local/OpenAI-Compatible Model

- [ ] Start LM Studio, Ollama, or another compatible endpoint.
- [ ] Configure base URL and model ID in Settings.
- [ ] Run the settings endpoint/model test.
- [ ] Send a normal chat message.
- [ ] Confirm streamed reply quality is acceptable for release demo.
- [ ] Record skip reason if GPU/model resources are unavailable.

## Permission Prompt Test

- [ ] Reset or review permission policy so a prompt will appear.
- [ ] Ask for a tool-backed action.
- [ ] Confirm permission modal appears with: tool name, group icon, args preview, four buttons (Deny, Once, Session, Always).
- [ ] **Deny** path: tool does not run; reply explains it lacked the data.
- [ ] **Allow once** path: tool runs; next call re-prompts.
- [ ] **For session** path: tool runs; next call does not prompt.
- [ ] **Always** path: tool runs; the decision persists in `~/.thaddeus/runtime-settings.json`. Restart and confirm no re-prompt.
- [ ] Confirm persisted policy is visible or reflected in behavior.

## MCP Web Tool Test

- [ ] Ask a live web/search question.
- [ ] Confirm tool permission appears if required.
- [ ] Confirm answer includes useful source context.
- [ ] Source cards render below the reply with favicons, excerpts, and links.
- [ ] Tool-activity pills show `web_search started → completed` above the message.
- [ ] Activity page (`/activity`) lists the entry; clicking shows arguments and result.
- [ ] Record provider/network issues if live data is unavailable.

## MCP File/Document Tool Test

- [ ] Settings → Files → add an allowlisted root.
- [ ] Prepare a harmless local Markdown or text file.
- [ ] Ask Sir Thaddeus to read or summarize it.
- [ ] Confirm file permission prompt appears.
- [ ] Approve once or for session.
- [ ] Confirm output matches the file content.
- [ ] Try a path **outside** the allowlist → tool refuses; reply explains.
- [ ] Toggle "Disable all file access" → file_read refuses unconditionally.
- [ ] Repeat with a supported document type when available (PDF / DOCX / XLSX / CSV).

## Wiki CRUD Test

- [ ] Create a wiki root or use the default root.
- [ ] Create a folder.
- [ ] Create a page.
- [ ] Edit page content.
- [ ] Rename or move the page.
- [ ] Search for the page.
- [ ] Export and import a small wiki set.
- [ ] Delete page → goes to trash. Restore → returns. Purge → gone for good.

## Wiki Revisions Test

- [ ] Edit a wiki page more than once.
- [ ] Open revisions.
- [ ] Confirm prior revisions are visible.
- [ ] Restore or inspect a revision.

## Wiki Assistant Action Test

- [ ] Use page chat on a wiki page.
- [ ] Generate a draft from page context.
- [ ] Select text and run a rewrite action (Tighten / Clarify / More formal).
- [ ] Confirm results are useful and bounded to page context where expected.

## Routine CRUD And Run Test

- [ ] `/routines` → "+ New routine" creates a draft and routes to edit.
- [ ] Add 3-4 checklist items, set name + description, save.
- [ ] Disable via the inline toggle → moves under "Show disabled".
- [ ] Re-enable → moves back to default list.
- [ ] Run → checklist appears; check items; complete.
- [ ] History → completed run is recorded with timestamps and percentage.
- [ ] Delete routine → cascade-deletes runs.
- [ ] Confirm there is no claim or behavior implying scheduled unattended execution.

## Memo CRUD Test

- [ ] Create a memo with title, body (Markdown), and tags.
- [ ] Memo body renders as Markdown.
- [ ] Edit memo (title / body / tags) inline.
- [ ] Pin / unpin (pinned sorts to top).
- [ ] Delete memo.

## Activity And Diagnostics Test

- [ ] Open Activity after chat/tool/wiki/routine actions.
- [ ] Confirm recent events are visible.
- [ ] Click an entry → detail page with id, kind, status, started/completed, thread link if applicable.
- [ ] Open Diagnostics.
- [ ] Diagnostics shows: state, uptime, thread count, voice block, build version, PID, thread store path, **logs path**.
- [ ] Refresh → uptime increments.
- [ ] Check local logs or audit artifacts when needed.

## Stop-All And Kill Test

- [ ] Start a harmless long-running or sidecar-backed operation if available.
- [ ] During streaming, click **stop-all** (or hit `POST /api/stop-all`) → tool aborts; runtime stays up.
- [ ] Use **kill** only at the end of validation → runtime exits cleanly; webview closes (when shell-managed).
- [ ] Confirm no unexpected child processes remain (`Get-Process Thaddeus*`).

## Packaging Smoke Test

- [ ] Run `./dev/package-runtime.ps1 -Rids win-x64`.
- [ ] Run `./dev/release-package.ps1 -Runtime win-x64` when ready for a package candidate.
- [ ] Run `./dev/smoke-test.ps1 -SkipLaunch` for package structure validation.
- [ ] Launch the packaged runtime or shell manually on a Windows machine.
- [ ] Verify checksum files for release archives.
- [ ] *(optional)* Repeat for `osx-arm64` / `linux-x64` if cross-build agents are available.

## Beta Features — Checked Or Explicitly Skipped

For each item, mark **checked** (works on the test machine) or **skipped** (known not validated for v1.0):

- [ ] Voice ASR: PTT → mic → transcript appears in input. ☐ checked  ☐ skipped
- [ ] Voice TTS: speak-aloud button on a reply produces audio. ☐ checked  ☐ skipped
- [ ] Push-to-talk hotkey works while another window is focused. ☐ checked  ☐ skipped
- [ ] Tray menu items: At your service / Stand down / Dismiss. ☐ checked  ☐ skipped
- [ ] Compact panel renders without errors. ☐ checked  ☐ skipped
- [ ] Clipboard read/write tools (Windows). ☐ checked  ☐ skipped
- [ ] Screen capture / "what's on my screen" (Windows). ☐ checked  ☐ skipped

If any Beta item is broken in a way that would surprise a power user, either fix it before tag, or call it out in the release notes.

## README, Demo, And Docs

- [ ] [README.md](../README.md) describes the v1 product surface accurately.
- [ ] [V1_SCOPE.md](../V1_SCOPE.md) matches current release intent.
- [ ] [DEMO_SCRIPT.md](DEMO_SCRIPT.md) can be followed in 3-5 minutes.
- [ ] [KNOWN_LIMITATIONS.md](KNOWN_LIMITATIONS.md) is honest and current.
- [ ] [ROADMAP.md](ROADMAP.md) reinforces v1 scope.
- [ ] [ARCHITECTURE_PUBLIC.md](ARCHITECTURE_PUBLIC.md) matches [ARCHITECTURE.md](ARCHITECTURE.md).
- [ ] [FEATURE_GAP_MATRIX.md](FEATURE_GAP_MATRIX.md) reflects current completion status.
- [ ] [CHANGELOG.md](../CHANGELOG.md) has a v1.0 entry.
- [ ] All relative markdown links resolve.

---

## Sign-off

When every required row above is checked, the release captain signs and dates this checklist:

- Release captain: _________________________
- Build SHA: _________________________
- Tag: `v1.0.0`
- Date: _________________________

Attach the completed checklist to the release notes.
