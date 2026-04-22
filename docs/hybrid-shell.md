# Sir Thaddeus v2 — Hybrid shell (Phase 1)

The hybrid shell is the new default surface for Sir Thaddeus, landing across
Phases 1–8 on the `task/hybrid-shell-phase1` branch. It is a single
self-contained `Thaddeus.Runtime` binary that hosts a React UI and a local
REST + WebSocket API on `127.0.0.1`.

## What's in v2

| Surface | Status |
|---|---|
| Threads + chat | ✅ Phase 4 |
| Voice + activity feed | ✅ Phase 5 |
| Settings (with secret masking) | ✅ Phase 6 |
| Memory (memos + tags + pin) | ✅ Phase 7.1 |
| Automations (CRUD + run + activity) | ✅ Phase 7.2 |
| Onboarding (4-step wizard + persisted flag) | ✅ Phase 8.1 |
| Self-contained packaging | ✅ Phase 8.2 |

## What's NOT in v2 yet

These remain in the legacy v1 surface (`apps/headless-runtime` and the older harness path):

- The full sprint harness (`tools/SirThaddeus.Harness`) and its stage suites.
- Push-to-talk system tray + global hotkey ergonomics.
- Action permission broker UI.
- Profiles / personality admin.
- Diagnostics / audit search panes.
- Anything that imports `SirThaddeus.Agent.*` or the legacy contracts pipeline.

The old Avalonia UI has been removed from the repo. The remaining legacy
terminal runtime stays only as a transitional harness surface and will be
retired after the harness fully targets the hybrid runtime.

## Build & run

```pwsh
# Run the runtime in dev mode (serves the prebuilt wwwroot bundle)
dotnet run --project src/Thaddeus.Runtime

# Or produce a self-contained single-file binary
pwsh dev/package-runtime.ps1 -Rids win-x64
```

See [docs/packaging.md](packaging.md) for the full packaging flow and the
deferred packaging gaps (MSIX, .app bundle, Linux desktop integration,
auto-update channel).

## Layout

- `src/Thaddeus.Runtime/` — ASP.NET Core minimal-API host.
- `src/Thaddeus.Runtime/wwwroot/` — built React bundle (synced from `web/dist/`).
- `web/` — React + TanStack Router source. `npm run build` then sync.
- `packages/shared-types/` — cs+ts shared DTOs (Settings, Memo, Automation, Activity, Voice).
- `tests/runtime/` — runtime xUnit tests (84/84 green at Phase 8.1).
- `web/tests/e2e/` — Playwright smoke tests (9/9 green at Phase 8.1).

## Security notes

- **Loopback only.** The runtime binds `127.0.0.1:<random-port>`. It is not
  reachable from other machines on the LAN.
- **Bearer token on every request.** Every `/api` and `/ws` call requires the
  per-launch bearer token printed at startup and embedded in the SPA bootstrap
  meta tags. SPA HTML itself (`GET /`) is the only unauthenticated route.
- **WebSocket auth uses `?access_token=`.** Browsers cannot set custom headers
  on WebSocket handshakes, so the bearer rides as a query parameter on `/ws`
  per [RFC 6750 §2.3](https://datatracker.ietf.org/doc/html/rfc6750#section-2.3).
  The middleware only honours `access_token` on the `/ws` path; data routes
  (`/api/*`) require the `Authorization: Bearer …` header.
- **Logged-URL caveat.** The default ASP.NET Core request log includes the
  query string, so the per-launch token will appear in `~/.thaddeus/logs/`.
  This is acceptable because the token rotates on every launch and the log
  files live on the same machine that printed the token. Operators who do
  not want the token in logs can set `Serilog__MinimumLevel__Override__Microsoft_AspNetCore=Warning`
  to suppress request logging entirely.

## Open items (Phase 9+)

- Cloud STT/TTS providers.
- Auto-update channel + signed installers (MSIX, .app, AppImage).
- macOS packaging verification (requires Mac host).
- Default Piper voice bundling.
- Pro diagnostics surface.
- Re-point the legacy harness at the hybrid runtime, then retire `apps/headless-runtime`.
