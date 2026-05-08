# Sir Thaddeus — v1.0 release checklist

Run this **end to end** on a representative machine before tagging v1.0.
Don't shortcut sections you "know" pass. The point of the checklist is to
catch the regressions you didn't expect.

> **Pass criterion:** every required box checked. Beta items may be skipped
> if explicitly noted, but they must be **explicitly skipped**, not silently.

---

## 0. Clean clone and build

- [ ] Fresh clone into an empty directory.
- [ ] `dotnet --info` shows .NET 10 SDK on PATH.
- [ ] `node --version` shows 18.x or compatible.
- [ ] `dotnet build SirThaddeus.sln` → 0 errors, 0 warnings.
- [ ] `cd web && npm install && npm run typecheck` → clean.
- [ ] `cd web && npm run build` → bundle in `web/dist/`.
- [ ] `pwsh dev/test.ps1 -Configuration Release -SkipScreenObserveHarness` →
      all green. Expected today: **2,457 passed, 0 failed, 0 skipped**.
      *(TRX results land in `artifacts/test-results/`.)*
- [ ] *(optional)* If `artifacts/harness-suites/screen-observe/` is
      populated, drop `-SkipScreenObserveHarness` and re-run. The suite
      fixtures are not checked in — see KNOWN_LIMITATIONS §13.
- [ ] `pwsh dev/preflight.ps1 -SkipBootstrap` → green for a faster gate
      (still runs the full Release test suite; skips the env-restore step).
- [ ] `cd web && npm run lint` → 0 errors, 0 warnings.

## 1. Runtime launches standalone

- [ ] `dotnet run --project src/Thaddeus.Runtime` prints a loopback URL
      including `?access_token=...`.
- [ ] Open that URL in a browser → workspace renders.
- [ ] Header shows runtime-state badge as **Ready** (green dot).
- [ ] Sidebar version badge reads `v0.3.0` (or whatever the release tag is).
- [ ] No errors in the runtime console during steady idle.

## 2. Shell launches and supervises the runtime *(Windows)*

- [ ] `dotnet run --project src/Thaddeus.Shell` opens an embedded webview.
- [ ] Webview lands on the workspace home; same runtime URL behind it.
- [ ] Closing the webview hides to tray (when "Minimize to tray on close"
      is enabled in Settings → General).
- [ ] Right-click tray → "At your service, sir" restores the window.
- [ ] Right-click tray → "Stand down" calls Stop-all (see §13).
- [ ] Right-click tray → "Dismiss" exits cleanly (no orphaned `Thaddeus.*`
      processes; verify with `Get-Process Thaddeus*`).

## 3. Workspace navigation

- [ ] All sidebar items reachable: Home, Chat, Wiki, History, Activity,
      Memory, Routines, Settings, Diagnostics.
- [ ] Settings tab bar renders and switches: General, Models, Audio &
      Voice, Files, Location, Advanced.
- [ ] `/settings/audio` (or any legacy category URL) **redirects to**
      `/settings`.
- [ ] Theme picker (Settings → General → Appearance) toggles Light / Dark /
      System and persists across reload.
- [ ] No 404 / blank routes anywhere reachable from the sidebar.

## 4. Chat smoke test — stub assistant

*(Use this if no model is configured yet; runs against the built-in stub.)*

