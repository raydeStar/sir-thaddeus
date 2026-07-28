# Modern Workbench Wedge 1 implementation contract

- **Status:** implemented and locally verified; protected PR verification pending
- **Baseline:** `6722ef18b5adcdedb5f7340e1dc493bfffdedf63`
- **Product-quality lane:** information architecture and conversation retrieval
- **Primary metric:** one canonical place to find and manage conversations
- **Rollback:** revert this wedge without changing thread files, runtime APIs,
  or assistant behavior

## Decision

Finish the first modern-workbench wedge by making `/chat` the canonical
conversation library. Preserve `/` as the quiet new-conversation state and
preserve existing `/chat/{threadId}` conversation URLs. Redirect the legacy
`/history` URL to `/chat`.

This closes the remaining overlap between Home, Chat, and History. It does not
introduce workspace persistence or change what a conversation contains.

## Existing capability audit

The production baseline already provides most of the required foundation:

- `IThreadStore` persists thread title and pin state in the existing JSON thread
  record.
- `GET /api/threads` returns summaries ordered by most recent update.
- `PATCH /api/threads/{id}` supports title and pin changes.
- `DELETE /api/threads/{id}` removes a thread.
- the former History route already implements title/preview search, persistent
  pinning, rename, delete, and recency groups;
- the command palette searches conversation titles and previews;
- the desktop sidebar is permanently labeled and already shows recent
  conversations;
- Home is already a focused new-conversation composer;
- conversation URLs, chat execution, memory, permissions, voice, Wiki context,
  work receipts, and the optional Wiki workbench are independent of the list
  route.

The missing product contract is consolidation. `/chat` and `/history` currently
present two different list experiences for the same underlying object, while
Home links to the legacy surface.

## User-visible contract

### Home

- `/` remains the new-conversation surface.
- Its compact recent list remains a convenience on narrow layouts.
- “View all” opens `/chat`.
- Starting a conversation continues to create a thread and navigate directly to
  `/chat/{threadId}`.

### Conversations

- `/chat` is titled **Conversations**.
- It provides one New conversation action.
- Search matches the locally available title and last-message preview.
- Pinned conversations appear first in a dedicated Pinned group.
- Unpinned conversations are grouped as Today, Yesterday, Previous 7 days,
  Previous 30 days, and Older.
- Each row supports open, pin/unpin, rename, and delete.
- Empty search and empty-library states are distinct.
- Mutation failures remain visible on the page; a failed mutation must not
  optimistically claim success.

### Legacy route

- `/history` redirects with replacement to `/chat`.
- Existing bookmarks do not produce a dead route or a second information
  architecture.

### Sidebar

- Pinned conversations receive a small dedicated section.
- Recent excludes pinned items and remains ordered by update time.
- “All conversations” opens `/chat`.
- The sidebar and conversation page read from the same frontend store so a pin,
  rename, delete, or new thread is reflected consistently.

## State and API contract

- No backend schema or endpoint changes.
- No migration: old thread JSON remains valid because `Pinned` is already
  optional/default-false.
- `useChatStore` is the frontend authority for the thread summary collection.
- Store mutations call the existing API and update the shared collection from
  the runtime-confirmed response after success.
- Deleting the active thread clears active-thread state; deleting another
  thread leaves the active conversation untouched.
- Search and grouping are deterministic presentation functions over thread
  summaries. They do not issue model calls or inspect message bodies.

## Accessibility and responsive contract

- Search has a visible label or accessible name.
- Row actions are keyboard reachable and become visible on `focus-within`, not
  hover alone.
- Pin state is exposed in button names and visible text/icon state.
- Delete confirmation names the target and states that deletion is not
  reversible.
- At narrow widths, row actions remain visible and wrapping must not create
  horizontal overflow.
- At desktop widths, the labeled sidebar remains stable; this wedge does not
  reintroduce hover expansion.

## Non-goals

- Workspace records, workspace-specific instructions, or an Unfiled migration.
- Full-message or semantic conversation search.
- Archive, folders, tags, bulk selection, or cloud synchronization.
- A generic artifact store or durable workbench version model.
- Changes to assistant routing, model selection, memory, permissions, safety,
  tools, Wiki storage, STT, TTS, or headless behavior.
- Restyling the adaptive workbench that was already validated in PRs 275–279.

## Acceptance tests

1. `/chat` creates a thread and opens `/chat/{threadId}`.
2. Two created threads can be renamed, pinned, searched, and deleted from
   `/chat`.
3. Pinning moves a thread into Pinned and removes it from recency groups.
4. Clearing search restores all groups.
5. `/history` replaces itself with `/chat`.
6. Home “View all” points to `/chat`.
7. Sidebar renders pinned and recent conversations without duplicating one
   thread in both sections.
8. A keyboard user can focus every row action and see which action is focused.
9. The conversation library has no horizontal overflow at a narrow viewport.
10. Existing chat, Wiki, permission, voice, receipt, and responsive-workbench
    focused tests remain green.

## Verification ladder

1. TypeScript typecheck and lint.
2. Focused Playwright conversation-library and adaptive-workbench scenarios.
3. Production web build.
4. `./dev/test.ps1`.
5. Protected PR check and post-merge `master` check if the wedge is published.

## Promotion and rollback

Promote when the acceptance tests and full repository gate pass with no
capability regression. The implementation is a frontend consolidation over
existing runtime contracts, so rollback is one code revert. Thread data written
before, during, or after the wedge remains compatible.

## Local verification evidence

Verified on 2026-07-27 from baseline
`6722ef18b5adcdedb5f7340e1dc493bfffdedf63`:

- `npm run typecheck`: passed;
- `npm run lint`: passed with zero warnings;
- `npx playwright test`: 32/32 scenarios passed;
- `./dev/test.ps1`: 2,844/2,844 .NET tests passed across five assemblies;
- production web and .NET builds: passed;
- visual desktop review: passed for hierarchy, pinned/recent separation,
  action discoverability, and conversation grouping;
- mobile browser coverage: passed for keyboard focus and horizontal overflow.

The optional screen-observe harness was not run because its fixture directory
was not present. NuGet vulnerability metadata also emitted a network warning;
the build itself completed with zero errors. Remote protected-branch checks
remain the final promotion gate.
