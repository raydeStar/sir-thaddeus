# Meaningful Copilot Architecture

## Vision

A **local‑first, explicit‑permission copilot** that anyone can run for free, with a **paid service that performs heavy lifting, orchestration, and long‑running tasks** without compromising security or agency.

The product is deliberately split into:

- **Open Core (OSS)**: trust, safety, permissions, and local capability
- **Paid Services**: scale, persistence, reliability, and compute‑heavy or long‑running work

---

## Architectural Principle

> **Power lives locally. Burden lives in the service.**

Local mode should feel complete and respectful. Paid mode should feel like hiring a calm, tireless operator.

---

## Market Thesis (from the research)

The research supports three big claims:

1) **People want “agents,” but they don’t trust them yet.** Broad permissions + cloud black boxes create fear.
2) **Local-first is the credibility moat.** An assistant that works offline, with no telemetry by default, feels materially different.
3) **The first thing people will pay for is persistence.** Not “smarter,” but *tireless* (monitoring, scheduled checks, long-running jobs).

Practical translation: you win trust with the **OSS runtime**, then earn money with **workers that do boring persistence**.

---

## Repivot (based on what the data actually rewards)

Instead of trying to ship “a copilot that does everything,” ship **two products that interlock**:

### A) The Trust OS (OSS)
The desktop runtime + overlay + permission broker + audit log.

This is your *Home Assistant moment*: it’s the thing people can inspect, fork, and build on.

### B) The Tireless Worker (Paid)
Observation Workers + scheduling + notifications.

This is the first monetizable wedge because it delivers ongoing value while the user sleeps.

**Wedge to start with:** “Watchers” (stock/price/portal changes) + a simple UI to author JSON specs.

**What not to do early:** generalized web “do anything” autonomy. It’s harder, scarier, and gets you dragged into security/compliance swamp.

---

## System Layers (Always Present)

### 1. Local Runtime (OSS)

Runs entirely on the user’s machine.

Responsibilities:

- UI (PTT, hard off, overlay indicator)
- Permission broker + token issuance
- Tool execution sandbox
- Local automation (browser, screen read, OS actions)
- Local LLM support (optional)
- Audit log (local)

Guarantees:

- Works offline
- No telemetry by default
- No background execution when off

#### UI & UX (V0 — intentionally basic)

The UI must be *calm, explicit, and boring*. V0 optimizes for trust and clarity over style.

**UI Surfaces (V0)**

1) **Overlay Pill (always-on-top, corner anchored)**
   - Shows explicit states with icons + text:
     - Off / Idle
     - Listening (mic)
     - Thinking (spinner)
     - Reading Screen (eye)
     - Browser Control (cursor)
     - Service Working (cloud)
   - Click opens a small drawer:
     - Last 10 actions (audit feed)
     - “STOP ALL” (kills runtime + revokes permissions)
     - “Pause Service Jobs” (revokes service token)

2) **Command Palette (Ctrl+Space)**
   - Text entry for commands (“summarize this”, “create watcher”, “open reflex math”)
   - Shows the tool plan preview and requests permissions

3) **Push-to-Talk Hotkey (Hold-to-talk)**
   - Default: Ctrl+Alt+Space (configurable)
   - Release-to-send; Esc cancels; dedicated hotkey for “Silence” (cuts TTS)

4) **Minimal Settings Panel**
   - Hotkeys
   - Local model provider (optional)
   - Service connection (optional)
   - Privacy toggles (telemetry off by default)

**V0 Interaction Pattern (every action)**

1. User invokes (PTT or palette)
2. Copilot proposes a plan (tools + permissions)
3. User approves (time-boxed)
4. Tool runner executes
5. Result is returned + action is logged

**UX rule:** No silent mode. If it’s acting, the overlay says so.

---

### 2. Tool & Agent Framework (OSS)

Defines how work is described and executed.

Components:

- Tool schema definitions
- Agent state machine (request → plan → propose → execute)
- Time‑boxed permission tokens
- Safe scheduling primitives

**Important**: Agents may *propose* background or long‑running work, but **cannot persist or schedule without a backend**.

---

### 3. Service Connector (OSS)

A thin client that can optionally connect to paid services.

Responsibilities:

- Auth handshake (device‑bound)
- Encrypt job payloads
- Receive results/events
- Surface service activity in the local overlay

