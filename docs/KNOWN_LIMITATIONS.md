# Sir Thaddeus — Known limitations (v1.0)

These are the boundaries we shipped on purpose. Each one is either a Beta
feature that needs more time on real machines, or a Deferred feature we
chose to leave for v1.1+ to keep the v1.0 scope honest.

If you hit one of these and assumed it was a bug, it's not — it's the line
we drew. If you think the line is in the wrong place, open an issue against
the relevant roadmap milestone in [`docs/ROADMAP.md`](ROADMAP.md).

---

## 1. Windows-first shell ergonomics

The full desktop experience — the supervised `Thaddeus.Shell`, embedded
webview, tray icon, "At your service / Stand down / Dismiss" menu, and
minimize-to-tray — is implemented and tested on **Windows only**.

The runtime itself (`Thaddeus.Runtime`) builds and runs on macOS and Linux
as a single-file binary. You can launch it from a terminal, open the printed
`http://127.0.0.1:<port>?access_token=...` URL in a browser, and you have
the workspace. What you don't have on macOS/Linux today:

- Tray menu
- Global push-to-talk hotkey registration
- Embedded webview wrapper
- Auto-launch on login

This is not a "coming soon" promise. v1 ships Windows-primary on purpose.
See [`docs/migration/non-transferrable-functionality.md`](migration/non-transferrable-functionality.md)
for the per-feature breakdown.

## 2. Voice depends on local sidecars, models, and your machine

Voice is **Beta** in v1. It works in the maintainer's setup; it has not been
validated across the long tail of mics, sound cards, sample rates, headsets,
and Bluetooth profiles you will throw at it.

Specifically:

- **ASR** uses Whisper.cpp via the `VoiceHost` sidecar. The bundled `base`
  model is fine for short utterances; longer or accented speech may need
  a larger model swapped in via Settings → Audio.
- **TTS** defaults to KokoroSharp (CPU). Piper is kept as a legacy fallback.
  Both are local; both depend on voice files that must be present on disk.
- **Push-to-talk** registers a global hotkey on Windows via
  `WindowsGlobalShortcutAdapter`. If another app already grabbed your chosen
  combo, registration silently fails — diagnose via Settings → Audio →
  "Check VoiceHost".
- **VoiceHost startup** can take 5–15 seconds on cold launch while models
  warm up. The first PTT press of a session may feel slow; subsequent
  presses are fast.

If voice is broken on your machine, **everything else still works**. v1.0
does not regress when voice is off.

## 3. Compact panel is a stub

The `/compact` route is a Phase-2 placeholder. It renders a small card with
a runtime-state badge and a "Press your global shortcut" hint. It does not
yet show transcript, PTT, or quick-interaction controls.

Treat `/compact` as a preview surface. Don't build workflows around it for v1.

## 4. Tray menu and global shortcuts need live Windows validation

The tray menu is wired and the icon is custom-branded as of 0.3.0
([`87a82aa`](../CHANGELOG.md)). Global shortcuts (PTT, Stop-all) register on
Windows. Both depend on:

- A live Windows shell (no headless CI validation).
- Your shortcut combo being free.
- Antivirus or security software not blocking the registration.

If the tray icon never appears, or the global PTT fires for the wrong
window, that's the seam we acknowledge here.

## 5. No scheduled or unattended automation

Sir Thaddeus does not run anything in the background on its own. There is
no scheduler, no `IHostedService` for routines, no cron-like trigger. The
v0.3.0 release explicitly removed the Automations feature for this reason —
it was drifting away from the trust model.

**Routines** is the v1 alternative: you press Run, you see a checklist, you
walk through it. There is a meta-test in the runtime test suite that asserts
no `IHostedService` is added under `Thaddeus.Runtime.Routines` — the "no
scheduler" property is enforced by code review.

If you need scheduled automation, v1 is the wrong tool. We have no plan to
add it back in v1.1 either.

## 6. No polished installer, no auto-update

v1.0 ships single-file binaries via `dev/package-runtime.ps1`. They run
without an installer; they update by replacement.

Specifically deferred:

- **Windows MSIX** — needs a manifest, signing cert, and the MSIX Packaging
  Tool.
- **macOS `.app` bundle + notarization** — needs an Apple Developer ID.
- **Linux `.desktop` integration / AppImage** — straightforward but unbuilt.
- **Auto-update channel** — no update server, no signing, no patcher.

