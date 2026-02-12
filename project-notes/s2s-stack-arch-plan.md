# Push‑To‑Talk Voice Assistant Plan (Fulloch‑style, Your Control Contract)

## Goal

Build a local-first voice loop that only listens when *you* ask, speaks when *you* allow, and can be silenced instantly.

**Non‑negotiables**

* **No wakeword. No background listening. Ever.**
* Listening happens **only** while the user is actively holding the mic button.

**Core UX contract**

* **Hold Mic** = listen/record
* **Release Mic** = send → AI responds
* **Shutup** = immediate stop (speaker + in-flight generation)
* **Mic press interrupts speech** (never talks over you)

---

## Phase 0 — Decide the “first runnable” slice

**Definition of Done (v0)**

* UI has **Mic Hold** button + **Shutup** button
* Records while held
* On release: runs ASR → LLM → TTS → plays audio
* Shutup stops audio instantly
* Pressing mic while speaking interrupts speech and starts recording

Keep everything else as TODO.

---

## Phase 1 — Architecture Skeleton (Orchestrator first)

### 1.1 Create the state machine

Implement a single orchestrator with explicit states:

* `Idle`
* `Listening` (PTT held; recording)
* `Transcribing` (ASR)
* `Planning` (LLM decides: speak-only vs tool calls)
* `Acting` (tool execution, if any)
* `Speaking` (TTS + playback)
* `Canceled` (transient)
* `Faulted` (service error; returns to Idle after surfacing message)

**Rules**

* Only one state at a time
* State transitions are explicit
* Every transition logs a short line for debugging

### 1.2 Introduce cancellation + exclusivity primitives

* `CancellationTokenSource` per **session** ("the one true red button")
* `SemaphoreSlim speechLock = new(1,1)` (one speaker at a time)
* `SemaphoreSlim micLock = new(1,1)` (one recorder at a time)
* `Channel<InputEvent>` (or equivalent) to serialize UI/hotkey events into the orchestrator

**Invariants**

* Speaker playback must respect a cancellation token.
* `Shutup` cancels the active token.
* `MicDown` also cancels the active token before opening mic.

---

## Phase 2 — UI + Input Wiring (Push-to-talk, not wakeword)

### 2.1 UI components

* Mic button supports **press-and-hold** (mouse down / mouse up)
* Shutup button (single click)
* Status indicator mirrors real pipeline states:

  * `Idle | Listening | Transcribing | Planning | Acting | Speaking | Faulted`

### 2.2 Input events

* `OnMicDown()`
* `OnMicUp()`
* `OnShutup()`

**Event behavior**

* `OnMicDown()`

  * Enqueue `MicDown`
* `OnMicUp()`

  * Enqueue `MicUp`
* `OnShutup()`

  * Enqueue `Shutup`

**Orchestrator loop responsibilities (the only place state may change)**

* On `MicDown`

  * `EndSession(reason: Interrupt)` if `Speaking/Planning/Acting/Transcribing`
  * Enter `Listening`
  * Start recording
* On `MicUp`

  * Stop recording
  * Enter `Transcribing` → call ASR
  * Enter `Planning` → call LLM (may decide tool calls)
  * If tool calls: Enter `Acting` → execute tools
  * Enter `Speaking` → call TTS + playback
  * On completion: `EndSession(reason: Complete)` → `Idle`
* On `Shutup`

  * `EndSession(reason: Shutup)` → `Idle`
* On any service error

  * `EndSession(reason: Fault)`
  * Enter `Faulted` (surface message) → return to `Idle`
* `OnShutup()`

  * Cancel everything
  * Stop playback immediately
  * Return to `Idle`

---

## Phase 3 — Audio Capture (record while held)

### 3.1 Choose capture library

* Use a reliable Windows audio capture lib (WASAPI wrapper, NAudio, etc.)

### 3.2 Capture format standard

* Record mono PCM
* e.g. 16kHz or 24kHz (match ASR expectations)
* Store in memory stream or temp WAV

### 3.3 Ring buffer (optional, v1+)

* Add a tiny pre-roll buffer (100–300ms) to avoid clipped first syllable.

---

## Phase 4 — ASR Service (speech → text)

### 4.1 Pick ASR backend

* Start with **Qwen3-ASR**

### 4.2 Run ASR as a local service

* Expose `POST /asr` that accepts audio (WAV/PCM) and returns text

### 4.3 Client integration

* On MicUp: send recorded audio to `/asr`
* Respect cancellation token

**Done criteria**

* Transcript shows in logs/UI

---

## Phase 5 — Routing (fast path + LLM path)

**Tool exposure rule (v0)**

* Don’t dump “all tools” into the LLM prompt.
* Instead: provide a **dynamic shortlist** based on the user’s utterance + current app context (screen, files, etc.).
* Keep the full tool registry server-side; show the model only what’s plausibly relevant.

(You can add accept/deny/allow-session/allow-always later—this plan assumes tools may be disabled entirely for v0.)

### 5.1 Fast-path commands (deterministic)

Before calling the LLM, check transcript against simple commands:

* “shutup” / “stop” (alias to Shutup)
* “repeat last” (optional)
* “volume up/down” (optional)

### 5.2 LLM route

* If not matched, send transcript to your LLM (Qwen SRS or your chosen router)
* LLM returns response text (and/or tool calls)

**Important**: for v0, tools can be disabled—just respond.

---

## Phase 6 — TTS + Playback (text → speech)

### 6.1 Pick TTS backend

* Start with **Kokoro**

### 6.2 Run TTS as a local service

* Expose `POST /tts` returns streamed PCM chunks

### 6.3 Playback pipeline

* Acquire `speechLock`
* Stream audio chunks to output
* Stop immediately when token cancels

**Done criteria**

* Speech starts within ~300–800ms after text is ready (streaming helps)

---

## Phase 7 — Interrupt Discipline (the “never talks over me” guarantee)

### 7.0 Single-threaded orchestration (required)

Run all state transitions through **one** orchestrator loop (event queue). This prevents race conditions like:

* Shutup + MicDown arriving mid-transition
* A late ASR/LLM/TTS response starting speech after cancel

**Rule:** the orchestrator loop is the **only** place allowed to change state.

### 7.1 Session identity + late-result rejection (required)

Maintain:

* `currentSessionId`
* `currentCts`

Every async operation must carry `sessionId` and must be discarded unless:

* `sessionId == currentSessionId`, and
* state is exactly what the result expects (e.g., ASR result only accepted in `Transcribing`).

This makes “barge-in” and “shutup” unbreakable even when services respond late.
Maintain a `currentSessionId` and `currentCts`.

### 7.2 Hard rules

* Starting `Listening` always cancels any `Responding`.
* `Shutup` cancels everything.
* Any pipeline stage checks `token.IsCancellationRequested` and exits.

### 7.3 Speaker stop is immediate

Do not wait for TTS to finish; stop audio device output instantly.

---

## Phase 8 — Observability (make debugging easy)

Add logs for:

* State transitions
* Session id creation/cancel
* Latencies: record duration, ASR time, LLM time, tool time, TTS time
* Cancel reasons: `Interrupt | Shutup | Complete | Fault | Timeout`
* Error paths (service down, bad audio, etc.)

Minimal UI:

* Status indicator (pipeline state)
* Optional: last transcript + last response text
* Optional: a small red banner for `Faulted` with the short error message

---

## Phase 9 — Packaging + Reliability

### 9.0 One cleanup path (required)

Implement a single `EndSession(reason)` function (called **only** by the orchestrator loop) that always:

* stops recording device
* stops playback device immediately
* cancels `currentCts`
* disposes session resources (streams/buffers)
* releases any held locks
* clears transient state (pending tasks, queued audio)

This prevents audio device lockups and “ghost speaking” after cancel.

### 9.1 Services lifecycle

* Decide: embed services via local processes, Docker, or native builds
* Orchestrator should:

  * detect if ASR/TTS is running
  * start them (optional)
  * show friendly error when missing

### 9.2 Timeouts + deadman switches (required)

Add explicit timeouts:

* ASR timeout (scaled by clip length)
* LLM timeout
* Tool execution timeout (per tool)
* TTS start timeout (time-to-first-audio)

Deadman rule examples:

* If `Speaking` receives no audio for X ms → cancel → `Faulted`
* If any stage exceeds timeout → cancel → `Faulted`

### 9.3 Config

* Set endpoints + model selection in config
* Provide safe defaults
* Add a single “safe mode” switch that disables tools entirely (v0 friendliness)
* Set endpoints + model selection in config
* Provide safe defaults

---

## Phase 10 — Extensions (after v0 works)

### 10.1 Video input (still audio output)

Two modes:

* **Snapshot on MicUp**: capture one frame and send alongside transcript
* **Short burst**: capture N frames over 1–2 seconds

### 10.2 Multimodal fusion

* Add `POST /vision` (frames → text)
* Combine: `transcript + vision summary` → LLM

### 10.3 Tool execution (Fulloch-style)

* Introduce “tool router” after transcript:

  * deterministic tool rules
  * LLM tool planner
  * execute tools
  * speak result

### 10.4 Quality upgrades

* VAD (voice activity detection) for better trimming
* Noise suppression
* Echo cancellation (if needed)

---

## “Cursor Implementation Order” (recommended)

1. Orchestrator state machine + cancel rules
2. UI press/hold + shutup wiring
3. Audio capture record/stop
4. ASR service + client
5. TTS service + playback + streaming
6. Latency logs + error handling
7. Fast-path commands
8. (Later) tools + multimodal

---

## Acceptance Tests (must pass)

* Holding Mic never records when not held.
* Releasing Mic triggers exactly one request.
* While speaking, pressing Mic stops speech immediately and begins recording.
* Shutup stops speech immediately and cancels in-flight work.
* No two speech playbacks overlap.

---

## TODOs (explicitly out of scope for v0)

* Wakeword  -- will never do
* Background listening -- will never do
* Multi-user profiles -- TODO
* Memory write/read policy 
* Encryption/vault
* Cloud connectors
* Full tool ecosystem