If disconnected:

- All local functionality still works
- Service‑only features gracefully degrade

---

## Paid Service Layers

### 4. Managed Agent Service (Paid)

This is where the hard things live.

Capabilities:

- Long‑running agents
- Scheduled jobs (cron‑like)
- Web monitoring
- Cross‑session memory
- Compute‑heavy reasoning
- Multi‑step workflows

Design rules:

- Service agents **never bypass local permission rules**
- All service actions must be mirrored to the local audit feed
- User can revoke service tokens instantly

---

### 5. Secure Observation Workers (Paid)

Used for tasks like:

- Checking stock on websites
- Monitoring price changes
- Watching for page updates
- Polling APIs or scraping HTML

Key properties:

- Headless, isolated workers
- Declarative observation specs
- No user credentials unless explicitly delegated
- Strict rate limits + robots compliance where applicable

Observation spec example (illustrative only):

```json
{
  "version": "1.0",
  "target": {
    "type": "web_page",
    "url": "https://store.example.com/item/123",
    "method": "GET"
  },
  "check": {
    "type": "text_contains",
    "value": "In Stock",
    "scope": "visible_text"
  },
  "schedule": {
    "interval": "30m",
    "jitter": "±5m"
  },
  "notify": {
    "on_match": ["local_notification"],
    "once": true
  },
  "limits": {
    "max_checks": 500,
    "expires_at": "2026-06-01T00:00:00Z"
  }
}
```json
{
  "target": "https://store.example.com/item/123",
  "signal": "text_contains",
  "value": "In Stock",
  "interval": "30m",
  "notify": "local"
}
````

---

### 6. Notification Bridge (Paid)

Routes events back to the user:

- Local notification
- Push (optional)
- Email (optional)

Notifications are **informational, not commanding**. No dark patterns.

---

## Local vs Paid Capability Matrix

| Capability             | Local (Free)  | Paid Service    |
| ---------------------- | ------------- | --------------- |
| Voice PTT              | ✓             | ✓               |
| Screen reading         | ✓             | ✓               |
| Browser automation     | ✓             | ✓               |
| Credential broker      | ✓             | ✓               |
| One‑shot tasks         | ✓             | ✓               |
| Long‑running agents    | ✗             | ✓               |
| Scheduled monitoring   | ✗             | ✓               |
| Cross‑session memory   | Limited/local | ✓               |
| Multi‑site observation | ✗             | ✓               |
| Heavy reasoning        | Local compute | Managed compute |

---

## Example: Store Stock Checker Agent

### Local‑Only Mode

User says: "Check if the blue jacket is in stock at Target."

Flow:

1. Copilot proposes opening browser and checking the page
2. User approves screen + browser access
3. Tool runner navigates and reads page
4. Copilot reports result
5. Session ends

Limitations:

- No background checks
- No alerts if status changes later

---

### Paid Mode (The Hard Thing)

User says: "Let me know when the blue jacket is back in stock."

Flow:

1. Copilot proposes creating an observation agent
2. UI explains: background checks every 30 minutes
3. User approves service job
4. Service spins up an observation worker
5. Worker checks page over time
6. When signal triggers, service notifies local client
7. Local overlay shows: "Item now in stock"

User can:

- Pause
- Modify interval
- Cancel permanently

---

## Observation Specification (OSS)

This section defines the **declarative Observation Spec** used by Secure Observation Workers. This spec is intentionally boring, constrained, and inspectable. It describes *what to watch*, *how to evaluate it*, and *what to do when it changes* — without encoding behavior or automation logic.

### Design Goals

- Declarative, not procedural
- Human‑readable and explainable
- Safe by default
- Portable (user owns the intent)
- Stable over time

### High‑Level Shape

```json
{
  "version": "1.0",
  "target": { /* what to observe */ },
  "check": { /* how to evaluate */ },
  "schedule": { /* how often */ },
  "notify": { /* what happens on match */ },
  "limits": { /* safety bounds */ }
}
```

---

### 1. target — What is being observed

```json
"target": {
  "type": "web_page",
  "url": "https://store.example.com/item/123",
  "method": "GET"
}
```

Supported target types (V1):

- `web_page` (HTML fetch)
- `api_endpoint` (JSON response)

Rules:

