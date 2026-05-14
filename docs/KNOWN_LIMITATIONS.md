# Known Limitations

Sir Thaddeus v1 is a credible power-user release, not a finished consumer distribution. These limitations are release boundaries, not excuses. They keep v1 honest and protect the project from accidental scope expansion.

## Windows-First Shell Ergonomics

The hybrid runtime and many packages are built with portability in mind, but the richest shell experience is Windows-first today. Tray behavior, global shortcuts, desktop observation hooks, and some shell workflows need live Windows validation before they should be described as broadly complete.

The runtime itself (`Thaddeus.Runtime`) builds and runs on macOS and Linux as a single-file binary. You can launch it from a terminal, open the printed `http://127.0.0.1:<port>?access_token=...` URL in a browser, and you have the workspace. What you don't have on macOS/Linux today is the Shell supervisor, tray menu, embedded webview wrapper, and global hotkey registration.

## Voice Is Beta

Voice / ASR / TTS depends on local sidecars, assets, models, drivers, and machine setup. The runtime can supervise and proxy voice services, but voice should not be the headline v1 promise.

If voice is broken on your machine, **everything else still works**. v1.0 does not regress when voice is off.

## Compact Panel Is Minimal

The compact route and launcher exist, but the active UI is a minimal idle/status surface. Treat it as beta until it has a complete quick-interaction workflow.

## Tray And Global Shortcuts Need Live Validation

Tray integration, push-to-talk, global shortcut hooks, stop-all shortcuts, and compact-panel shortcuts are present in the shell code. They still need live validation on the target Windows machines and should remain beta for v1.

## No Scheduled Unattended Automation

Manual routines and run history are part of v1. Scheduled automations and unattended background execution are deferred. Do not present routines as a scheduler.

A meta-test in the runtime test suite asserts no `IHostedService` is added under `Thaddeus.Runtime.Routines` — the "no scheduler" property is enforced by code review.

## No Polished Installer Or Auto-Update

The project has packaging scripts and can produce runnable artifacts, but v1 does not include a polished installer, signed MSIX, macOS app bundle, Linux desktop package, or auto-update channel.

Specifically deferred:

- **Windows MSIX** — needs a manifest, signing cert, and the MSIX Packaging Tool.
- **macOS `.app` bundle + notarization** — needs an Apple Developer ID.
- **Linux `.desktop` integration / AppImage** — straightforward but unbuilt.
- **Auto-update channel** — no update server, no signing, no patcher.

Power users on day one launch the binary from a terminal or a Start-menu shortcut they make themselves. A polished installer is a v1.1+ concern.

## Runtime Portability Comes Before Desktop Parity

The loopback runtime and MCP layers are broader than the current desktop UX. Cross-platform desktop parity is deferred until platform-specific shell packaging and ergonomics are validated.

## Model Quality Depends On The Configured Model

Sir Thaddeus can talk to LM Studio, Ollama's OpenAI-compatible shim, hosted OpenAI-compatible APIs, and custom endpoints. Tool reliability, instruction following, latency, and answer quality depend heavily on the chosen model and server health.

In particular:

- **Tool routing** improves significantly when a small fast "gatekeeper" model is configured to pre-classify each turn (Settings → Models → Verification model). This avoids feeding every tool to a big slow model on every prompt.
- **Streaming reliability** depends on your endpoint. LM Studio is the baseline; OpenAI-compatible community servers vary.
- **Function-calling correctness** depends on the model. Smaller models occasionally hallucinate "I can't do that" even when a tool is available; the imperative-tool path ("use web_search") was added to work around this.

## Web And Live Data Depend On Providers

Web search, page fetches, weather, place lookup, feeds, and other live-data tools depend on provider availability, network conditions, and rate limits. Demo and release checklists should include local file/document fallbacks.

When a provider is down or rate-limited, the agent surfaces the failure rather than fabricating an answer — that's by design.

## Settings → Advanced → Limits Are Saved But Not Enforced

The Advanced settings tab exposes tool-budget fields (max tool calls per turn, per session, etc.). v1.0 persists these values; the runtime does not yet read them at gate time. The tab labels itself "Saved but not yet enforced by the runtime" — believe the label.

This is a v1.1 finishing item.

## Profile And Personality Admin Are Not In The Workspace

The runtime API does not expose `/api/profile` or `/api/personalities`. The headless terminal (`apps/headless-runtime`) still has profile and personality endpoints; the v2 hybrid runtime that the workspace talks to does not.

Practically: there is no UI to set a display name, alias, "about-me", or to swap personalities from the workspace. Greeting prompts can't reflect identity until v1.1 ships the endpoint and the UI.

## Audit / Activity Is Read-Only

The Activity page lists chat turns, voice turns, routine runs, and tool calls. You can click an entry for detail. v1.0 does **not** ship:

- Filter / search across activity.
- Bulk export of audit log.
- A separate admin pane for security review.

The audit log on disk (`~/.thaddeus/logs/audit.jsonl`) is the source of truth; the page is convenience.

## Advanced Admin Surfaces Are Deferred

The current workspace has activity and diagnostics, but not an advanced audit-search/admin pane. Review deeper audit details through local logs and artifacts when needed.

## Screen-Observe Harness Fixtures Are Not Checked In

`dev/test.ps1` supports an optional **screen-observe harness** that exercises the Windows screen-observation tools against pre-recorded suites under `artifacts/harness-suites/screen-observe/`. Those suite fixtures are not in-tree (they're generated by a separate workflow). When the fixtures are absent, the test gate reports the harness as skipped instead of failing the otherwise valid .NET test run.

For v1.0 release validation, the canonical invocation is:

```powershell
pwsh dev/test.ps1 -Configuration Release
```

This is documented in [RELEASE_CHECKLIST.md](RELEASE_CHECKLIST.md). Generating the screen-observe fixtures and pinning a hash for them is a v1.1 task.

## Legacy Runtime Still Exists

[apps/headless-runtime/](../apps/headless-runtime/) remains for harness and transitional workflows. It should not be promoted as the public v1 product surface.

---

## Posture

These are not bugs. They are the line drawn so v1.0 ships honest. The roadmap ([ROADMAP.md](ROADMAP.md)) names the milestone where each item moves.
