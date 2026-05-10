# Flaky test register

Single source of truth for tests that have failed once but passed on
re-run with no code change in between. Each entry stays here until the
test is either fixed (race, missing wait, server-side bug) or deleted.

## Open

(none)

## Closed

### `web/tests/e2e/activity.smoke.spec.ts:17` — chat turn appears in the activity log and reaches Ok

**Observed:** 2026-05-09, during the post-v2-parity Playwright UX pass.

**Failure mode:**

```
Error: expect(locator).toBeVisible() failed
  Locator: getByTestId('activity-list')
  Expected: visible
  Timeout: 10000ms
  Error: element(s) not found
```

The test sent a chat message, navigated to `/activity`, and the page
rendered `[data-testid="activity-empty"]` ("No activity yet. Send a
message to populate the log.") instead of `[data-testid="activity-list"]`.
The chat message itself reached the server (the prior assertion
`chat-message-list contains "activity smoke"` passed).

**Triage:**

- 1st full-suite run: failed (this test only)
- Single-test rerun: passed (7.6s)
- 2nd full-suite run: passed (11/11, 28s)

So it's not a deterministic regression. Likely race between:

1. `POST /api/threads/{id}/messages` returning to the SPA, which is
   what the previous assertion waits on, and
2. `IActivityLog.Append(...)` running inline in the same handler so
   the entry is observable to the next `GET /api/activity?limit=100`.

When the test navigates to `/activity` immediately after the chat post
returns, the activity store calls `listActivity(100)` which goes to the
runtime's REST endpoint. If the test happened to win the race against
the C# task scheduler in [ChatApi.cs:133](../src/Thaddeus.Runtime/Api/ChatApi.cs)
(where `activity.Append` runs synchronously *after* `store.AppendMessageAsync`
and *before* the 201 response is written), the entry would already be
in the log. Theoretically.

In practice the activity store also subscribes to `/ws` for
`activity.appended` events. The empty-state render means BOTH the
initial REST snapshot and the subsequent WS event are missing — i.e.
the empty state was committed to React before either source landed.

**Likely root cause** (unverified): the React hook in
[`activity.index.tsx:17-19`](../web/src/routes/activity.index.tsx)
calls `connect()` once on mount; if the component renders
*before* the connect promise resolves, it commits an empty
state. The store's `loading` flag should prevent this — let me re-read:

```tsx
{loading && entries.length === 0 ? (
  <p data-testid="activity-loading">Loading…</p>
) : entries.length === 0 ? (
  <p data-testid="activity-empty">No activity yet…</p>
) : (
  <ul data-testid="activity-list">…</ul>
)}
```

Initial state is `loading: false`. `connect()` calls `set({ loading: true })`
inside `refresh()`. If the first render happens between `connect()` being
called and `set({ loading: true })` being committed, we render
`activity-empty`. Once `refresh()` resolves, we transition straight to
`activity-list` — but the test's 10s `toBeVisible` is an observation, so
once it sees `activity-empty` once it doesn't necessarily wait for the
later transition. Wait — actually `toBeVisible` polls, so it should
observe the eventual transition.

So the real candidate is: the activity store's WebSocket connection
plus REST snapshot both genuinely returned no entries. That would
happen if the chat handler's `activity.Append` ran *after* the test's
`listActivity` call and the WS event also arrived after the page
mounted. Both sources should be reliable, so this is a real race
window worth investigating.

**Fixed:** 2026-05-09. The chat store renders the user message
optimistically *before* the chat POST returns
([web/src/stores/chatStore.ts:143-156](../web/src/stores/chatStore.ts)),
so the "chat-message-list contains 'activity smoke'" gate was
satisfied while `activity.Append` was still pending server-side.

The fix waits for the assistant bubble (`[data-role="assistant"]`)
to appear before navigating to `/activity`. The assistant bubble
only renders after `chat.turn.start` arrives on the WS, which the
server only emits after appending the activity entry. Deterministic
gate, no race.

Verified: 1 single-test pass + 1 spec-only pass + 1 full-suite pass
all deterministic, and the test got faster (7.6s → 2.6s) because we
no longer rely on the 10s `toBeVisible` poll fallback.