- No implicit authentication
- No cookies unless explicitly delegated
- No script execution required

---

### 2. check — What signal matters

```json
"check": {
  "type": "text_contains",
  "value": "In Stock",
  "scope": "visible_text"
}
```

Supported check types (V1):

- `text_contains`
- `text_not_contains`
- `regex_match`
- `json_path_equals`
- `json_path_exists`

Rules:

- Deterministic evaluation
- No ML inference in workers
- Same input must yield same result

---

### 3. schedule — How often it runs

```json
"schedule": {
  "interval": "30m",
  "jitter": "±5m"
}
```

Rules:

- Minimum interval enforced by service
- Random jitter required to avoid hammering

---

### 4. notify — What happens on match

```json
"notify": {
  "on_match": ["local_notification"],
  "once": true
}
```

Supported channels:

- `local_notification`
- `push`
- `email`

Rules:

- Informational only
- No action chaining

---

### 5. limits — Safety & fairness bounds

```json
"limits": {
  "max_checks": 500,
  "expires_at": "2026-06-01T00:00:00Z"
}
```

Rules:

- Hard stop conditions required
- Prevents infinite monitoring

---

### Complete Example (Stock Watch)

```json
{
  "version": "1.0",
  "target": {
    "type": "web_page",
    "url": "https://store.example.com/item/123",
    "method": "GET"
  },
  "check": {
    "type": "text_contains",
    "value": "In Stock",
    "scope": "visible_text"
  },
  "schedule": {
    "interval": "30m",
    "jitter": "±5m"
  },
  "notify": {
    "on_match": ["local_notification"],
    "once": true
  },
  "limits": {
    "max_checks": 500,
    "expires_at": "2026-06-01T00:00:00Z"
  }
}
```

---

### V1.1 Extension (recommended)

Add these fields without changing the core shape:

- **"trigger"**: `on_match | on_change | on_transition`
- **"state"**: `track_last_value: true` and `emit_on: ["first_match","changed"]`
- **"checks"** (plural): allow AND/OR composition (still deterministic)

This keeps the spec boring *and* makes it expressive enough for real monitoring.

---

### Why This Spec Matters

- Users can read and understand it
- Users can export and keep it
- Services can execute it safely
- Future systems can reuse it

This is *intent ownership*, not task outsourcing.

---

## Security & Trust Invariants

### Core Invariants

- No silent background work
- No credential exposure to LLMs
- All service actions are inspectable locally
- Local kill switch always overrides service
- Open source trust surfaces

### Agentic Authorization Bypass (the villain)

Agents become dangerous when they inherit broader or longer-lived permissions than the user invoking them.

**Risk intuition:**

- More permissions = more possible harm
- More autonomy = less chance to catch mistakes
- More duration = more time for something to go wrong

A useful “safety math” heuristic:

> **Security Risk ∝ Permissions × Autonomy × Duration**

This architecture reduces risk by:

- **Identity inheritance:** agents act with the user’s permissions, not “agent superpowers”
- **Time-boxed tokens:** short-lived, scoped authorization for each privileged action
- **Local revocation:** STOP ALL terminates the runtime and invalidates outstanding permission grants

### Permission Token (V1)

Permission is not a boolean; it is a **lease**.

```json
{
  "token_version": "1.0",
  "principal": "user:local",
  "issued_at": "2026-02-03T20:00:00Z",
  "expires_at": "2026-02-03T20:00:30Z",
  "capability": "SCREEN_READ",
  "scope": {
    "window": "active",
    "max_frames": 3
  },
  "purpose": "Summarize the article currently on screen",
  "nonce": "<random>",
  "revocable": true
}
```

Rules:
- Every tool call must present a valid, unexpired token
- Tokens are single-purpose and time-limited
- STOP ALL revokes all tokens immediately

### Audit Log (V1)

The audit log is the user’s “flight recorder.” It must be local-first, append-only, and human readable.

```json
{
  "event_version": "1.0",
  "ts": "2026-02-03T20:00:02Z",
  "actor": "runtime",
  "action": "BROWSER_NAVIGATE",
  "target": "https://store.example.com/item/123",
  "permission_token_id": "<hash>",
  "result": "ok",
  "details": {
    "domain": "store.example.com",
    "reason": "User requested stock check"
  }
}
```

---

