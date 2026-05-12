# Logging & turn traces

A short orientation for anyone — human or AI — who needs to answer **"why
did the assistant give that response?"** without grepping ten files.

## TL;DR — where to look

| Question | Where it lives | Format |
| --- | --- | --- |
| What was the final assistant text? | `<lockDir>/threads/<threadId>.json` | Per-thread JSON |
| Why did the runtime pick that route / those tools / that search provider? | `<lockDir>/turns/<messageId>.jsonl` | Per-turn JSONL |
| Is the runtime healthy? Where are paths? | `GET /api/diagnostics` | JSON |
| What permissioned actions ran? | `<lockDir>/audit.jsonl` | JSONL, rotated 10 MB × 5 |

`<lockDir>` is the directory holding the runtime lock file — usually
`%LOCALAPPDATA%\SirThaddeus\` in normal runs, or a per-test temp directory
under Playwright. Resolve it from `GET /api/diagnostics` (`turnsRoot`,
`logsRoot`, `threadStoreRoot`).

## Per-turn trace files

Every assistant reply produces one file: `<turnsRoot>/<messageId>.jsonl`.
Each line is the full event envelope that flew over the WebSocket while
the turn was happening, so the on-disk shape matches the real-time UI
exactly. Lines you'll see, in order:

| Event type | What it tells you |
| --- | --- |
| `chat.user.message` | The user prompt that started the turn (text, threadId, createdAt) |
| `chat.turn.start` | The turn began — message and thread ids, started-at timestamp |
| `chat.footman.decision` | Gatekeeper verdict: `nextState`, `confidence`, `reasonCode`, `toolsKept` / `toolsTotal` |
| `chat.memory.recalled` | Semantic memory retrieval surfaced ≥1 item: per-kind counts, `preview`, `durationMs`. Emitted only when something was actually pulled — absence in a trace means recall fired and returned empty. |
| `chat.tool.started` | A tool call begins — `tool`, `group`, `argsPreview` |
| `chat.tool.completed` | The matching completion — `ok`, `durationMs`, `resultSnippet`, `error` |
| `chat.turn.complete` | The final assembled assistant text plus any structured `sources` (citations) |

Streaming token deltas (`chat.turn.delta`) are intentionally **not**
written to disk — the assembled text is on `chat.turn.complete`, and
including every chunk would multiply file size by 30–100× with no
diagnostic value. Use the WebSocket if you want live deltas.

## API

| Route | Use |
| --- | --- |
| `GET /api/turns?limit=N` | List the most recent traces (newest first). N defaults to 50, max 500. Each entry has `messageId`, `threadId`, `modifiedAt`, `sizeBytes`, `eventCount`, `lastEventType`. |
| `GET /api/turns/{messageId}/trace` | Read one trace file as a parsed array of events. |
| `GET /api/diagnostics` | Includes `turnsRoot`, `logsRoot`, `threadStoreRoot` so callers can resolve paths without env-var sniffing. |

All routes use the standard bearer-token auth.

## UI surface

**Settings → Logs** is the user-facing entry point. It shows the three
durable paths (turn traces, chat threads, runtime logs) with copy-to-
clipboard buttons, and a list of the 25 most recent turns. Click a row
to inline-view the JSONL events that shaped that response.

The standalone **Diagnostics** page also surfaces `turnsRoot` next to
`logsRoot` for parity.

## End-to-end testing

Per-turn traces give Playwright a deterministic, post-hoc way to assert
on the *shape* of a turn without scraping WebSocket frames:

```ts
// After triggering a chat turn that should hit web search…
const list = await fetch(`${baseUrl}/api/turns?limit=5`, {
  headers: { Authorization: `Bearer ${token}` },
}).then((r) => r.json());

const turn = list.turns.find((t) => t.threadId === threadId);
const trace = await fetch(`${baseUrl}/api/turns/${turn.messageId}/trace`, {
  headers: { Authorization: `Bearer ${token}` },
}).then((r) => r.json());

const tools = trace.events
  .filter((e) => e.type === 'chat.tool.started')
  .map((e) => e.payload.tool);
expect(tools).toContain('web_search');
```

Because Playwright's `global-setup.ts` runs the runtime in `--test-mode`
with a unique lock directory per worker, traces are isolated to the test
run and don't bleed between runs.

## Tuning semantic-memory recall

If a turn that *should* have surfaced a stored fact didn't ("I told you I
like siamese cats yesterday and you didn't mention it"), the trace tells
you which of three things broke:

1. **No `chat.memory.recalled` event in the trace.** Retrieval found
   nothing relevant. Either the fact was never extracted in the first
   place (the user message that contained it didn't trigger
   `AutoMemoryExtractor`) or the scoring threshold gated it out. Cross-
   check the user's earlier turn's trace for `chat.tool.completed`
   entries from `memory_save_fact` / `memory_save_nugget`.
2. **Event present, but `preview` doesn't contain the expected fact.**
   Retrieval ran but the item didn't rank high enough to make the top-N
   pack. Tune the composite scoring weights in
   [`Scoring.cs`](packages/memory/SirThaddeus.Memory/Scoring.cs) — start
   by lowering the lexical-match threshold or raising the recency boost.
3. **Event present, fact in `preview`, but the model didn't use it.**
   The retrieval worked end-to-end; the model just chose not to surface
   what it was given. That's a prompt-engineering issue in
   `MemoryContextStep.AppendMemoryPackToSystemMessage` — try wrapping
   the `[REMEMBERED CONTEXT]` block in stronger directive language.

The **Memory Recall chip** above each assistant message in `/chat` shows
the same `chat.memory.recalled` payload live, so you can spot-check
during real conversations without opening the JSONL.

## Troubleshooting

- **Settings → Logs is empty.** No turns have completed yet — send a
  chat message. If still empty, `turnsRoot` may not exist; check
  `/api/diagnostics`.
- **A trace file exists but ends mid-turn.** The runtime crashed or the
  turn was cancelled. Look for the last `chat.tool.started` without a
  matching `chat.tool.completed` — that's where it stopped.
- **A correlation id wasn't safe.** The writer rejects message ids that
  contain anything other than `[A-Za-z0-9_-]` (path-traversal guard).
  ULID-shaped ids always pass.