- [ ] Settings → Models → leave "Test connection" failed; pick "Custom".
- [ ] New chat → send "hello".
- [ ] Stub reply streams in.
- [ ] Tool-activity area is empty (stub doesn't call tools).
- [ ] Thread persists; refresh browser → message is still there.

## 5. Chat smoke test — local / OpenAI-compatible model

- [ ] LM Studio (or chosen endpoint) running.
- [ ] Settings → Models → preset matches; **Test connection** returns model
      list.
- [ ] New chat → send "What's 13×17? Show your reasoning."
- [ ] Reply streams in. Math is correct.
- [ ] Refresh → message still present.

## 6. Permission prompt

- [ ] Send a prompt that needs a tool: e.g. "What's the latest stable
      release of .NET? Cite a source."
- [ ] Permission modal appears with: tool name, group icon, args preview,
      four buttons (Deny, Once, Session, Always).
- [ ] **Deny** path: tool does not run; reply explains it lacked the data.
- [ ] **Allow once** path: tool runs; next call re-prompts.
- [ ] **For session** path: tool runs; next call does not prompt.
- [ ] **Always** path: tool runs; the decision persists in
      `~/.thaddeus/runtime-settings.json` (verify on disk, then restart and
      confirm no re-prompt).

## 7. MCP web tool

- [ ] After approving `web_search`: source cards render below the reply
      with favicons, excerpts, and links.
- [ ] Tool-activity pills appear above the message, showing
      `web_search started → completed`.
- [ ] Activity page (`/activity`) lists the entry; clicking shows arguments
      and result.

## 8. MCP file / document tool

- [ ] Settings → Files → add an allowlisted root (e.g. project folder).
- [ ] Send "Read this file: README.md".
- [ ] Permission prompt for `file_read` appears; approve once.
- [ ] Reply contains content from the README.
- [ ] Try a path **outside** the allowlist → tool refuses; reply explains.
- [ ] Settings → Files → toggle "Disable all file access" → file_read
      refuses unconditionally.

## 9. Wiki CRUD

- [ ] `/wiki` → create root → create page in root.
- [ ] Edit page title and body; save → revision recorded.
- [ ] Refresh → state preserved.
- [ ] Delete page → goes to trash (not hard-deleted).
- [ ] Trash → restore page → page returns.
- [ ] Trash → purge → page is gone for good.

## 10. Wiki revisions

- [ ] On a page with multiple saves: revisions dropdown lists timestamped
      entries.
- [ ] Preview a prior revision → editor switches to read-only preview.
- [ ] Roll back → page content reverts; new revision recorded.

## 11. Wiki assistant actions

- [ ] **Page chat:** ask a question scoped to the current page; reply uses
      the page as context.
- [ ] **Draft:** prompt the assistant to draft a paragraph; result is
      inserted at the cursor.
- [ ] **Selected-text rewrite:** highlight a paragraph, choose Tighten /
      Clarify / More formal; selection is replaced.

## 12. Routine CRUD and run

- [ ] `/routines` → "+ New routine" creates a draft and routes to edit.
- [ ] Add 3–4 checklist items, set name + description, save.
- [ ] Disable via the inline toggle → moves under "Show disabled".
- [ ] Re-enable → moves back to default list.
- [ ] Run → checklist appears; check items; complete.
- [ ] History → completed run is recorded with timestamps and percentage.
- [ ] Delete routine → cascade-deletes runs.

## 13. Activity and diagnostics

- [ ] Activity page lists recent turns with kind, status, time.
- [ ] Click an entry → detail page with id, kind, status, started/completed,
      thread link if applicable, detail text.
- [ ] Diagnostics page shows: state, uptime, thread count, voice block,
      build version, PID, thread store path, **logs path**.
- [ ] Refresh → uptime increments.

## 14. Stop-all and kill

- [ ] Send a long prompt that triggers a tool call.
- [ ] During streaming, hover the kill switch (header). Tooltip explains.
- [ ] Click the kill switch → runtime exits cleanly; webview closes (when
      shell-managed).
- [ ] Restart → no stale state; thread is intact.
- [ ] *(Optional)* Hit `POST /api/stop-all` directly while a tool is in
      flight → tool is aborted, runtime stays up.

## 15. Packaging smoke test

- [ ] `pwsh dev/package-runtime.ps1 -Rids win-x64` produces
      `artifacts/publish/win-x64/Thaddeus.Runtime.exe`.
- [ ] Copy that exe to a different folder; double-click → workspace launches.
- [ ] Settings reachable, model probe works, basic chat works.
- [ ] Close the binary; no orphan processes.
- [ ] *(Optional)* Repeat for `osx-arm64` / `linux-x64` if cross-build agents
      are available; document any failures in `docs/packaging.md`.

## 16. Beta features — checked or explicitly skipped

For each item, mark **checked** (it works on the test machine) or **skipped**
(known not validated for v1.0):

- [ ] Voice ASR: PTT → mic → transcript appears in input.  ☐ checked  ☐ skipped
- [ ] Voice TTS: speak-aloud button on a reply produces audio.  ☐ checked  ☐ skipped
- [ ] Push-to-talk hotkey works while another window is focused.  ☐ checked  ☐ skipped
- [ ] Tray menu items behave per §2.  ☐ checked  ☐ skipped
- [ ] Compact panel renders without errors.  ☐ checked  ☐ skipped
- [ ] Clipboard read/write tools (Windows).  ☐ checked  ☐ skipped
- [ ] Screen capture / "what's on my screen" (Windows).  ☐ checked  ☐ skipped

If any Beta item is broken in a way that would surprise a power user,
either fix it before tag, or call it out in the release notes.

## 17. Docs and demo

- [ ] [`README.md`](../README.md) lists Core v1 features and labels Beta /
      Deferred clearly.
- [ ] [`V1_SCOPE.md`](../V1_SCOPE.md) accurately reflects what shipped.
- [ ] [`docs/DEMO_SCRIPT.md`](DEMO_SCRIPT.md) runs end-to-end without
      manual recovery.
- [ ] [`docs/KNOWN_LIMITATIONS.md`](KNOWN_LIMITATIONS.md) matches reality.
- [ ] [`docs/ROADMAP.md`](ROADMAP.md) reflects what's next, not aspirations.
- [ ] [`docs/ARCHITECTURE_PUBLIC.md`](ARCHITECTURE_PUBLIC.md) reflects the
      actual layer boundaries.
- [ ] [`CHANGELOG.md`](../CHANGELOG.md) has a v1.0 entry.

---

## Sign-off

When every required row above is checked, the release captain signs and
dates this checklist:

- Release captain: _________________________
- Build SHA: _________________________
- Tag: `v1.0.0`
- Date: _________________________

Attach the completed checklist to the release notes.