## Monetization Philosophy

You charge for:

- Time
- Persistence
- Reliability
- Compute
- Convenience

You do **not** charge for:

- Trust
- Safety
- Basic agency

### Packaging (initial)

**Free / OSS**
- Trust OS runtime (overlay, PTT, permission broker)
- Tool SDK + plugin ecosystem
- Local audit log
- Observation spec authoring + validation

**Paid (subscription)**
- Observation Workers (watchers)
- Scheduler + retries + notification delivery
- Higher limits (more watchers, shorter intervals)
- Optional: managed compute for heavy tasks

**Enterprise / Pro (later)**
- Self-hosted worker service (on-prem)
- SSO, org policy, central audit export
- Support + SLAs

Upsell principle: local remains useful; paid makes it tireless.

---

## Implementation Plan (Phased)

### Repo / Module Layout (suggested)

Keep the trust surfaces open and the workers separable.

```
meaningful-copilot/
  apps/
    desktop-runtime/        # OSS: overlay, PTT, settings, command palette
    service-connector/      # OSS: client that talks to paid services
  packages/
    permission-broker/      # OSS: token issuance + revocation
    tool-runner/            # OSS: sandboxed execution + adapters
    observation-spec/       # OSS: schema + validators + explainers
    audit-log/              # OSS: event schema + storage
    ui-kit-basic/           # OSS: minimal components (later)
  services/
    observation-workers/    # Paid: polling engine + deterministic checks
    scheduler/              # Paid: queues + rate limiting
    notifier/               # Paid: push/email
  docs/
    DESIGN.md
    OSS_SCHEMA.md
    THREAT_MODEL.md
```

Design rule: service code can be proprietary; schemas and clients stay open.


### Phase 0 — Non‑Negotiables (OSS)

These are implemented first and gate all other features.

- **Hard Off Switch**

  - Terminates assistant process
  - Releases mic, screen, and input hooks
  - Unhooks global hotkeys
  - Blocks outbound network access for assistant

- **Active Overlay Indicator**

  - Always‑on‑top UI
  - Explicit states: Listening / Reading Screen / Browser Control / Service Working
  - Clickable status panel with last actions
  - Global **STOP ALL** button

If a feature cannot honor these, it does not ship.

---

### Phase 1 — Local Core (OSS, Complete Product)

1. **Invocation & Control**

   - Push‑to‑talk (hold to record)
   - Push‑to‑silence (cut TTS)
   - Typed command palette fallback

2. **Permission Broker**

   - Time‑boxed permission tokens
   - Explicit scope and purpose
   - Automatic invalidation on STOP ALL

3. **Tool Runner (Local)**

   - Executes only schema‑validated tool calls
   - Verifies permission token before execution
   - Emits local audit logs

4. **Observation Spec Authoring (Local)**

   - Create, validate, and explain Observation Specs
   - No background execution locally
   - Users fully own and export specs

---

### Phase 2 — First Paid Capability

5. **Observation Worker Service (Paid)**

   - Validates Observation Spec
   - Schedules polling with enforced limits
   - Evaluates checks deterministically
   - Emits match events only

6. **Service Connector (OSS)**

   - Encrypts and transmits specs
   - Receives service events
   - Mirrors service activity in overlay
   - Allows pause / modify / cancel

7. **Notification Bridge (Paid)**

   - Local notifications (V1)
   - Push / Email (future)

---

## Feature Admission Test (Governance Rule)

Before adding *any* new feature, it must pass all five questions:

1. **Does it require ambient or implicit access?**

   - If yes → reject or redesign

2. **Can it be expressed declaratively?**

   - If no → it does not belong in a spec

3. **Can a user explain what it does without trusting the system?**

   - If no → simplify

4. **Does it increase dependence rather than capability?**

   - If yes → pause

5. **If it fails at 2am, who suffers?**

   - User silently → unacceptable
   - Service / operator → acceptable

Only features that pass all five are eligible to ship.

---

## Roadmap Slice (V1)

1. Local runtime + overlay
2. Permission broker
3. Tool schema + browser automation
4. Observation Spec authoring + validation
5. One paid service: website observation
6. Event bridge back to local UI

Everything else builds on this.

---

## Guiding Ethos

> "If the user stops paying, the assistant should still feel respectful and useful — just not tireless."