Power users on day one launch the binary from a terminal or a Start-menu
shortcut they made themselves. A polished installer is a v1.1+ concern.

See [`docs/packaging.md`](packaging.md) for the current packaging path.

## 7. Cross-platform runtime exists before full desktop parity

The single-file `Thaddeus.Runtime` binary builds for `win-x64`, `osx-arm64`,
and `linux-x64`. It will host the workspace UI on any of them.

What is **not** at parity on macOS/Linux:

- The `Thaddeus.Shell` supervisor.
- Tray, global shortcuts, push-to-talk.
- Voice host startup paths (the Python sidecar runs, but install ergonomics
  are Windows-tested).

If you're running on Mac or Linux and you hit ergonomics gaps, that's
expected. The runtime is the contract; the shell is the comfort.

## 8. Local model quality depends on the model you chose

Sir Thaddeus does not ship with a model. It assumes you brought your own
local server (LM Studio, Ollama, or any OpenAI-compatible endpoint). The
quality of every chat reply, tool selection, and wiki rewrite is bounded
by your choice.

In particular:

- **Tool routing** improves significantly when a small fast "gatekeeper"
  model is configured to pre-classify each turn (Settings → Models →
  Verification model). This avoids feeding every tool to a big slow model
  on every prompt.
- **Streaming reliability** depends on your endpoint. LM Studio is our
  baseline; OpenAI-compatible community servers vary.
- **Function-calling correctness** depends on the model. Smaller models
  occasionally hallucinate "I can't do that" even when a tool is available;
  the imperative-tool path ("use web_search") was added in 0.3.0
  ([`b75e098`](../CHANGELOG.md)) to work around this.

If your model is bad at agentic workflows, Sir Thaddeus cannot fix that.

## 9. Web / live-data quality depends on providers and network

Web search, weather, places, and retailer fast-paths all hit external
services. Their freshness and correctness are bounded by:

- The provider's quota and rate limits.
- Your network reachability.
- Whether the provider changed their HTML / JSON shape this week.

We don't claim "real-time market data" or "guaranteed fresh news". When a
provider is down or rate-limited, the agent surfaces the failure rather
than fabricating an answer — that's by design.

## 10. Settings → Advanced → "Limits" are saved but not enforced

The Advanced settings tab exposes tool-budget fields (max tool calls per
turn, per session, etc.). v1.0 persists these values; the runtime does not
yet read them at gate time. The tab labels itself "Saved but not yet
enforced by the runtime" — believe the label.

This is a v1.1 finishing item.

## 11. Profile and personality admin are not in the workspace

The runtime API does not expose `/api/profile` or `/api/personalities`.
The headless terminal (`apps/headless-runtime`) still has profile and
personality endpoints; the v2 hybrid runtime that the workspace talks to
does not.

Practically: there is no UI to set a display name, alias, "about-me", or
to swap personalities from the workspace. Greeting prompts can't reflect
identity until v1.1 ships the endpoint and the UI.

## 12. Audit / activity is read-only

The Activity page lists chat turns, voice turns, routine runs, and tool
calls. You can click an entry for detail. v1.0 does **not** ship:

- Filter / search across activity.
- Bulk export of audit log.
- A separate admin pane for security review.

The audit log on disk (`~/.thaddeus/logs/audit.jsonl`) is the source of
truth; the page is convenience.

## 13. Screen-observe harness fixtures are not checked in

`dev/test.ps1` supports an optional **screen-observe harness** that
exercises the Windows screen-observation tools against pre-recorded
suites under `artifacts/harness-suites/screen-observe/`. Those suite
fixtures are not in-tree (they're generated by a separate workflow), so
running `dev/test.ps1` without `-SkipScreenObserveHarness` will fail with
"Suite directory not found" even on a clean machine where every other
test passed.

For v1.0 release validation, the canonical invocation is:

```powershell
pwsh dev/test.ps1 -Configuration Release -SkipScreenObserveHarness
```

This is documented in [`docs/RELEASE_CHECKLIST.md`](RELEASE_CHECKLIST.md).
Generating the screen-observe fixtures and pinning a hash for them is a
v1.1 task.

---

## Posture

These are not bugs. They are the line we drew so v1.0 ships honest. The
roadmap ([`docs/ROADMAP.md`](ROADMAP.md)) names the milestone where each
item moves.
